using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Kernel;

/// <summary>
/// Base class for built-in operation owners. It implements the pre-compatibility
/// <see cref="IOperationOwner{TRequest,TResponse}"/> contract and adds the
/// kernel-only execution context used for content handles, caller facts, and
/// protocol-compatible rendering.
/// </summary>
internal abstract class BuiltInOperationOwner<TRequest, TResponse>
    : IOperationOwner<TRequest, TResponse>, IContextualOperationOwner<TRequest, TResponse>
{
    protected BuiltInOperationOwner(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        OperationId = new OperationId(operationId);
    }

    public OperationId OperationId { get; }

    public ValueTask<OperationResponse<TResponse>> HandleAsync(
        TRequest request,
        CancellationToken cancellationToken) =>
        HandleAsync(request, OperationExecutionContext.CreateDetached(), cancellationToken);

    public abstract ValueTask<OperationResponse<TResponse>> HandleAsync(
        TRequest request,
        OperationExecutionContext context,
        CancellationToken cancellationToken);
}

internal interface IContextualOperationOwner<in TRequest, TResponse>
{
    ValueTask<OperationResponse<TResponse>> HandleAsync(
        TRequest request,
        OperationExecutionContext context,
        CancellationToken cancellationToken);
}

internal sealed class DelegateOperationOwner<TRequest, TResponse>(
    string operationId,
    Func<TRequest, OperationExecutionContext, CancellationToken, ValueTask<OperationResponse<TResponse>>> handler)
    : BuiltInOperationOwner<TRequest, TResponse>(operationId)
{
    private readonly Func<TRequest, OperationExecutionContext, CancellationToken,
        ValueTask<OperationResponse<TResponse>>> _handler =
        handler ?? throw new ArgumentNullException(nameof(handler));

    public override ValueTask<OperationResponse<TResponse>> HandleAsync(
        TRequest request,
        OperationExecutionContext context,
        CancellationToken cancellationToken) =>
        _handler(request, context, cancellationToken);
}
