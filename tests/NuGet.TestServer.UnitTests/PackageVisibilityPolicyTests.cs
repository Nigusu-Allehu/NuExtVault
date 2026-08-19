using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class PackageVisibilityPolicyTests
{
    public static TheoryData<int, bool, int, bool> VisibilityMatrix
    {
        get
        {
            var data = new TheoryData<int, bool, int, bool>();
            foreach (var moderationState in Enum.GetValues<PackageModerationState>())
            {
                foreach (var listed in new[] { false, true })
                {
                    foreach (var resourceClass in Enum.GetValues<PackageResourceClass>())
                    {
                        var expected = moderationState == PackageModerationState.Published &&
                            (listed || resourceClass != PackageResourceClass.Search);
                        data.Add(
                            (int)moderationState,
                            listed,
                            (int)resourceClass,
                            expected);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(VisibilityMatrix))]
    public void Visibility_is_defined_for_every_authority_fact_and_resource_class(
        int moderationStateValue,
        bool listed,
        int resourceClassValue,
        bool expected)
    {
        var facts = new PackageAuthorityFacts(
            (PackageModerationState)moderationStateValue,
            listed);
        var resourceClass = (PackageResourceClass)resourceClassValue;
        Assert.Equal(expected, PackageVisibilityPolicy.Instance.CanRead(facts, resourceClass));
    }

    [Fact]
    public void Unknown_authority_facts_and_resource_classes_fail_closed()
    {
        var policy = PackageVisibilityPolicy.Instance;

        Assert.All(
            Enum.GetValues<PackageResourceClass>(),
            resourceClass => Assert.False(policy.CanRead(
                new PackageAuthorityFacts((PackageModerationState)int.MaxValue, IsListed: true),
                resourceClass)));
        Assert.False(policy.CanRead(
            new PackageAuthorityFacts(PackageModerationState.Published, IsListed: true),
            (PackageResourceClass)int.MaxValue));
    }

    [Fact]
    public void Immutable_grant_sets_can_model_independently_differing_resource_classes()
    {
        var grants = PackagePublicGrantSet.Create([PackageResourceClass.Registration]);

        Assert.True(grants.Contains(PackageResourceClass.Registration));
        Assert.False(grants.Contains(PackageResourceClass.VersionEnumeration));
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
        var restartedCandidates = Assert.IsAssignableFrom<IPackageCandidateStore>(restarted);
        Assert.Null(await restarted.FindAsync("Visibility.Package", "1.0.0"));
        Assert.Null(await restarted.FindSymbolAsync("Visibility.Package", "1.0.0"));
        Assert.NotNull(await restarted.FindStoredAsync("Visibility.Package", "1.0.0"));
        Assert.Single(await restartedCandidates.FindStoredByIdAsync("Visibility.Package"));
    }

    private static async Task AssertStoreVisibilityAsync(IPackageStore store)
    {
        var candidates = Assert.IsAssignableFrom<IPackageCandidateStore>(store);
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
        Assert.Single(await candidates.FindStoredByIdAsync("Visibility.Package"));
        Assert.Contains(
            await store.GetAllStoredAsync(),
            stored => stored.Identity.Id == "Visibility.Package");

        await store.AddAsync(TestPackageBuilder.Create("Search.Listed", "1.0.0").Build());
        await store.AddAsync(TestPackageBuilder.Create("Search.Unlisted", "1.0.0").Build());
        await store.AddAsync(TestPackageBuilder.Create("Search.Hidden", "1.0.0").Build());
        Assert.True(await store.SetListedAsync("Search.Unlisted", "1.0.0", false));
        Assert.True(await store.SetModerationStateAsync(
            "Search.Hidden",
            "1.0.0",
            PackageModerationState.Quarantined));

        var search = await store.SearchAsync("Search.", false, 0, 20);

        Assert.Equal(1, search.TotalHits);
        Assert.Equal("Search.Listed", Assert.Single(search.Items).Package.Identity.Id);
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
