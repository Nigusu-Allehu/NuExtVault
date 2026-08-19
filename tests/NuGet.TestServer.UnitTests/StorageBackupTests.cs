using System.IO.Compression;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class StorageBackupTests
{
    [Fact]
    public async Task Backup_restores_packages_and_vulnerability_cache_into_clean_storage()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var sourceStore = new InMemoryPackageStore(source.Path);
        await sourceStore.AddAsync(
            TestPackageBuilder.Create("Recovered.Package", "1.2.3").Build());
        var vulnerabilityDirectory = Path.Combine(
            source.Path,
            "vulnerabilities",
            "snapshot");
        Directory.CreateDirectory(vulnerabilityDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(vulnerabilityDirectory, "metadata.json"),
            """{"id":"snapshot"}""");
        var backupPath = Path.Combine(source.Path, "backup.zip");

        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        await StorageBackup.RestoreAsync(backupPath, destination.Path);

        var restoredStore = new InMemoryPackageStore(destination.Path);
        Assert.NotNull(await restoredStore.FindAsync("Recovered.Package", "1.2.3"));
        Assert.True(File.Exists(
            Path.Combine(
                destination.Path,
                "vulnerabilities",
                "snapshot",
                "metadata.json")));
        Assert.Contains(
            manifest.Files,
            file => file.Path == "packages/recovered.package/1.2.3/recovered.package.1.2.3.nupkg");
        Assert.Contains(
            manifest.Files,
            file => file.Path == "vulnerabilities/snapshot/metadata.json");
    }

    [Fact]
    public async Task Restore_rejects_content_that_does_not_match_the_manifest()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var sourceStore = new InMemoryPackageStore(source.Path);
        await sourceStore.AddAsync(
            TestPackageBuilder.Create("Tampered.Package", "1.0.0").Build());
        var backupPath = Path.Combine(source.Path, "backup.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Update))
        {
            var packageEntry = Assert.Single(
                archive.Entries,
                entry => entry.FullName.EndsWith(".nupkg", StringComparison.Ordinal));
            var packagePath = packageEntry.FullName;
            packageEntry.Delete();
            var replacement = archive.CreateEntry(packagePath);
            await using var writer = new StreamWriter(replacement.Open());
            await writer.WriteAsync("tampered");
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => StorageBackup.RestoreAsync(backupPath, destination.Path));

        Assert.Contains("integrity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination.Path));
    }

    [Fact]
    public async Task Backup_restores_current_durable_database_and_security_state()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await using (var store = new DurablePackageStore(source.Path))
        await using (var supplyChain = new PackageSupplyChainService(store, source.Path))
        {
            var result = await supplyChain.PublishAsync(new PackagePublicationRequest(
                TestPackageBuilder.Create("Durable.Backup", "1.0.0").Build(),
                "publisher",
                "default",
                Administrator: false));
            Assert.Equal(PackagePublicationOutcome.Published, result.Outcome);
        }

        var securityDirectory = Path.Combine(source.Path, "security");
        Directory.CreateDirectory(securityDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(securityDirectory, "package-owners.json"),
            """{"Durable.Backup":"publisher"}""");
        var backupPath = Path.Combine(source.Path, "backup.zip");

        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        await StorageBackup.RestoreAsync(backupPath, destination.Path);

        Assert.Contains(manifest.Files, file => file.Path == "packages.db");
        Assert.Contains(manifest.Files, file => file.Path == "supply-chain.db");
        Assert.Contains(
            manifest.Files,
            file => file.Path == "security/package-owners.json");
        await using var restoredStore = new DurablePackageStore(destination.Path);
        await using var restoredSupplyChain =
            new PackageSupplyChainService(restoredStore, destination.Path);
        Assert.NotNull(await restoredStore.FindAsync("Durable.Backup", "1.0.0"));
        Assert.NotNull(await restoredSupplyChain.GetStatusAsync("Durable.Backup", "1.0.0"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NuGet.TestServer.UnitTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
