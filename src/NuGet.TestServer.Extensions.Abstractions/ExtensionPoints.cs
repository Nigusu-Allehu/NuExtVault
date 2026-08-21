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
