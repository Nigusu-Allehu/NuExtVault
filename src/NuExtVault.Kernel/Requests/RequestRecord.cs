namespace NuExtVault.Requests;

public sealed record RequestRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    string Method,
    string Path,
    int StatusCode,
    long DurationMilliseconds,
    string? FaultRuleId,
    string? AuthenticatedUser);

internal sealed record CapturedRequestRecord(
    RequestRecord Record,
    IReadOnlyDictionary<string, string> Headers)
{
    public bool BodyStored => false;
}
