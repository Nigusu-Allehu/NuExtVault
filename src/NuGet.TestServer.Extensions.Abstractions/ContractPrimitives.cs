using System.Text.Json.Serialization;

namespace NuGet.TestServer.Extensions.Abstractions;

internal sealed record OperationId(string Value);

internal enum OperationFamily
{
    ServiceIndex,
    FlatContainer,
    Registration,
    Search,
    PackageManagement,
    Moderation,
    Vulnerabilities,
    TestControl,
    Health,
    Diagnostics,
    Backup,
    Restore
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

internal sealed record StreamHandle(
    string Id,
    long MaximumLength,
    string ContentType);

internal sealed record ContentDescriptor(
    StreamHandle Content,
    string? Sha512,
    long Length,
    bool SupportsRanges);

internal sealed record OperationResponse<TResponse>
{
    [JsonConstructor]
    internal OperationResponse(TResponse? value, OperationError? error)
    {
        if ((value is null) == (error is null))
        {
            throw new ArgumentException(
                "An operation response must contain exactly one value or error.");
        }

        Value = value;
        Error = error;
    }

    public TResponse? Value { get; }

    public OperationError? Error { get; }

    internal static OperationResponse<TResponse> Success(TResponse value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), null);

    internal static OperationResponse<TResponse> Failure(OperationError error) =>
        new(default, error ?? throw new ArgumentNullException(nameof(error)));
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
