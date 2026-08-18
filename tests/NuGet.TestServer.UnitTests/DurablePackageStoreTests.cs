using Microsoft.Data.Sqlite;
using NuGet.Packaging.Core;
using NuGet.TestServer.Packages;
using NuGet.Versioning;

namespace NuGet.TestServer.UnitTests;

public sealed class DurablePackageStoreTests
{
    [Fact]
    public async Task Package_metadata_and_blob_survive_restart()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var first = new DurablePackageStore(directory.Path))
        {
            await first.AddAsync(TestPackageBuilder.Create("Persistent.Package", "1.2.3").Build());
            Assert.True(await first.SetListedAsync("Persistent.Package", "1.2.3", false));
        }

        await using var second = new DurablePackageStore(directory.Path);
        var restored = await second.FindAsync("persistent.package", "1.2.3");

        Assert.NotNull(restored);
        Assert.False(restored.IsListed);
        Assert.Equal("Persistent.Package", restored.Identity.Id);
        Assert.True(File.Exists(Path.Combine(directory.Path, "packages.db")));
    }

    [Fact]
    public async Task Metadata_database_records_the_current_schema_migration()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var store = new DurablePackageStore(directory.Path))
        {
            await store.AddAsync(TestPackageBuilder.Create("Schema.Package", "1.0.0").Build());
        }

        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(directory.Path, "packages.db")};Pooling=False");
        connection.Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText = "SELECT version FROM storage_migrations;";

        Assert.Equal(1L, versionCommand.ExecuteScalar());
        Assert.Equal(1L, migrationCommand.ExecuteScalar());
    }

    [Fact]
    public async Task Corrupted_metadata_database_has_an_actionable_diagnostic()
    {
        using var directory = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "packages.db"), "not sqlite");

        var error = Assert.Throws<PackageStorageCorruptionException>(
            () => new DurablePackageStore(directory.Path));

        Assert.Contains("packages.db", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("migrated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Legacy_file_layout_is_imported_without_moving_or_losing_packages()
    {
        using var directory = TemporaryDirectory.Create();
        var package = TestPackageBuilder.Create("Legacy.Package", "2.0.0").Build();
        var packageDirectory = Path.Combine(directory.Path, "packages", "legacy.package", "2.0.0");
        Directory.CreateDirectory(packageDirectory);
        var packagePath = Path.Combine(packageDirectory, "legacy.package.2.0.0.nupkg");
        await File.WriteAllBytesAsync(packagePath, package.Content);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, ".unlisted"), string.Empty);

        await using var store = new DurablePackageStore(directory.Path);
        var imported = await store.FindAsync("Legacy.Package", "2.0.0");

        Assert.NotNull(imported);
        Assert.False(imported.IsListed);
        Assert.True(File.Exists(packagePath));
    }

    [Fact]
    public async Task Corrupted_blob_is_reported_with_package_identity()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var first = new DurablePackageStore(directory.Path))
        {
            await first.AddAsync(TestPackageBuilder.Create("Corrupt.Package", "1.0.0").Build());
        }

        var blob = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "packages"),
            "*.nupkg",
            SearchOption.AllDirectories));
        await File.AppendAllTextAsync(blob, "corruption");

        var error = Assert.Throws<PackageStorageCorruptionException>(
            () => new DurablePackageStore(directory.Path));
        Assert.Contains("Corrupt.Package", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.0.0", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_second_store_cannot_use_the_same_root_concurrently()
    {
        using var directory = TemporaryDirectory.Create();
        var first = new DurablePackageStore(directory.Path);

        var error = Assert.Throws<PackageStorageInUseException>(
            () => new DurablePackageStore(directory.Path));

        Assert.Contains(directory.Path, error.Message, StringComparison.OrdinalIgnoreCase);
        await first.DisposeAsync();
    }

    [Fact]
    public async Task Canceled_publication_leaves_no_package_or_partial_blob()
    {
        using var directory = TemporaryDirectory.Create();
        await using var store = new DurablePackageStore(directory.Path);
        var package = new TestPackage
        {
            Identity = new PackageIdentity("Canceled.Package", NuGetVersion.Parse("1.0.0")),
            Content = new byte[64 * 1024 * 1024],
            NuspecContent = [],
            NormalizedVersion = "1.0.0",
            Description = string.Empty,
            Authors = string.Empty,
            Tags = string.Empty,
            DependencyGroups = [],
            Published = DateTimeOffset.UtcNow
        };
        using var cancellation = new CancellationTokenSource();

        var publication = store.AddAsync(package, cancellation.Token).AsTask();
        await WaitUntilAsync(
            () => Directory.EnumerateFiles(
                    Path.Combine(directory.Path, "packages"),
                    "*.tmp",
                    SearchOption.AllDirectories)
                .Any(),
            TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);

        Assert.Null(await store.FindAsync("Canceled.Package", "1.0.0"));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "packages"),
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Canceled_reset_preserves_all_committed_packages()
    {
        using var directory = TemporaryDirectory.Create();
        await using var store = new DurablePackageStore(directory.Path);
        await store.AddAsync(TestPackageBuilder.Create("Reset.Package", "1.0.0").Build());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ResetAsync(cancellation.Token).AsTask());

        Assert.NotNull(await store.FindAsync("Reset.Package", "1.0.0"));
    }

    [Fact]
    public async Task Orphaned_complete_blob_is_recovered_after_interrupted_metadata_publication()
    {
        using var directory = TemporaryDirectory.Create();
        var package = TestPackageBuilder.Create("Recovered.Package", "3.0.0").Build();
        var packageDirectory = Path.Combine(directory.Path, "packages", "recovered.package", "3.0.0");
        Directory.CreateDirectory(packageDirectory);
        await File.WriteAllBytesAsync(
            Path.Combine(packageDirectory, "recovered.package.3.0.0.nupkg"),
            package.Content);

        await using var store = new DurablePackageStore(directory.Path);

        Assert.NotNull(await store.FindAsync("Recovered.Package", "3.0.0"));
    }

    [Fact]
    public async Task Interrupted_blob_temporary_file_is_removed_during_recovery()
    {
        using var directory = TemporaryDirectory.Create();
        var packageDirectory = Path.Combine(directory.Path, "packages", "partial.package", "1.0.0");
        Directory.CreateDirectory(packageDirectory);
        var partialPath = Path.Combine(
            packageDirectory,
            "partial.package.1.0.0.nupkg.interrupted.tmp");
        await File.WriteAllTextAsync(partialPath, "partial");

        await using var store = new DurablePackageStore(directory.Path);

        Assert.False(File.Exists(partialPath));
        Assert.Null(await store.FindAsync("Partial.Package", "1.0.0"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public static TemporaryDirectory Create() =>
            new(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NuGet.TestServer.UnitTests",
                Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }

        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            await Task.Delay(1, cancellation.Token);
        }
    }
}
