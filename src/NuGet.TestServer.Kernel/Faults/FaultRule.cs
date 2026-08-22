using System.Net;

namespace NuGet.TestServer.Faults;

public sealed record FaultRule(
    string Id,
    string? Method,
    string? PathContains,
    HttpStatusCode StatusCode,
    int RemainingMatches,
    TimeSpan Delay);
