using System.Text.Json.Serialization;

namespace NuGet.TestServer.Extensions.Sdk;

internal sealed record OperationId(string Value);

/// <summary>
/// An open operation family. Well-known families are declared here for the built-in
/// protocol surface; separately compiled modules declare their own with
/// <see cref="Custom"/> so the family set is not closed by the kernel.
/// </summary>
internal sealed record OperationFamily(string Name)
{
    internal static OperationFamily ServiceIndex { get; } = new("ServiceIndex");

    internal static OperationFamily FlatContainer { get; } = new("FlatContainer");

    internal static OperationFamily Registration { get; } = new("Registration");

    internal static OperationFamily Search { get; } = new("Search");

    internal static OperationFamily PackageManagement { get; } = new("PackageManagement");

    internal static OperationFamily Moderation { get; } = new("Moderation");

    internal static OperationFamily Vulnerabilities { get; } = new("Vulnerabilities");

    internal static OperationFamily TestControl { get; } = new("TestControl");

    internal static OperationFamily Health { get; } = new("Health");

    internal static OperationFamily Diagnostics { get; } = new("Diagnostics");

    internal static OperationFamily Backup { get; } = new("Backup");

    internal static OperationFamily Restore { get; } = new("Restore");

    public static OperationFamily Custom(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new OperationFamily(name);
    }

    public override string ToString() => Name;
}

internal sealed record OperationContract(
    OperationId Id,
    OperationFamily Family,
    int ContractVersion,
    string RequestContract,
    string ResponseContract);

internal sealed record OperationBinding(
    OperationContract Contract,
    Type RequestType,
    Type ResponseType);

internal sealed record EmptyRequest;

internal sealed record EmptyResponse;

internal sealed record PackageIdentity(string Id, string Version);

public sealed record StreamHandle
{
    public StreamHandle(string id, long maximumLength, string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (maximumLength <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLength),
                "A stream handle must declare a positive maximum length.");
        }

        Id = id;
        MaximumLength = maximumLength;
        ContentType = contentType;
    }

    public string Id { get; }

    public long MaximumLength { get; }

    public string ContentType { get; }
}

internal sealed record ContentDescriptor(
    StreamHandle Content,
    string? Sha512,
    long Length,
    bool SupportsRanges);

public sealed record OperationResponse<TResponse>
{
    [JsonConstructor]
    internal OperationResponse(TResponse? value, OperationError? error)
        : this(value, error, null)
    {
    }

    internal OperationResponse(
        TResponse? value,
        OperationError? error,
        OperationResult? rendering)
    {
        if ((value is null) == (error is null))
        {
            throw new ArgumentException(
                "An operation response must contain exactly one value or error.");
        }

        Value = value;
        Error = error;
        Rendering = rendering;
    }

    [JsonInclude]
    internal TResponse? Value { get; }

    [JsonInclude]
    internal OperationError? Error { get; }

    /// <summary>
    /// The optional transport-neutral rendering an owner attached to this response. It
    /// is the only way an owner may influence the wire form; the kernel remains the
    /// only component that speaks HTTP.
    /// </summary>
    [JsonIgnore]
    internal OperationResult? Rendering { get; }

    public static OperationResponse<TResponse> Success(TResponse value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), null, null);

    internal static OperationResponse<TResponse> Success(
        TResponse value,
        OperationResult rendering) =>
        new(
            value ?? throw new ArgumentNullException(nameof(value)),
            null,
            rendering ?? throw new ArgumentNullException(nameof(rendering)));

    internal static OperationResponse<TResponse> Failure(OperationError error) =>
        new(default, error ?? throw new ArgumentNullException(nameof(error)), null);

    internal static OperationResponse<TResponse> Failure(
        OperationError error,
        OperationResult rendering) =>
        new(
            default,
            error ?? throw new ArgumentNullException(nameof(error)),
            rendering ?? throw new ArgumentNullException(nameof(rendering)));
}

internal sealed record OperationError
{
    [JsonConstructor]
    internal OperationError(string code, string message, int? retryAfterSeconds)
    {
        Code = code;
        Kind = OperationErrorCodes.Classify(code);
        Message = message;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public string Code { get; }

    [JsonIgnore]
    public OperationErrorKind Kind { get; }

    public string Message { get; }

    public int? RetryAfterSeconds { get; }
}

internal enum OperationErrorKind
{
    InvalidRequest,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    LimitExceeded,
    Unavailable,
    Internal
}

internal static class OperationErrorCodes
{
    internal const string InvalidRequest = "request.invalid";
    internal const string Unauthorized = "access.unauthorized";
    internal const string PolicyDenied = "policy.denied";
    internal const string NotFound = "resource.not-found";
    internal const string Conflict = "resource.conflict";
    internal const string LimitExceeded = "limit.exceeded";
    internal const string Unavailable = "service.unavailable";
    internal const string Internal = "operation.internal";

    internal static OperationErrorKind Classify(string code) => code switch
    {
        InvalidRequest => OperationErrorKind.InvalidRequest,
        Unauthorized => OperationErrorKind.Unauthorized,
        PolicyDenied => OperationErrorKind.Forbidden,
        NotFound => OperationErrorKind.NotFound,
        Conflict => OperationErrorKind.Conflict,
        LimitExceeded => OperationErrorKind.LimitExceeded,
        Unavailable => OperationErrorKind.Unavailable,
        _ => OperationErrorKind.Internal
    };
}
