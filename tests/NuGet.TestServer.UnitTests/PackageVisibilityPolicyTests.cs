using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class PackageVisibilityPolicyTests
{
    public static TheoryData<int, int, bool> VisibilityMatrix
    {
        get
        {
            var data = new TheoryData<int, int, bool>();
            foreach (var state in Enum.GetValues<PackageLifecycleState>())
            {
                foreach (var resourceClass in Enum.GetValues<PackageResourceClass>())
                {
                    var expected = resourceClass == PackageResourceClass.Administrative ||
                        state == PackageLifecycleState.Published ||
                        state == PackageLifecycleState.Unlisted &&
                        resourceClass != PackageResourceClass.Search;
                    data.Add((int)state, (int)resourceClass, expected);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(VisibilityMatrix))]
    public void Visibility_is_defined_for_every_state_and_resource_class(
        int stateValue,
        int resourceClassValue,
        bool expected)
    {
        var state = (PackageLifecycleState)stateValue;
        var resourceClass = (PackageResourceClass)resourceClassValue;
        Assert.Equal(expected, PackageVisibilityPolicy.Instance.CanRead(state, resourceClass));
    }

    [Theory]
    [InlineData(PackageModerationState.Published, true, (int)PackageLifecycleState.Published)]
    [InlineData(PackageModerationState.Published, false, (int)PackageLifecycleState.Unlisted)]
    [InlineData(PackageModerationState.Quarantined, true, (int)PackageLifecycleState.Quarantined)]
    [InlineData(PackageModerationState.Rejected, true, (int)PackageLifecycleState.Quarantined)]
    [InlineData(PackageModerationState.Deleted, true, (int)PackageLifecycleState.Deleted)]
    public void Existing_durable_fields_map_to_one_lifecycle_state(
        PackageModerationState moderationState,
        bool listed,
        int expectedValue)
    {
        var expected = (PackageLifecycleState)expectedValue;
        Assert.Equal(expected, PackageVisibilityPolicy.Instance.GetState(moderationState, listed));
    }

    [Fact]
    public async Task In_memory_store_applies_the_authoritative_policy()
    {
        await using var store = new InMemoryPackageStore();
        await AssertStoreVisibilityAsync(store);
    }

    [Fact]
    public async Task Durable_store_applies_the_authoritative_policy_across_restart()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var first = new DurablePackageStore(directory.Path))
        {
            await AssertStoreVisibilityAsync(first);
        }

        await using var restarted = new DurablePackageStore(directory.Path);
        Assert.Null(await restarted.FindAsync("Visibility.Package", "1.0.0"));
        Assert.Null(await restarted.FindSymbolAsync("Visibility.Package", "1.0.0"));
        Assert.NotNull(await restarted.FindStoredAsync("Visibility.Package", "1.0.0"));
    }

    private static async Task AssertStoreVisibilityAsync(IPackageStore store)
    {
        var package = TestPackageBuilder.Create("Visibility.Package", "1.0.0").Build();
        var symbols = TestPackageBuilder.Create("Visibility.Package", "1.0.0")
            .WithFile("lib/net10.0/Visibility.Package.pdb", [1, 2, 3, 4])
            .Build();
        await store.AddAsync(package);
        await store.AddSymbolAsync(symbols.Content);

        Assert.NotNull(await store.FindAsync("Visibility.Package", "1.0.0"));
        Assert.Single(await store.FindByIdAsync("Visibility.Package"));
        Assert.Equal(1, (await store.SearchAsync("Visibility.Package", false, 0, 20)).TotalHits);
        Assert.NotNull(await store.FindSymbolAsync("Visibility.Package", "1.0.0"));

        Assert.True(await store.SetListedAsync("Visibility.Package", "1.0.0", false));
        Assert.False((await store.FindAsync("Visibility.Package", "1.0.0"))!.IsListed);
        Assert.Single(await store.FindByIdAsync("Visibility.Package"));
        Assert.Equal(0, (await store.SearchAsync("Visibility.Package", false, 0, 20)).TotalHits);
        Assert.NotNull(await store.FindSymbolAsync("Visibility.Package", "1.0.0"));

        Assert.True(await store.SetModerationStateAsync(
            "Visibility.Package",
            "1.0.0",
            PackageModerationState.Quarantined));
        Assert.Null(await store.FindAsync("Visibility.Package", "1.0.0"));
        Assert.Empty(await store.FindByIdAsync("Visibility.Package"));
        Assert.Equal(0, (await store.SearchAsync("Visibility.Package", false, 0, 20)).TotalHits);
        Assert.Null(await store.FindSymbolAsync("Visibility.Package", "1.0.0"));
        Assert.NotNull(await store.FindStoredAsync("Visibility.Package", "1.0.0"));
        Assert.Contains(
            await store.GetAllStoredAsync(),
            stored => stored.Identity.Id == "Visibility.Package");
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
                "NuGet.TestServer.VisibilityTests",
                Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
