using System.Collections.Immutable;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Kernel;

/// <summary>
/// The kernel HTTP gateway. Route handlers bind inputs and call the gateway; the
/// gateway owns dispatch, error mapping, and response serialization so no feature
/// logic remains in the route table.
/// </summary>
internal sealed class OperationGateway(
    OperationDispatcher dispatcher,
    ISecurityAuditSink audits,
    string hostInstanceId)
{
    public string HostInstanceId { get; } = hostInstanceId;

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

    public Task<IResult> ExecuteAsync<TRequest, TResponse>(
        HttpContext context,
        string operationId,
        TRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync<TRequest, TResponse>(
            context,
            operationId,
            _ => ValueTask.FromResult(request),
            cancellationToken);

    public async Task<IResult> ExecuteAsync<TRequest, TResponse>(
        HttpContext context,
        string operationId,
        Func<OperationExecutionContext, ValueTask<TRequest>> bind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bind);
        var execution = CreateExecution(context);
        TRequest request;
        try
        {
            request = await bind(execution);
        }
        catch (BadHttpRequestException)
        {
            throw;
        }
        catch (OperationBindingException exception)
        {
            return OperationResults.Render(exception.Result, execution);
        }

        var response = await dispatcher.DispatchAsync<TRequest, TResponse>(
            new OperationId(operationId),
            request,
            execution,
            cancellationToken);
        return OperationResults.Render(response, execution);
    }
}

/// <summary>
/// Thrown by route binding when the current protocol rejects a request before an
/// operation can be dispatched.
/// </summary>
internal sealed class OperationBindingException(OperationHttpResult result) : Exception
{
    public OperationHttpResult Result { get; } = result;
}

internal static class OperationResults
{
    public static IResult Render<TResponse>(
        OperationResponse<TResponse> response,
        OperationExecutionContext execution)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(execution);
        if (response.Error is not null)
        {
            return Render(
                execution.Result ?? OperationErrorPolicy.CreateResult(response.Error),
                execution);
        }

        return Render(
            execution.Result ?? new OperationHttpResult(
                StatusCodes.Status200OK,
                new JsonResponseBody(response.Value!)),
            execution);
    }

    public static IResult Render(OperationHttpResult result, OperationExecutionContext execution)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(execution);
        return result.Body switch
        {
            null => result.Location is null
                ? Results.StatusCode(result.StatusCode)
                : CreatedResult(result, null),
            ProblemResponseBody problem => Results.Problem(
                problem.Detail,
                statusCode: result.StatusCode),
            TextResponseBody text => Results.Text(text.Value, text.ContentType),
            ContentResponseBody content => RenderContent(content, execution),
            JsonResponseBody json => result.Location is null
                ? Results.Json(json.Value, statusCode: result.StatusCode)
                : CreatedResult(result, json.Value),
            _ => throw new InvalidOperationException("Unsupported operation response body.")
        };
    }

    private static IResult CreatedResult(OperationHttpResult result, object? value) =>
        result.StatusCode switch
        {
            StatusCodes.Status202Accepted => Results.Accepted(result.Location, value),
            _ => Results.Created(result.Location!, value)
        };

    private static IResult RenderContent(
        ContentResponseBody body,
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
