using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Kernel;

internal static class PolicyParticipantAggregator
{
    public static async ValueTask<PolicyAggregationResult> EvaluateAsync<TContext>(
        string policyPoint,
        TContext context,
        IEnumerable<PolicyParticipantRegistration<TContext>> participants,
        PolicyAggregationRequirement requirement,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyPoint);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(requirement);
        cancellationToken.ThrowIfCancellationRequested();

        var selected = participants
            .Where(participant => string.Equals(
                participant.PolicyPoint,
                policyPoint,
                StringComparison.Ordinal))
            .OrderBy(participant => participant.ParticipantId, StringComparer.Ordinal)
            .ToArray();
        var authoritative = selected
            .Where(participant => participant.IsAuthoritative)
            .Select(participant => participant.ParticipantId)
            .ToHashSet(StringComparer.Ordinal);
        if (requirement.MinimumAuthoritativeParticipants <= 0 ||
            requirement.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requirement),
                "Policy participant count and timeout must be positive.");
        }

        if (authoritative.Count < requirement.MinimumAuthoritativeParticipants ||
            requirement.RequiredAuthoritativeParticipants.Any(id => !authoritative.Contains(id)))
        {
            return FailClosed("policy.required-participant-missing", []);
        }

        var results = ImmutableArray.CreateBuilder<PolicyParticipantResult>(selected.Length);
        foreach (var registration in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(requirement.Timeout);
            PolicyDecision decision;
            try
            {
                var evaluation = registration.Participant
                    .EvaluateAsync(context, timeout.Token)
                    .AsTask();
                decision = await evaluation.WaitAsync(
                    requirement.Timeout,
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                await timeout.CancelAsync();
                return FailClosed("policy.participant-timeout", results.ToImmutable());
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return FailClosed("policy.participant-timeout", results.ToImmutable());
            }
            catch (Exception exception)
            {
                return FailClosed(
                    "policy.participant-failed",
                    results.ToImmutable(),
                    exception.Message);
            }

            results.Add(new(
                registration.ParticipantId,
                registration.IsAuthoritative,
                decision));
        }

        var completed = results.ToImmutable();
        var denial = completed.FirstOrDefault(result =>
            result.Decision.Kind == PolicyDecisionKind.Deny);
        if (denial is not null)
        {
            return new(denial.Decision, completed, FailedClosed: false);
        }

        if (requirement.Kind == PolicyAggregationKind.AllMustAllow &&
            completed.Any(result =>
                result.IsAuthoritative &&
                result.Decision.Kind != PolicyDecisionKind.Allow))
        {
            return FailClosed("policy.authoritative-participant-abstained", completed);
        }

        return new(new PolicyDecision(PolicyDecisionKind.Allow, null), completed, FailedClosed: false);
    }

    private static PolicyAggregationResult FailClosed(
        string reasonCode,
        ImmutableArray<PolicyParticipantResult> results,
        string? detail = null) =>
        new(
            new PolicyDecision(PolicyDecisionKind.Deny, reasonCode, Detail: detail),
            results,
            FailedClosed: true);
}
