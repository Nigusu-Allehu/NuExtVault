using Microsoft.Data.Sqlite;
using NuGet.Packaging.Core;
using NuGet.TestServer.Packages;
using NuGet.Versioning;
using System.Diagnostics;
using System.Security.Cryptography;

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
    public async Task Version_one_metadata_is_migrated_and_backfilled_for_indexed_queries()
    {
        using var directory = TemporaryDirectory.Create();
        await CreateVersionOneStorageAsync(directory.Path);

        await using var store = new DurablePackageStore(directory.Path);
        var packages = await store.FindByIdAsync("SCHEMA.PACKAGE");
        var search = await store.SearchAsync("migration", false, 0, 20);

        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(directory.Path, "packages.db")};Pooling=False");
        connection.Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText = "SELECT max(version) FROM storage_migrations;";
        using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText =
            """
            SELECT count(*)
            FROM pragma_table_info('packages')
            WHERE name IN ('normalized_id', 'is_prerelease', 'version_sort_key', 'search_text');
            """;
        using var indexesCommand = connection.CreateCommand();
        indexesCommand.CommandText =
            """
            SELECT count(*)
            FROM sqlite_master
            WHERE type IN ('index', 'table')
              AND name IN ('ix_packages_identity', 'ix_packages_registration',
                           'ix_packages_search_page', 'packages_search');
            """;
        using var identityPlanCommand = connection.CreateCommand();
        identityPlanCommand.CommandText =
            """
            EXPLAIN QUERY PLAN
            SELECT normalized_version
            FROM packages
            WHERE normalized_id = 'schema.package' AND normalized_version = '1.0.0';
            """;
        using var searchPlanCommand = connection.CreateCommand();
        searchPlanCommand.CommandText =
            """
            EXPLAIN QUERY PLAN
            SELECT rowid
            FROM packages_search
            WHERE packages_search MATCH '"migration"';
            """;

        Assert.Equal(4L, versionCommand.ExecuteScalar());
        Assert.Equal([1L, 2L, 3L, 4L], ReadMigrationVersions(connection));
        Assert.Equal(4L, migrationCommand.ExecuteScalar());
        Assert.Equal(4L, columnsCommand.ExecuteScalar());
        Assert.Equal(4L, indexesCommand.ExecuteScalar());
        Assert.Contains(
            "ix_packages_identity",
            ReadQueryPlan(identityPlanCommand),
            StringComparison.Ordinal);
        Assert.Contains(
            "VIRTUAL TABLE INDEX",
            ReadQueryPlan(searchPlanCommand),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1.0.0", Assert.Single(packages).NormalizedVersion);
        Assert.NotEmpty(Assert.Single(packages).PackageHash);
        Assert.Equal(1, search.TotalHits);
    }

    [Fact]
    public async Task Metadata_queries_do_not_open_package_bodies()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var writer = new DurablePackageStore(directory.Path))
        {
            await writer.AddAsync(
                TestPackageBuilder.Create("Indexed.Package", "1.0.0")
                    .WithDescription("searchable metadata")
                    .Build());
        }

        var blob = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "packages"),
            "*.nupkg",
            SearchOption.AllDirectories));
        await using var exclusiveBodyLock = new FileStream(
            blob,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        await using var reader = new DurablePackageStore(directory.Path);

        Assert.Single(await reader.FindByIdAsync("INDEXED.PACKAGE"));
        Assert.Single(await reader.GetAllAsync());
        Assert.Equal(1, (await reader.SearchAsync("searchable", false, 0, 20)).TotalHits);
    }

    [Fact]
    public async Task Indexed_search_keeps_total_versions_and_order_under_concurrent_writes()
    {
        using var directory = TemporaryDirectory.Create();
        await using var store = new DurablePackageStore(directory.Path);
        for (var index = 0; index < 40; index++)
        {
            await store.AddAsync(
                TestPackageBuilder.Create($"Corpus.{index:D3}", "1.0.0")
                    .WithTags("representative searchable")
                    .Build());
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await start.Task;
            for (var index = 40; index < 60; index++)
            {
                await store.AddAsync(
                    TestPackageBuilder.Create($"Corpus.{index:D3}", "1.0.0")
                        .WithTags("representative searchable")
                        .Build());
                await Task.Delay(1);
            }
        });
        var readers = Enumerable.Range(0, 8).Select(async _ =>
        {
            start.TrySetResult();
            for (var iteration = 0; iteration < 10; iteration++)
            {
                var page = await store.SearchAsync("searchable", false, 10, 10);
                Assert.Equal(10, page.Items.Count);
                Assert.True(page.TotalHits >= 40);
                Assert.Equal(
                    page.Items.Select(item => item.Package.Identity.Id)
                        .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(id => id, StringComparer.Ordinal),
                    page.Items.Select(item => item.Package.Identity.Id));
                Assert.All(page.Items, item => Assert.Single(item.Versions));
            }
        });

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(60, (await store.SearchAsync("searchable", false, 0, 10)).TotalHits);
    }

    [Fact]
    public async Task Representative_corpus_startup_stays_body_independent_and_bounded()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var writer = new DurablePackageStore(directory.Path))
        {
            for (var index = 0; index < 200; index++)
            {
                await writer.AddAsync(
                    TestPackageBuilder.Create($"Performance.{index:D4}", "1.0.0")
                        .WithFile("content.bin", new byte[16 * 1024])
                        .Build());
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        await using (var reader = new DurablePackageStore(directory.Path))
        {
            Assert.Equal(200, (await reader.SearchAsync(string.Empty, false, 0, 1000)).TotalHits);
        }

        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Startup took {stopwatch.Elapsed}.");
        Assert.True(allocated < 12 * 1024 * 1024, $"Startup allocated {allocated:N0} bytes.");

        await using var searchStore = new DurablePackageStore(directory.Path);
        stopwatch.Restart();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var page = await searchStore.SearchAsync("performance", false, 50, 20);
            Assert.Equal(200, page.TotalHits);
            Assert.Equal(20, page.Items.Count);
        }

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"One hundred indexed searches took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Repository_metadata_and_symbols_survive_restart()
    {
        using var directory = TemporaryDirectory.Create();
        var metadata = new PackageRepositoryMetadata(
            ["Alice", "Bob"],
            Downloads: 42,
            Verified: true,
            new PackageDeprecation(
                ["Legacy", "Other"],
                "Use the replacement.",
                new AlternatePackage("Replacement.Package", "[2.0.0,)")));
        var symbols = TestPackageBuilder.Create("Persistent.Package", "1.2.3")
            .WithFile("lib/net10.0/Persistent.Package.pdb", [1, 2, 3, 4])
            .Build()
            .Content;

        await using (var first = new DurablePackageStore(directory.Path))
        {
            await first.AddAsync(
                TestPackageBuilder.Create("Persistent.Package", "1.2.3")
                    .WithPackageType("DotnetTool", "1.0.0")
                    .Build());
            Assert.True(await first.SetRepositoryMetadataAsync(
                "Persistent.Package",
                "1.2.3",
                metadata));
            await first.AddSymbolAsync(symbols);
        }

        await using var second = new DurablePackageStore(directory.Path);
        var restored = await second.FindAsync("persistent.package", "1.2.3");
        var restoredSymbols = await second.FindSymbolAsync("persistent.package", "1.2.3");
        var tools = await second.SearchAsync(
            string.Empty,
            includePrerelease: false,
            skip: 0,
            take: 20,
            packageType: "dotnettool");

        Assert.NotNull(restored);
        Assert.Equal(metadata.Owners, restored.RepositoryMetadata.Owners);
        Assert.Equal(metadata.Downloads, restored.RepositoryMetadata.Downloads);
        Assert.Equal(metadata.Verified, restored.RepositoryMetadata.Verified);
        Assert.Equal(
            metadata.Deprecation!.Reasons,
            restored.RepositoryMetadata.Deprecation!.Reasons);
        Assert.Equal(
            metadata.Deprecation.Message,
            restored.RepositoryMetadata.Deprecation.Message);
        Assert.Equal(
            metadata.Deprecation.AlternatePackage,
            restored.RepositoryMetadata.Deprecation.AlternatePackage);
        Assert.Equal(symbols, restoredSymbols);
        Assert.Equal(["Persistent.Package"], tools.Items.Select(item => item.Package.Identity.Id));
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
    public async Task Numeric_moderation_state_fails_closed_in_search_and_metadata_reads()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var writer = new DurablePackageStore(directory.Path))
        {
            await writer.AddAsync(
                TestPackageBuilder.Create("Invalid.State", "1.0.0").Build());
        }

        using (var connection = new SqliteConnection(
                   $"Data Source={Path.Combine(directory.Path, "packages.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE packages SET moderation_state = '1' WHERE normalized_id = 'invalid.state';";
            command.ExecuteNonQuery();
        }

        await using var reader = new DurablePackageStore(directory.Path);
        Assert.Equal(0, (await reader.SearchAsync("Invalid.State", false, 0, 20)).TotalHits);
        Assert.Throws<PackageStorageCorruptionException>(
            () => reader.FindAsync("Invalid.State", "1.0.0"));
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
    public async Task Same_length_blob_tampering_is_detected_when_content_is_opened()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var first = new DurablePackageStore(directory.Path))
        {
            await first.AddAsync(TestPackageBuilder.Create("Tampered.Package", "1.0.0").Build());
        }

        var blob = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "packages"),
            "*.nupkg",
            SearchOption.AllDirectories));
        var content = await File.ReadAllBytesAsync(blob);
        content[^1] ^= 0xff;
        await File.WriteAllBytesAsync(blob, content);

        await using var second = new DurablePackageStore(directory.Path);
        var package = await second.FindAsync("Tampered.Package", "1.0.0");

        Assert.NotNull(package);
        var contentError = Assert.Throws<PackageStorageCorruptionException>(
            () => _ = package.Content);
        Assert.Contains("Tampered.Package", contentError.Message, StringComparison.OrdinalIgnoreCase);
        var error = Assert.Throws<PackageStorageCorruptionException>(
            () => package.OpenReadStream());
        Assert.Contains("Tampered.Package", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1.0.0", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_symbol_staging_rolls_back_the_package_blob()
    {
        using var directory = TemporaryDirectory.Create();
        var symbols = TestPackageBuilder.Create("Rollback.Package", "1.0.0")
            .WithFile("lib/net10.0/Rollback.Package.pdb", [1, 2, 3, 4])
            .Build()
            .Content;
        await using var store = new DurablePackageStore(directory.Path);
        await store.AddAsync(TestPackageBuilder.Create("Rollback.Package", "1.0.0").Build());
        await store.AddSymbolAsync(symbols);
        var symbolPath = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "packages"),
            "*.snupkg",
            SearchOption.AllDirectories));
        var pendingSymbolPath = Path.Combine(
            directory.Path,
            "trash",
            Path.GetRelativePath(Path.Combine(directory.Path, "packages"), symbolPath));
        Directory.CreateDirectory(pendingSymbolPath);

        await Assert.ThrowsAsync<IOException>(
            () => store.DeleteAsync("Rollback.Package", "1.0.0").AsTask());

        Assert.NotNull(await store.FindAsync("Rollback.Package", "1.0.0"));
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "packages"),
            "*.nupkg",
            SearchOption.AllDirectories));
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
            Summary = string.Empty,
            Title = string.Empty,
            Authors = string.Empty,
            Tags = string.Empty,
            ProjectUrl = null,
            Readme = string.Empty,
            Icon = string.Empty,
            LicenseExpression = string.Empty,
            LicenseFile = string.Empty,
            LicenseUrl = null,
            PackageTypes = [],
            Repository = null,
            PackageHash = string.Empty,
            RepositoryMetadata = new PackageRepositoryMetadata([], 0, false, null),
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

    private static long[] ReadMigrationVersions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM storage_migrations ORDER BY version;";
        using var reader = command.ExecuteReader();
        var versions = new List<long>();
        while (reader.Read())
        {
            versions.Add(reader.GetInt64(0));
        }

        return versions.ToArray();
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

    private static async Task CreateVersionOneStorageAsync(string root)
    {
        var package = TestPackageBuilder.Create("Schema.Package", "1.0.0")
            .WithDescription("migration searchable metadata")
            .Build();
        var relativeBlob = Path.Combine(
            "packages",
            "schema.package",
            "1.0.0",
            "schema.package.1.0.0.nupkg");
        var fullBlob = Path.Combine(root, relativeBlob);
        Directory.CreateDirectory(Path.GetDirectoryName(fullBlob)!);
        await File.WriteAllBytesAsync(fullBlob, package.Content);

        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "packages.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
                CREATE TABLE packages (
                    id TEXT NOT NULL COLLATE NOCASE,
                    normalized_version TEXT NOT NULL,
                    original_version TEXT NOT NULL,
                    description TEXT NOT NULL,
                    authors TEXT NOT NULL,
                    tags TEXT NOT NULL,
                    nuspec BLOB NOT NULL,
                    published_utc TEXT NOT NULL,
                    is_listed INTEGER NOT NULL CHECK (is_listed IN (0, 1)),
                    content_length INTEGER NOT NULL CHECK (content_length >= 0),
                    blob_path TEXT NOT NULL UNIQUE,
                    sha256 BLOB NOT NULL CHECK (length(sha256) = 32),
                    PRIMARY KEY (id, normalized_version)
                );
                CREATE TABLE storage_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                );
                INSERT INTO storage_migrations(version, applied_utc)
                VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                INSERT INTO packages (
                    id, normalized_version, original_version, description, authors, tags,
                    nuspec, published_utc, is_listed, content_length, blob_path, sha256)
                VALUES (
                    $id, $normalizedVersion, $originalVersion, $description, $authors, $tags,
                    $nuspec, $published, 1, $length, $blobPath, $sha256);
                PRAGMA user_version = 1;
            """;
        command.Parameters.AddWithValue("$id", package.Identity.Id);
        command.Parameters.AddWithValue("$normalizedVersion", package.NormalizedVersion);
        command.Parameters.AddWithValue("$originalVersion", package.Identity.Version.ToFullString());
        command.Parameters.AddWithValue("$description", package.Description);
        command.Parameters.AddWithValue("$authors", package.Authors);
        command.Parameters.AddWithValue("$tags", package.Tags);
        command.Parameters.AddWithValue("$nuspec", package.NuspecContent);
        command.Parameters.AddWithValue("$published", package.Published.ToString("O"));
        command.Parameters.AddWithValue("$length", package.ContentLength);
        command.Parameters.AddWithValue("$blobPath", relativeBlob);
        command.Parameters.AddWithValue("$sha256", SHA256.HashData(package.Content));
        command.ExecuteNonQuery();
        package.Dispose();
    }

    private static string ReadQueryPlan(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
        {
            details.Add(reader.GetString(3));
        }

        return string.Join(Environment.NewLine, details);
    }
}
