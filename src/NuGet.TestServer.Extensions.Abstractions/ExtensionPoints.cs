namespace NuGet.TestServer.Extensions.Abstractions;

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
    string? ReasonCode);

internal enum PolicyDecisionKind
{
    Allow,
    Deny,
    Abstain
}
