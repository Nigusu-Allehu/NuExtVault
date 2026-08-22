namespace NuGet.TestServer.Extensions.Sdk;

/// <summary>
/// Canonical names of the kernel capabilities a separately compiled module may request.
/// Names are the single source of truth shared by code, manifests, profiles, tests, and
/// documentation.
/// </summary>
internal static class KernelCapabilityNames
{
    /// <summary>Reads the kernel-owned host clock. Narrow, serializable, read-only.</summary>
    public const string HostClockRead = "host.clock.read";

    /// <summary>
    /// Performs a bounded indexed package search. The kernel applies authoritative
    /// search visibility immediately before returning the page.
    /// </summary>
    public const string PackageSearchQuery = "packages.search.query";

    /// <summary>
    /// Performs bounded signature and scanner inspection of one kernel-issued package
    /// handle. The capability returns observations only and cannot mutate package state.
    /// </summary>
    public const string SupplyChainSignatureInspect = "supply-chain.signature.inspect";
    public const string SupplyChainPackageScan = "supply-chain.package.scan";
}

/// <summary>
/// A narrow, action-scoped, serializable read of the kernel-owned host clock. It is
/// asynchronous so the same call can cross a process boundary later.
/// </summary>
public interface IHostClockCapability
{
    ValueTask<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken);
}

internal sealed record PolicyPackageHandle(string Value);

internal sealed record PolicyPackageIdentity(string Id, string Version);

internal enum SignatureInspectionOutcome
{
    Valid,
    Invalid,
    Unsigned
}

internal sealed record PackageSignatureInspection(
    SignatureInspectionOutcome Outcome,
    string Detail);

internal enum PackageScannerInspectionOutcome
{
    Clean,
    Malicious,
    Inconclusive
}

internal sealed record PackageScannerInspection(
    PackageScannerInspectionOutcome Outcome,
    string Detail);

internal interface IPackageSignatureInspectionCapability
{
    ValueTask<PackageSignatureInspection> InspectSignatureAsync(
        PolicyPackageHandle package,
        CancellationToken cancellationToken);
}

internal interface IPackageScannerCapability
{
    ValueTask<PackageScannerInspection> ScanAsync(
        PolicyPackageHandle package,
        CancellationToken cancellationToken);
}
