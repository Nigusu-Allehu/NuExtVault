using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Kernel.Routing;

/// <summary>
/// Kernel-owned caller facts for one request. Descriptor binders never see the HTTP
/// request, the security configuration, or the audit sink.
/// </summary>
internal sealed record EndpointCaller(bool HasIdentity, string? IdentityName, bool IsAdministrator)
{
    public string IdentityOr(string fallback) =>
        string.IsNullOrEmpty(IdentityName) ? fallback : IdentityName;
}

/// <summary>
/// The transport-neutral request surface a descriptor binder may use. Implementations
/// are kernel-internal; a binder can read declared route and query values, bind bounded
/// bodies, and register non-buffering content streams, but it can never reach
/// <c>HttpContext</c>, dependency injection, or endpoint routing.
/// </summary>
internal abstract class EndpointRequest
{
    /// <summary>The uppercase HTTP method of the current request.</summary>
    public abstract string Method { get; }

    /// <summary>The request path of the current request.</summary>
    public abstract string Path { get; }

    public abstract string? ContentType { get; }

    public abstract long? ContentLength { get; }

    public abstract bool HasFormContent { get; }

    /// <summary>Effective, host-resolved limits for the current endpoint.</summary>
    public abstract EndpointLimits Limits { get; }

    public abstract EndpointCaller Caller { get; }

    public abstract string GetRoute(string name);

    public abstract string? GetQuery(string name);

    public abstract int? GetQueryInt32(string name);

    public abstract bool? GetQueryBoolean(string name);

    /// <summary>
    /// Registers the unbuffered request body as kernel content.
    /// </summary>
    public abstract StreamHandle BindBodyStream();

    /// <summary>
    /// Registers an uploaded package or symbol package as kernel content. Multipart
    /// payloads are streamed; nothing is buffered by the gateway.
    /// </summary>
    public abstract ValueTask<StreamHandle> BindUploadAsync(
        string missingFileDetail,
        CancellationToken cancellationToken);

    public abstract StreamHandle RegisterContent(ReadOnlyMemory<byte> content, string contentType);

    /// <summary>
    /// Reads a required JSON body, rejecting unsupported media types and malformed
    /// payloads exactly as the previous minimal-API binding did.
    /// </summary>
    public abstract ValueTask<TBody> ReadRequiredJsonAsync<TBody>(CancellationToken cancellationToken);

    /// <summary>
    /// Reads an optional JSON body, returning <c>null</c> when the payload is malformed
    /// so the binder can choose its own protocol response.
    /// </summary>
    public abstract ValueTask<TBody?> ReadOptionalJsonAsync<TBody>(
        CancellationToken cancellationToken)
        where TBody : class;

    /// <summary>
    /// Applies a request-body limit that is narrower than the endpoint default.
    /// </summary>
    public abstract void LimitRequestBody(long maximumBytes);
}

/// <summary>
/// Kernel dispatch surface handed to a descriptor invocation. It carries no transport
/// types so the same invocation can run in-process today and out-of-process later.
/// </summary>
internal interface IEndpointOperationDispatcher
{
    ValueTask<OperationHttpResult> DispatchAsync<TRequest, TResponse>(
        string operationId,
        TRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of binding a request: either a typed operation dispatch or a
/// protocol-compatible short-circuit result.
/// </summary>
internal sealed class EndpointInvocation
{
    private readonly OperationHttpResult? _result;
    private readonly Func<
        IEndpointOperationDispatcher,
        CancellationToken,
        ValueTask<OperationHttpResult>>? _dispatch;

    private EndpointInvocation(
        OperationHttpResult? result,
        Func<IEndpointOperationDispatcher, CancellationToken, ValueTask<OperationHttpResult>>?
            dispatch)
    {
        _result = result;
        _dispatch = dispatch;
    }

    public static EndpointInvocation Result(OperationHttpResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new EndpointInvocation(result, null);
    }

    public static EndpointInvocation Operation<TRequest, TResponse>(
        string operationId,
        TRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(request);
        return new EndpointInvocation(
            null,
            (dispatcher, token) =>
                dispatcher.DispatchAsync<TRequest, TResponse>(operationId, request, token));
    }

    public ValueTask<OperationHttpResult> ExecuteAsync(
        IEndpointOperationDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return _result is not null
            ? ValueTask.FromResult(_result)
            : _dispatch!(dispatcher, cancellationToken);
    }
}

/// <summary>
/// Binds one request into an operation dispatch. Handlers are declared by descriptors
/// and are the only extension-authored code the gateway calls before dispatch.
/// </summary>
internal interface IEndpointHandler
{
    ValueTask<EndpointInvocation> BindAsync(
        EndpointRequest request,
        CancellationToken cancellationToken);
}

internal delegate ValueTask<EndpointInvocation> EndpointBinder(
    EndpointRequest request,
    CancellationToken cancellationToken);

internal static class EndpointHandler
{
    public static IEndpointHandler Create(EndpointBinder binder) => new DelegateHandler(binder);

    /// <summary>
    /// Creates a handler that binds a typed request without inspecting the payload.
    /// </summary>
    public static IEndpointHandler Create<TRequest, TResponse>(
        string operationId,
        Func<EndpointRequest, TRequest> bind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(bind);
        return new DelegateHandler((request, _) => ValueTask.FromResult(
            EndpointInvocation.Operation<TRequest, TResponse>(operationId, bind(request))));
    }

    private sealed class DelegateHandler(EndpointBinder binder) : IEndpointHandler
    {
        private readonly EndpointBinder _binder =
            binder ?? throw new ArgumentNullException(nameof(binder));

        public ValueTask<EndpointInvocation> BindAsync(
            EndpointRequest request,
            CancellationToken cancellationToken) => _binder(request, cancellationToken);
    }
}
