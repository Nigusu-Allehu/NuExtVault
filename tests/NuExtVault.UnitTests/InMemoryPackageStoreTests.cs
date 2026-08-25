using NuExtVault.Packages;

namespace NuExtVault.UnitTests;

public sealed class InMemoryPackageStoreTests
{
    [Fact]
    public async Task Store_is_case_insensitive_and_orders_versions()
    {
        var store = new InMemoryPackageStore();
        await store.AddAsync(TestPackageBuilder.Create("Example.Package", "2.0.0").Build());
        await store.AddAsync(TestPackageBuilder.Create("example.package", "1.0.0").Build());

        var packages = await store.FindByIdAsync("EXAMPLE.PACKAGE");

        Assert.Equal(["1.0.0", "2.0.0"], packages.Select(p => p.NormalizedVersion));
    }

    [Fact]
    public async Task Store_rejects_duplicates_and_can_unlist_and_reset()
    {
        var store = new InMemoryPackageStore();
        var package = TestPackageBuilder.Create("Example", "1.0.0").Build();
        await store.AddAsync(package);

        await Assert.ThrowsAsync<DuplicatePackageException>(() => store.AddAsync(package).AsTask());

        Assert.True(await store.SetListedAsync("Example", "1.0.0", false));
        Assert.False((await store.FindAsync("example", "1.0.0"))!.IsListed);

        await store.ResetAsync();
        Assert.Empty(await store.FindByIdAsync("Example"));
    }

    [Fact]
    public async Task Search_returns_all_applicable_versions_and_total_before_pagination()
    {
        var store = new InMemoryPackageStore();
        await store.AddAsync(TestPackageBuilder.Create("Example.Logging", "1.0.0").Build());
        await store.AddAsync(TestPackageBuilder.Create("Example.Logging", "1.5.0").Build());
        await store.AddAsync(TestPackageBuilder.Create("Example.Logging", "2.0.0-beta.1").Build());
        await store.AddAsync(TestPackageBuilder.Create("Example.Tracing", "1.0.0").WithTags("logging").Build());
        await store.AddAsync(TestPackageBuilder.Create("Other", "1.0.0").WithDescription("LOGGING").Build());
        await store.AddAsync(TestPackageBuilder.Create("Unlisted.Logging", "1.0.0").Build());
        await store.SetListedAsync("Unlisted.Logging", "1.0.0", false);

        var stable = await store.SearchAsync("LoGgInG", includePrerelease: false, skip: 1, take: 1);
        var prerelease = await store.SearchAsync("logging", includePrerelease: true, skip: 0, take: 20);

        Assert.Equal(3, stable.TotalHits);
        var stableResult = Assert.Single(stable.Items);
        Assert.Equal("Example.Tracing", stableResult.Package.Identity.Id);
        Assert.Equal(["1.0.0"], stableResult.Versions.Select(package => package.NormalizedVersion));

        Assert.Equal(3, prerelease.TotalHits);
        Assert.Equal(
            ["Example.Logging", "Example.Tracing", "Other"],
            prerelease.Items.Select(result => result.Package.Identity.Id));
        var logging = prerelease.Items[0];
        Assert.Equal("2.0.0-beta.1", logging.Package.NormalizedVersion);
        Assert.Equal(
            ["1.0.0", "1.5.0", "2.0.0-beta.1"],
            logging.Versions.Select(package => package.NormalizedVersion));
    }

    [Fact]
    public async Task Empty_search_has_deterministic_case_insensitive_ordering_and_offsets()
    {
        var store = new InMemoryPackageStore();
        await store.AddAsync(TestPackageBuilder.Create("zulu", "1.0.0").Build());
        await store.AddAsync(TestPackageBuilder.Create("Alpha", "1.0.0").Build());
        await store.AddAsync(TestPackageBuilder.Create("bravo", "1.0.0").Build());

        var firstPage = await store.SearchAsync(string.Empty, includePrerelease: false, skip: 0, take: 2);
        var secondPage = await store.SearchAsync(string.Empty, includePrerelease: false, skip: 2, take: 2);

        Assert.Equal(3, firstPage.TotalHits);
        Assert.Equal(["Alpha", "bravo"], firstPage.Items.Select(result => result.Package.Identity.Id));
        Assert.Equal(3, secondPage.TotalHits);
        Assert.Equal(["zulu"], secondPage.Items.Select(result => result.Package.Identity.Id));
    }

    [Fact]
    public async Task Search_filters_explicit_and_implicit_package_types()
    {
        var store = new InMemoryPackageStore();
        await store.AddAsync(TestPackageBuilder.Create("Library", "1.0.0").Build());
        await store.AddAsync(
            TestPackageBuilder.Create("Tool", "1.0.0")
                .WithPackageType("DotnetTool", "1.0.0")
                .Build());

        var dependencies = await store.SearchAsync(
            string.Empty,
            includePrerelease: false,
            skip: 0,
            take: 20,
            packageType: "dependency");
        var tools = await store.SearchAsync(
            string.Empty,
            includePrerelease: false,
            skip: 0,
            take: 20,
            packageType: "DOTNETTOOL");

        Assert.Equal(["Library"], dependencies.Items.Select(item => item.Package.Identity.Id));
        Assert.Equal(["Tool"], tools.Items.Select(item => item.Package.Identity.Id));
    }

    [Fact]
    public async Task File_backed_store_persists_packages_and_listing_state()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "NuExtVault.UnitTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var first = new InMemoryPackageStore(directory);
            await first.AddAsync(TestPackageBuilder.Create("Persistent.Package", "1.0.0").Build());
            await first.SetListedAsync("Persistent.Package", "1.0.0", false);

            var second = new InMemoryPackageStore(directory);
            var restored = await second.FindAsync("persistent.package", "1.0.0");

            Assert.NotNull(restored);
            Assert.False(restored.IsListed);

            Assert.True(await second.DeleteAsync("Persistent.Package", "1.0.0"));
            var third = new InMemoryPackageStore(directory);
            Assert.Null(await third.FindAsync("Persistent.Package", "1.0.0"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Store_updates_and_persists_repository_metadata()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "NuExtVault.UnitTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var first = new InMemoryPackageStore(directory);
            await first.AddAsync(TestPackageBuilder.Create("Persistent.Package", "1.0.0").Build());
            var update = new PackageRepositoryMetadata(
                ["Alice", "Bob"],
                Downloads: 42,
                Verified: true,
                new PackageDeprecation(
                    ["Legacy", "Other"],
                    "Use the replacement.",
                    new AlternatePackage("Replacement.Package", "[2.0.0,)")));

            Assert.True(await first.SetRepositoryMetadataAsync(
                "Persistent.Package",
                "1.0.0",
                update));

            var restored = await new InMemoryPackageStore(directory)
                .FindAsync("persistent.package", "1.0.0");
            Assert.Equal(["Alice", "Bob"], restored!.RepositoryMetadata.Owners);
            Assert.Equal(42, restored.RepositoryMetadata.Downloads);
            Assert.True(restored.RepositoryMetadata.Verified);
            Assert.Equal(
                "Replacement.Package",
                restored.RepositoryMetadata.Deprecation!.AlternatePackage!.Id);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Reset_removes_staged_streamed_package_files()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "NuExtVault.UnitTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var built = TestPackageBuilder.Create("Staged.Package", "1.0.0").Build();
            await using var content = new MemoryStream(built.Content);
            var limits = new PackageTransferLimits { TemporaryDirectory = directory };
            var package = await TestPackage.FromStreamAsync(content, limits);
            var store = new InMemoryPackageStore(limits: limits);
            await store.AddAsync(package);
            Assert.Single(Directory.EnumerateFiles(directory, "*.tmp"));

            await store.ResetAsync();

            Assert.Empty(Directory.EnumerateFiles(directory));
            await store.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
