namespace NuExtVault.Packages;

public sealed record PackageTransferLimits
{
    public const long DefaultMaxRequestBodyBytes = 128L * 1024 * 1024;
    public const long DefaultMaxPackageBytes = 100L * 1024 * 1024;
    public const int DefaultMaxArchiveEntries = 10_000;
    public const long DefaultMaxArchiveEntryBytes = 64L * 1024 * 1024;
    public const long DefaultMaxExpandedArchiveBytes = 512L * 1024 * 1024;

    public static PackageTransferLimits Default { get; } = new();

    public long MaxRequestBodyBytes { get; init; } = DefaultMaxRequestBodyBytes;
    public long MaxPackageBytes { get; init; } = DefaultMaxPackageBytes;
    public int MaxArchiveEntries { get; init; } = DefaultMaxArchiveEntries;
    public long MaxArchiveEntryBytes { get; init; } = DefaultMaxArchiveEntryBytes;
    public long MaxExpandedArchiveBytes { get; init; } = DefaultMaxExpandedArchiveBytes;
    public string TemporaryDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "NuExtVault", "packages");

    public PackageTransferLimits Validate()
    {
        if (MaxRequestBodyBytes <= 0 ||
            MaxPackageBytes <= 0 ||
            MaxArchiveEntries <= 0 ||
            MaxArchiveEntryBytes <= 0 ||
            MaxExpandedArchiveBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PackageTransferLimits),
                "All package transfer limits must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(TemporaryDirectory))
        {
            throw new ArgumentException(
                "A temporary package directory is required.",
                nameof(PackageTransferLimits));
        }

        return this with { TemporaryDirectory = Path.GetFullPath(TemporaryDirectory) };
    }
}
