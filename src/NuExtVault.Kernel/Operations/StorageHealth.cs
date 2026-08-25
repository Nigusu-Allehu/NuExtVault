namespace NuExtVault.Operations;

public sealed class StorageHealth(string? storageDirectory)
{
    public const int VulnerabilitySnapshotRetentionLimit = 1;

    private readonly string? _root = storageDirectory is null
        ? null
        : Path.GetFullPath(storageDirectory);

    public StorageHealthReport GetReadiness() => GetReport(includeInventory: false);

    public StorageHealthReport GetReport() => GetReport(includeInventory: true);

    private StorageHealthReport GetReport(bool includeInventory)
    {
        if (_root is null)
        {
            return new StorageHealthReport(
                Ready: true,
                Status: "healthy",
                Dependency: "memory",
                Path: null,
                Reason: null,
                PackageCount: 0,
                StorageBytes: 0,
                FreeBytes: null,
                VulnerabilitySnapshotCount: 0,
                VulnerabilitySnapshotRetentionLimit);
        }

        if (!Directory.Exists(_root))
        {
            return Unhealthy("Storage directory does not exist.");
        }

        try
        {
            var probe = Path.Combine(_root, $".health-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, bufferSize: 1, FileOptions.DeleteOnClose))
            {
            }

            if (!includeInventory)
            {
                return new StorageHealthReport(
                    Ready: true,
                    Status: "healthy",
                    Dependency: "storage",
                    Path: _root,
                    Reason: null,
                    PackageCount: 0,
                    StorageBytes: 0,
                    FreeBytes: null,
                    VulnerabilitySnapshotCount: 0,
                    VulnerabilitySnapshotRetentionLimit);
            }

            var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToArray();
            var packageCount = files.Count(
                file => file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase));
            var bytes = files.Sum(file => new FileInfo(file).Length);
            var snapshotsDirectory = Path.Combine(_root, "vulnerabilities");
            var legacySnapshotCount = Directory.Exists(snapshotsDirectory)
                ? Directory.EnumerateDirectories(snapshotsDirectory)
                    .Count(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                : 0;
            var extensionStateDirectory = Path.Combine(_root, "extension-state");
            var ownerStateCount = Directory.Exists(extensionStateDirectory) &&
                                  Directory.EnumerateFiles(
                                          extensionStateDirectory,
                                          "*.json",
                                          SearchOption.AllDirectories)
                                      .Any()
                ? 1
                : 0;
            var rootPath = Path.GetPathRoot(_root);
            long? freeBytes = string.IsNullOrEmpty(rootPath)
                ? null
                : new DriveInfo(rootPath).AvailableFreeSpace;
            return new StorageHealthReport(
                Ready: true,
                Status: "healthy",
                Dependency: "storage",
                Path: _root,
                Reason: null,
                PackageCount: packageCount,
                StorageBytes: bytes,
                FreeBytes: freeBytes,
                VulnerabilitySnapshotCount: ownerStateCount + legacySnapshotCount,
                VulnerabilitySnapshotRetentionLimit);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Unhealthy(exception.Message);
        }
    }

    private StorageHealthReport Unhealthy(string reason) =>
        new(
            Ready: false,
            Status: "unhealthy",
            Dependency: "storage",
            Path: _root,
            Reason: reason,
            PackageCount: 0,
            StorageBytes: 0,
            FreeBytes: null,
            VulnerabilitySnapshotCount: 0,
            VulnerabilitySnapshotRetentionLimit);
}

public sealed record StorageHealthReport(
    bool Ready,
    string Status,
    string Dependency,
    string? Path,
    string? Reason,
    int PackageCount,
    long StorageBytes,
    long? FreeBytes,
    int VulnerabilitySnapshotCount,
    int VulnerabilitySnapshotRetentionLimit);
