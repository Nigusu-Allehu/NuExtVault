namespace NuGet.TestServer.Requests;

public sealed record RequestRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    string Method,
    string Path,
    int StatusCode,
    long DurationMilliseconds,
    string? FaultRuleId,
    string? AuthenticatedUser);
