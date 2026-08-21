using System.Collections.Immutable;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Routing;

namespace NuGet.TestServer.Kernel;

/// <summary>
/// The kernel HTTP gateway. The generated route table calls the gateway; the gateway
/// owns binding, dispatch, error mapping, and response serialization so no feature
/// logic remains in the route table.
/// </summary>
internal sealed class OperationGateway(
    OperationDispatcher dispatcher,
    ISecurityAuditSink audits,
    string hostInstanceId,
    KernelRequestInstrumentation instrumentation,
    KernelUrlProjector urls,
    TransportSecurityOptions transport)
{
    public string HostInstanceId { get; } = hostInstanceId;

    public Task InstrumentAsync(HttpContext context, RequestDelegate next) =>
        instrumentation.InvokeAsync(context, next);

    public OperationExecutionContext CreateExecution(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new OperationExecutionContext(
            HostInstanceId,
            new HttpOperationAuthorization(context, audits))
        {
            RequestPath = context.Request.Path.Value
        };
    }

    /// <summary>
    /// Executes one generated route: binds the request through the descriptor handler,
    /// dispatches the declared operation, and renders the protocol-compatible response.
    /// </summary>
    public async Task<IResult> ExecuteEndpointAsync(
        KernelRouteEndpoint endpoint,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(context);
        var execution = CreateExecution(context);
        var request = new HttpEndpointRequest(context, execution, endpoint.Limits);
        if (endpoint.Descriptor.Body.Kind != EndpointBodyKind.None &&
            endpoint.Limits.MaxRequestBytes > 0)
        {
            request.LimitRequestBody(endpoint.Limits.MaxRequestBytes);
        }

        EndpointInvocation invocation;
        try
        {
            invocation = await endpoint.Descriptor.Handler.BindAsync(request, cancellationToken);
        }
        catch (OperationBindingException exception)
        {
            return OperationResults.Render(
                exception.Result,
                execution,
                urls,
                () => PublicUrlOrigin.FromRequest(context, transport));
        }

        var result = await invocation.ExecuteAsync(
            new EndpointDispatcher(dispatcher, execution),
            cancellationToken);
        return OperationResults.Render(
            result,
            execution,
            urls,
            () => PublicUrlOrigin.FromRequest(context, transport));
    }

    private sealed class EndpointDispatcher(
        OperationDispatcher dispatcher,
        OperationExecutionContext execution) : IEndpointOperationDispatcher
    {
        public async ValueTask<OperationResult> DispatchAsync<TRequest, TResponse>(
            string operationId,
            TRequest request,
            CancellationToken cancellationToken)
        {
            var response = await dispatcher.DispatchAsync<TRequest, TResponse>(
                new OperationId(operationId),
                request,
                execution,
                cancellationToken);
            return OperationResults.Resolve(response, execution);
        }
    }
}

/// <summary>
/// The kernel renderer. It is the only component that maps a transport-neutral
/// <see cref="OperationResult"/> onto HTTP status codes, headers, and serialization.
/// </summary>
internal static class OperationResults
{
    public static IResult Render<TResponse>(
        OperationResponse<TResponse> response,
        OperationExecutionContext execution,
        KernelUrlProjector urls,
        Func<PublicUrlOrigin> origin) =>
        Render(Resolve(response, execution), execution, urls, origin);

    /// <summary>
    /// Chooses the protocol-compatible rendering for an operation response: the
    /// rendering the owner attached to the response, then the kernel-internal execution
    /// rendering, then the kernel error or success policy.
    /// </summary>
    public static OperationResult Resolve<TResponse>(
        OperationResponse<TResponse> response,
        OperationExecutionContext execution)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(execution);
        if (response.Error is not null)
        {
            return response.Rendering ??
                   execution.Result ??
                   OperationErrorPolicy.CreateResult(response.Error);
        }

        return response.Rendering ??
               execution.Result ??
               OperationResult.Ok(response.Value!);
    }

    public static IResult Render(
        OperationResult result,
        OperationExecutionContext execution,
        KernelUrlProjector urls,
        Func<PublicUrlOrigin> origin)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(execution);
        var statusCode = OperationErrorPolicy.GetStatusCode(result.Status);
        return result.Body switch
        {
            null => result.Location is null
                ? Results.StatusCode(statusCode)
                : CreatedResult(result, null),
            OperationProblemBody problem => Results.Problem(
                problem.Detail,
                statusCode: statusCode),
            OperationTextBody text => Results.Text(text.Value, text.MediaType),
            OperationContentBody content => RenderContent(content, execution),
            OperationDocumentBody document => result.Location is null
                ? Results.Json(
                    document.Document,
                    urls.CreateJsonOptions(origin),
                    statusCode: statusCode)
                : CreatedResult(result, document.Document),
            _ => throw new InvalidOperationException("Unsupported operation response body.")
        };
    }

    private static IResult CreatedResult(OperationResult result, object? value) =>
        result.Status switch
        {
            OperationResultStatus.Accepted => Results.Accepted(result.Location, value),
            _ => Results.Created(result.Location!, value)
        };

    private static IResult RenderContent(
        OperationContentBody body,
        OperationExecutionContext execution)
    {
        var content = execution.Content.Resolve(body.Handle);
        if (content.Stream is not null)
        {
            return Results.File(
                content.Stream,
                content.ContentType,
                enableRangeProcessing: content.SupportsRanges);
        }

        if (content.Bytes is not null)
        {
            return Results.Bytes(content.Bytes.Value, content.ContentType);
        }

        throw new InvalidOperationException(
            $"Content handle '{body.Handle.Id}' has no readable payload.");
    }
}

/// <summary>
/// Route metadata that names the operations a route dispatches. It makes the mapping
/// from the current URL surface to declared operations verifiable.
/// </summary>
internal sealed class OperationRouteMetadata(params string[] operationIds)
{
    public ImmutableArray<string> OperationIds { get; } = [.. operationIds];
}
