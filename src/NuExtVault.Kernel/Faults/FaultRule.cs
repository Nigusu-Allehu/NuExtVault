using System.Net;

namespace NuExtVault.Faults;

public sealed record FaultRule(
    string Id,
    string? Method,
    string? PathContains,
    HttpStatusCode StatusCode,
    int RemainingMatches,
    TimeSpan Delay);
