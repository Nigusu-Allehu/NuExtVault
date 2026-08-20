using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Kernel;

/// <summary>
/// Protocol-compatible rendering of an operation result. Owners describe the
/// response; the kernel gateway owns serialization, headers, and status codes.
/// </summary>
internal sealed record OperationHttpResult(
    int StatusCode,
    OperationResponseBody? Body = null,
    string? Location = null);

internal abstract record OperationResponseBody;

internal sealed record JsonResponseBody(object Value) : OperationResponseBody;

internal sealed record TextResponseBody(string Value, string ContentType) : OperationResponseBody;

internal sealed record ProblemResponseBody(string Detail) : OperationResponseBody;

internal sealed record ContentResponseBody(StreamHandle Handle) : OperationResponseBody;

/// <summary>
/// Kernel error classification and transport mapping. Owners return typed errors and
/// never choose status codes.
/// </summary>
internal static class OperationErrorPolicy
{
    public static int GetStatusCode(OperationErrorKind kind) => kind switch
    {
        OperationErrorKind.InvalidRequest => StatusCodes.Status400BadRequest,
        OperationErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        OperationErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        OperationErrorKind.NotFound => StatusCodes.Status404NotFound,
        OperationErrorKind.Conflict => StatusCodes.Status409Conflict,
        OperationErrorKind.LimitExceeded => StatusCodes.Status413PayloadTooLarge,
        OperationErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    public static OperationHttpResult CreateResult(OperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var statusCode = GetStatusCode(error.Kind);
        return error.Kind switch
        {
            OperationErrorKind.NotFound => new OperationHttpResult(statusCode),
            OperationErrorKind.Unauthorized => new OperationHttpResult(statusCode),
            OperationErrorKind.Forbidden => new OperationHttpResult(statusCode),
            _ => new OperationHttpResult(statusCode, new ProblemResponseBody(error.Message))
        };
    }

    public static OperationError NotFound(string message) =>
        new(OperationErrorCodes.NotFound, message, null);

    public static OperationError InvalidRequest(string message) =>
        new(OperationErrorCodes.InvalidRequest, message, null);

    public static OperationError Conflict(string message) =>
        new(OperationErrorCodes.Conflict, message, null);

    public static OperationError PolicyDenied(string message) =>
        new(OperationErrorCodes.PolicyDenied, message, null);

    public static OperationError LimitExceeded(string message) =>
        new(OperationErrorCodes.LimitExceeded, message, null);

    public static OperationError Unavailable(string message) =>
        new(OperationErrorCodes.Unavailable, message, null);

    public static OperationError Internal(string message) =>
        new(OperationErrorCodes.Internal, message, null);
}
