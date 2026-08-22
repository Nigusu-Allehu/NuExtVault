using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Kernel;

/// <summary>
/// Kernel error classification and transport mapping. Owners return typed errors and a
/// transport-neutral <see cref="OperationResult"/>; the kernel is the only component
/// that chooses HTTP status codes.
/// </summary>
internal static class OperationErrorPolicy
{
    public static int GetStatusCode(OperationResultStatus status) => status switch
    {
        OperationResultStatus.Ok => StatusCodes.Status200OK,
        OperationResultStatus.Created => StatusCodes.Status201Created,
        OperationResultStatus.Accepted => StatusCodes.Status202Accepted,
        OperationResultStatus.NoContent => StatusCodes.Status204NoContent,
        OperationResultStatus.InvalidRequest => StatusCodes.Status400BadRequest,
        OperationResultStatus.Unauthorized => StatusCodes.Status401Unauthorized,
        OperationResultStatus.Forbidden => StatusCodes.Status403Forbidden,
        OperationResultStatus.NotFound => StatusCodes.Status404NotFound,
        OperationResultStatus.Conflict => StatusCodes.Status409Conflict,
        OperationResultStatus.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
        OperationResultStatus.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
        OperationResultStatus.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        OperationResultStatus.TooManyRequests => StatusCodes.Status429TooManyRequests,
        OperationResultStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };

    public static int GetStatusCode(OperationErrorKind kind) =>
        GetStatusCode(OperationErrors.Classify(kind));

    public static OperationResult CreateResult(OperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = OperationErrors.Classify(error.Kind);
        return error.Kind switch
        {
            OperationErrorKind.NotFound or
            OperationErrorKind.Unauthorized or
            OperationErrorKind.Forbidden => new OperationResult(status),
            _ => OperationResult.Problem(status, error.Message)
        };
    }

    public static OperationError NotFound(string message) => OperationErrors.NotFound(message);

    public static OperationError InvalidRequest(string message) =>
        OperationErrors.InvalidRequest(message);

    public static OperationError Conflict(string message) => OperationErrors.Conflict(message);

    public static OperationError PolicyDenied(string message) =>
        OperationErrors.PolicyDenied(message);

    public static OperationError LimitExceeded(string message) =>
        OperationErrors.LimitExceeded(message);

    public static OperationError Unavailable(string message, int? retryAfterSeconds = null) =>
        OperationErrors.Unavailable(message, retryAfterSeconds);

    public static OperationError Internal(string message) => OperationErrors.Internal(message);
}
