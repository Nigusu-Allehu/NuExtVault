using System.Collections.Immutable;

namespace NuExtVault.Extensions.Sdk;

internal interface IOperationHandler<in TRequest, TResponse>
{
    ValueTask<OperationResponse<TResponse>> HandleAsync(
        TRequest request,
        CancellationToken cancellationToken);
}

internal interface IOperationOwner<in TRequest, TResponse>
    : IOperationHandler<TRequest, TResponse>
{
    OperationId OperationId { get; }
}

/// <summary>
/// Creates typed operation owners from delegates. Owners receive the typed request and
/// a cancellation token only; they never receive kernel execution state.
/// </summary>
internal static class OperationOwner
{
    public static IOperationOwner<TRequest, TResponse> Create<TRequest, TResponse>(
        string operationId,
        Func<TRequest, CancellationToken, ValueTask<OperationResponse<TResponse>>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(handler);
        return new DelegateOwner<TRequest, TResponse>(new OperationId(operationId), handler);
    }

    private sealed class DelegateOwner<TRequest, TResponse>(
        OperationId operationId,
        Func<TRequest, CancellationToken, ValueTask<OperationResponse<TResponse>>> handler)
        : IOperationOwner<TRequest, TResponse>
    {
        public OperationId OperationId { get; } = operationId;

        public ValueTask<OperationResponse<TResponse>> HandleAsync(
            TRequest request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}

internal interface IOperationValidator<in TRequest>
{
    ValueTask<OperationError?> ValidateAsync(
        TRequest request,
        CancellationToken cancellationToken);
}

internal interface IDocumentContributor<TContribution>
{
    ValueTask<TContribution> ContributeAsync(
        DocumentContributionContext context,
        CancellationToken cancellationToken);
}

internal interface IDocumentContributor<in TContext, TContribution>
{
    ValueTask<TContribution> ContributeAsync(
        TContext context,
        CancellationToken cancellationToken);
}

internal sealed record DocumentContributorDescriptor(
    string Point,
    string Contract,
    string Namespace,
    int Priority,
    Type ContextType,
    Type ContributionType);

internal sealed record RegisteredDocumentContributor<TContext, TContribution>(
    string ExtensionId,
    string Namespace,
    int Priority,
    IDocumentContributor<TContext, TContribution> Contributor);

internal interface IDocumentContributorRegistry
{
    void Register<TContext, TContribution>(
        string extensionId,
        string point,
        string contract,
        string @namespace,
        int priority,
        IDocumentContributor<TContext, TContribution> contributor);
}

public interface IDocumentContributionSource
{
    internal ImmutableArray<RegisteredDocumentContributor<TContext, TContribution>>
        Get<TContext, TContribution>(string point, string contract) => [];
}

internal sealed record DocumentContributionContext(
    OperationId OperationId,
    string Slot,
    string Namespace);

internal interface IPolicyParticipant<in TContext>
{
    ValueTask<PolicyDecision> EvaluateAsync(
        TContext context,
        CancellationToken cancellationToken);
}

internal sealed record PolicyDecision(
    PolicyDecisionKind Kind,
    string? ReasonCode,
    PolicyDecisionEffect Effect = PolicyDecisionEffect.None,
    string? Detail = null);

internal enum PolicyDecisionKind
{
    Allow,
    Deny,
    Abstain
}

internal enum PolicyDecisionEffect
{
    None,
    Reject,
    Quarantine,
    Unauthorized,
    ResourceLimit
}

internal enum PolicyAggregationKind
{
    AllMustAllow,
    DenyOverrides
}

internal sealed record PolicyAggregationRequirement(
    PolicyAggregationKind Kind,
    ImmutableArray<string> RequiredAuthoritativeParticipants,
    int MinimumAuthoritativeParticipants,
    TimeSpan Timeout);

internal sealed record PolicyParticipantResult(
    string ParticipantId,
    bool IsAuthoritative,
    PolicyDecision Decision);

internal sealed record PolicyAggregationResult(
    PolicyDecision Decision,
    ImmutableArray<PolicyParticipantResult> Results,
    bool FailedClosed);
