namespace NuGet.TestServer.Extensions.Abstractions;

/// <summary>
/// Canonical names of the kernel capabilities a separately compiled module may request.
/// Names are the single source of truth shared by code, manifests, profiles, tests, and
/// documentation.
/// </summary>
internal static class KernelCapabilityNames
{
    /// <summary>Reads the kernel-owned host clock. Narrow, serializable, read-only.</summary>
    public const string HostClockRead = "host.clock.read";
}

/// <summary>
/// A narrow, action-scoped, serializable read of the kernel-owned host clock. It is
/// asynchronous so the same call can cross a process boundary later.
/// </summary>
internal interface IHostClockCapability
{
    ValueTask<DateTimeOffset> GetUtcNowAsync(CancellationToken token);
}
