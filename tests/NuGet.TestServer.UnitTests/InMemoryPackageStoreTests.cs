using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

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
    public async Task Search_returns_only_the_latest_allowed_listed_version()
    {
        var store = new InMemoryPackageStore();
        await store.AddAsync(TestPackageBuilder.Create("Example.Logging", "1.0.0").Build());
        await store.AddAsync(TestPackageBuilder.Create("Example.Logging", "2.0.0-beta.1").Build());
        await store.AddAsync(TestPackageBuilder.Create("Other", "1.0.0").Build());

        var stable = await store.SearchAsync("logging", includePrerelease: false, skip: 0, take: 20);
        var prerelease = await store.SearchAsync("logging", includePrerelease: true, skip: 0, take: 20);

        Assert.Equal("1.0.0", Assert.Single(stable).NormalizedVersion);
        Assert.Equal("2.0.0-beta.1", Assert.Single(prerelease).NormalizedVersion);
    }

    [Fact]
    public async Task File_backed_store_persists_packages_and_listing_state()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "NuGet.TestServer.UnitTests",
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
}
