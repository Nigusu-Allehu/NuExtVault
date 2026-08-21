namespace NuGet.TestServer.Extensions.Abstractions;

/// <summary>
/// The semantic outcome of an operation or of request binding. It is transport-neutral:
/// the kernel is the only component that maps an outcome onto an HTTP status code, so
/// the same result can be rendered in-process today and out-of-process later.
/// </summary>
internal enum OperationResultStatus
{
    Ok,
    Created,
    Accepted,
    NoContent,
    InvalidRequest,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    UnsupportedMediaType,
    PayloadTooLarge,
    Unprocessable,
    TooManyRequests,
    Unavailable,
    InternalError
}

/// <summary>
/// The payload an operation result carries. Owners describe the payload; the kernel
/// owns serialization, headers, content negotiation, and streaming.
/// </summary>
internal abstract record OperationResultBody;

/// <summary>A serializable document the kernel renders with kernel-owned options.</summary>
internal sealed record OperationDocumentBody(object Document) : OperationResultBody;

internal sealed record OperationTextBody(string Value, string MediaType) : OperationResultBody;

internal sealed record OperationProblemBody(string Detail) : OperationResultBody;

/// <summary>Kernel-issued content, referenced by a bounded handle.</summary>
internal sealed record OperationContentBody(StreamHandle Handle) : OperationResultBody;

/// <summary>
/// The single, immutable, versioned, transport-neutral rendering contract. It is the
/// only way an extension may influence the wire form of a response. Extensions never
/// choose status codes, headers, or serializers.
/// </summary>
internal sealed record OperationResult(
    OperationResultStatus Status,
    OperationResultBody? Body = null,
    string? Location = null)
{
    /// <summary>The contract version of this rendering shape.</summary>
    internal const int ContractVersion = 1;

    public int Version => ContractVersion;

    public static OperationResult Ok(object document) =>
        new(OperationResultStatus.Ok, new OperationDocumentBody(document));

    public static OperationResult Ok(StreamHandle content) =>
        new(OperationResultStatus.Ok, new OperationContentBody(content));

    public static OperationResult Text(string value, string mediaType) =>
        new(OperationResultStatus.Ok, new OperationTextBody(value, mediaType));

    public static OperationResult NoContent() => new(OperationResultStatus.NoContent);

    public static OperationResult Created(object document, string location) =>
        new(OperationResultStatus.Created, new OperationDocumentBody(document), location);

    public static OperationResult Accepted(object document, string location) =>
        new(OperationResultStatus.Accepted, new OperationDocumentBody(document), location);

    public static OperationResult Empty(OperationResultStatus status) => new(status);

    public static OperationResult Problem(OperationResultStatus status, string detail) =>
        new(status, new OperationProblemBody(detail));
}

/// <summary>
/// Typed operation errors. Owners classify failures; the kernel maps the classification
/// onto the wire.
/// </summary>
internal static class OperationErrors
{
    public static OperationError NotFound(string message) =>
        new(OperationErrorCodes.NotFound, message, null);

    public static OperationError InvalidRequest(string message) =>
        new(OperationErrorCodes.InvalidRequest, message, null);

    public static OperationError Unauthorized(string message) =>
        new(OperationErrorCodes.Unauthorized, message, null);

    public static OperationError Conflict(string message) =>
        new(OperationErrorCodes.Conflict, message, null);

    public static OperationError PolicyDenied(string message) =>
        new(OperationErrorCodes.PolicyDenied, message, null);

    public static OperationError LimitExceeded(string message) =>
        new(OperationErrorCodes.LimitExceeded, message, null);

    public static OperationError Unavailable(string message, int? retryAfterSeconds = null) =>
        new(OperationErrorCodes.Unavailable, message, retryAfterSeconds);

    public static OperationError Internal(string message) =>
        new(OperationErrorCodes.Internal, message, null);

    /// <summary>
    /// The transport-neutral status the kernel renders for one error classification.
    /// </summary>
    public static OperationResultStatus Classify(OperationErrorKind kind) => kind switch
    {
        OperationErrorKind.InvalidRequest => OperationResultStatus.InvalidRequest,
        OperationErrorKind.Unauthorized => OperationResultStatus.Unauthorized,
        OperationErrorKind.Forbidden => OperationResultStatus.Forbidden,
        OperationErrorKind.NotFound => OperationResultStatus.NotFound,
        OperationErrorKind.Conflict => OperationResultStatus.Conflict,
        OperationErrorKind.LimitExceeded => OperationResultStatus.PayloadTooLarge,
        OperationErrorKind.Unavailable => OperationResultStatus.Unavailable,
        _ => OperationResultStatus.InternalError
    };
}

/// <summary>
/// Thrown by a descriptor binder when the current protocol rejects a request before an
/// operation can be dispatched. It carries only the transport-neutral rendering.
/// </summary>
internal sealed class OperationBindingException(
    OperationResult result,
    Exception? innerException = null) : Exception(null, innerException)
{
    public OperationResult Result { get; } =
        result ?? throw new ArgumentNullException(nameof(result));
}
