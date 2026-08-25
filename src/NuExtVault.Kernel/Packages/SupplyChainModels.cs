namespace NuExtVault.Packages;

public enum PackageModerationState
{
    Quarantined,
    Published,
    Rejected,
    Deleted
}

public enum PackagePublicationOutcome
{
    Published,
    Quarantined,
    Rejected,
    Duplicate,
    Conflict,
    Unauthorized,
    QuotaExceeded
}

public enum PackageScanOutcome
{
    Clean,
    Malicious,
    Inconclusive
}

public sealed record PackageScanResult(PackageScanOutcome Outcome, string Detail);

public interface IPackagePolicyScanner
{
    ValueTask<PackageScanResult> ScanAsync(
        TestPackage package,
        CancellationToken token = default);
}

public sealed class SafePackagePolicyScanner : IPackagePolicyScanner
{
    public ValueTask<PackageScanResult> ScanAsync(
        TestPackage package,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PackageScanResult(
            PackageScanOutcome.Clean,
            "Package passed structural policy checks. This scanner is not antivirus."));
    }
}

public sealed class DeterministicPackagePolicyScanner(
    IReadOnlyDictionary<string, PackageScanResult> results,
    PackageScanResult? defaultResult = null) : IPackagePolicyScanner
{
    private readonly IReadOnlyDictionary<string, PackageScanResult> _results =
        results ?? throw new ArgumentNullException(nameof(results));
    private readonly PackageScanResult _defaultResult = defaultResult ??
        new(PackageScanOutcome.Clean, "No deterministic test rule matched.");

    public ValueTask<PackageScanResult> ScanAsync(
        TestPackage package,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _results.TryGetValue(package.Identity.Id, out var result)
                ? result
                : _defaultResult);
    }
}

public sealed record SupplyChainOptions
{
    public bool RequireSignedPackages { get; init; }
    public int MaximumPackagesPerIdentity { get; init; } = int.MaxValue;
    public long MaximumBytesPerIdentity { get; init; } = long.MaxValue;
    public int MaximumPackagesPerRepository { get; init; } = int.MaxValue;
    public long MaximumBytesPerRepository { get; init; } = long.MaxValue;
    public IReadOnlyDictionary<string, string> NamespaceReservations { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal SupplyChainOptions Validate()
    {
        if (MaximumPackagesPerIdentity <= 0 ||
            MaximumBytesPerIdentity <= 0 ||
            MaximumPackagesPerRepository <= 0 ||
            MaximumBytesPerRepository <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SupplyChainOptions),
                "Supply-chain quotas must be positive.");
        }

        if (NamespaceReservations.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
        {
            throw new ArgumentException(
                "Namespace reservations require non-empty prefixes and identities.",
                nameof(NamespaceReservations));
        }

        return this;
    }
}

public sealed record PackagePublicationRequest(
    TestPackage Package,
    string Identity,
    string Repository,
    bool Administrator = false);

public sealed record PackagePublicationResult(
    PackagePublicationOutcome Outcome,
    string Message);

public sealed record PackageSupplyChainStatus(
    string Id,
    string Version,
    PackageModerationState State,
    string? Owner,
    string Repository,
    string ContentHash,
    long ContentLength);

public sealed record PackageValidationRecord(
    string Validator,
    string Outcome,
    string Detail,
    DateTimeOffset Timestamp);

public sealed record PackageSupplyChainAudit(
    long Sequence,
    DateTimeOffset Timestamp,
    string? PackageId,
    string? PackageVersion,
    string Actor,
    string Action,
    string Result,
    string Detail);
