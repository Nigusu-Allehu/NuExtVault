using NuExtVault.Packages;

namespace NuExtVault.UnitTests;

public sealed class PackageSupplyChainServiceTests
{
    [Fact]
    public async Task Quarantine_filters_rich_metadata_and_symbols_and_publication_survives_restart()
    {
        using var directory = TemporaryDirectory.Create();
        var scanner = new BlockingScanner();
        var package = TestPackageBuilder.Create("Integrated.Package", "1.0.0")
            .WithDescription("durable rich metadata")
            .WithSummary("published summary")
            .WithPackageType("DotnetTool", "1.0.0")
            .WithRepository("git", "https://example.test/repository.git", "abc123", "main")
            .Build();
        var symbolPackage = TestPackageBuilder.Create("Integrated.Package", "1.0.0")
            .WithFile("lib/net10.0/Integrated.Package.pdb", [1, 2, 3, 4])
            .Build();

        await using (var store = new DurablePackageStore(directory.Path))
        await using (var supplyChain = new PackageSupplyChainService(
                         store,
                         directory.Path,
                         new SupplyChainOptions(),
                         scanner))
        {
            var publication = supplyChain.PublishAsync(new(
                package,
                "publisher",
                "repository")).AsTask();
            await scanner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await store.AddSymbolAsync(symbolPackage.Content);

            Assert.Null(await store.FindAsync("Integrated.Package", "1.0.0"));
            Assert.Empty(await store.FindByIdAsync("Integrated.Package"));
            Assert.Empty((await store.SearchAsync(
                "Integrated.Package",
                includePrerelease: false,
                skip: 0,
                take: 20)).Items);
            Assert.Null(await store.FindSymbolAsync("Integrated.Package", "1.0.0"));

            scanner.Release.SetResult();
            Assert.Equal(
                PackagePublicationOutcome.Published,
                (await publication).Outcome);
        }

        await using var restartedStore = new DurablePackageStore(directory.Path);
        await using var restartedSupplyChain = new PackageSupplyChainService(
            restartedStore,
            directory.Path);
        var restarted = await restartedStore.FindAsync("Integrated.Package", "1.0.0");

        Assert.NotNull(restarted);
        Assert.Equal("published summary", restarted.Summary);
        Assert.Equal("DotnetTool", Assert.Single(restarted.PackageTypes).Name);
        Assert.Equal("abc123", restarted.Repository?.Commit);
        Assert.NotNull(await restartedStore.FindSymbolAsync("Integrated.Package", "1.0.0"));
        Assert.Equal(
            PackageModerationState.Published,
            (await restartedSupplyChain.GetStatusAsync("Integrated.Package", "1.0.0"))!.State);
    }

    [Fact]
    public async Task Quarantine_is_hidden_until_clean_validation_publishes()
    {
        var inner = new InMemoryPackageStore();
        var scanner = new BlockingScanner();
        await using var supplyChain = new PackageSupplyChainService(
            inner,
            options: new SupplyChainOptions(),
            scanner: scanner);
        var package = TestPackageBuilder.Create("Validated.Package", "1.0.0").Build();

        var publication = supplyChain.PublishAsync(
            new PackagePublicationRequest(package, "publisher", "repository")).AsTask();
        await scanner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(await inner.FindAsync("Validated.Package", "1.0.0"));
        Assert.Equal(
            PackageModerationState.Quarantined,
            (await supplyChain.GetStatusAsync("Validated.Package", "1.0.0"))!.State);

        scanner.Release.SetResult();
        var result = await publication;

        Assert.Equal(PackagePublicationOutcome.Published, result.Outcome);
        Assert.NotNull(await inner.FindAsync("Validated.Package", "1.0.0"));
    }

    [Fact]
    public async Task Malicious_and_unsigned_packages_fail_closed()
    {
        var inner = new InMemoryPackageStore();
        await using var maliciousStore = new PackageSupplyChainService(
            inner,
            options: new SupplyChainOptions(),
            scanner: new FixedScanner(PackageScanOutcome.Malicious));

        var malicious = await maliciousStore.PublishAsync(new(
            TestPackageBuilder.Create("Malicious.Package", "1.0.0").Build(),
            "publisher",
            "repository"));
        var maliciousRetry = await maliciousStore.PublishAsync(new(
            TestPackageBuilder.Create("Malicious.Package", "1.0.0").Build(),
            "publisher",
            "repository"));

        Assert.Equal(PackagePublicationOutcome.Rejected, malicious.Outcome);
        Assert.Equal(PackagePublicationOutcome.Rejected, maliciousRetry.Outcome);
        Assert.Null(await inner.FindAsync("Malicious.Package", "1.0.0"));

        await using var unsignedStore = new PackageSupplyChainService(
            new InMemoryPackageStore(),
            options: new SupplyChainOptions { RequireSignedPackages = true },
            scanner: new FixedScanner(PackageScanOutcome.Clean));
        var unsigned = await unsignedStore.PublishAsync(new(
            TestPackageBuilder.Create("Unsigned.Package", "1.0.0").Build(),
            "publisher",
            "repository"));

        Assert.Equal(PackagePublicationOutcome.Rejected, unsigned.Outcome);
        Assert.Contains("signature", unsigned.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ownership_quota_and_immutable_duplicate_rules_are_atomic()
    {
        await using var store = new PackageSupplyChainService(
            new InMemoryPackageStore(),
            options: new SupplyChainOptions
            {
                MaximumPackagesPerIdentity = 1,
                MaximumPackagesPerRepository = 1
            },
            scanner: new FixedScanner(PackageScanOutcome.Clean));
        var original = TestPackageBuilder.Create("Owned.Package", "1.0.0").Build();
        var sameContent = TestPackage.FromContent(original.Content);
        var changedContent = TestPackageBuilder.Create("Owned.Package", "1.0.0")
            .WithFile("changed.txt", "changed"u8.ToArray())
            .Build();

        var first = await store.PublishAsync(new(original, "owner", "repository"));
        var duplicate = await store.PublishAsync(new(sameContent, "owner", "repository"));
        var changed = await store.PublishAsync(new(changedContent, "owner", "repository"));
        var unauthorized = await store.PublishAsync(new(
            TestPackageBuilder.Create("Owned.Package", "2.0.0").Build(),
            "attacker",
            "other"));
        var overQuota = await store.PublishAsync(new(
            TestPackageBuilder.Create("Other.Package", "1.0.0").Build(),
            "owner",
            "repository"));

        Assert.Equal(PackagePublicationOutcome.Published, first.Outcome);
        Assert.Equal(PackagePublicationOutcome.Duplicate, duplicate.Outcome);
        Assert.Equal(PackagePublicationOutcome.Conflict, changed.Outcome);
        Assert.Equal(PackagePublicationOutcome.Unauthorized, unauthorized.Outcome);
        Assert.Equal(PackagePublicationOutcome.QuotaExceeded, overQuota.Outcome);
    }

    [Fact]
    public async Task Concurrent_first_publish_has_one_owner_and_cannot_exceed_quota()
    {
        await using var store = new PackageSupplyChainService(
            new InMemoryPackageStore(),
            options: new SupplyChainOptions { MaximumPackagesPerRepository = 1 },
            scanner: new FixedScanner(PackageScanOutcome.Clean));

        var results = await Task.WhenAll(
            store.PublishAsync(new(
                TestPackageBuilder.Create("Race.Package", "1.0.0").Build(),
                "first",
                "repository")).AsTask(),
            store.PublishAsync(new(
                TestPackageBuilder.Create("Race.Package", "2.0.0").Build(),
                "second",
                "repository")).AsTask());

        Assert.Single(results, result => result.Outcome == PackagePublicationOutcome.Published);
        Assert.Single(results, result =>
            result.Outcome is PackagePublicationOutcome.Unauthorized or
                PackagePublicationOutcome.QuotaExceeded);
    }

    [Fact]
    public async Task Moderation_validation_and_audit_survive_restart()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var firstStore = new DurablePackageStore(directory.Path))
        await using (var first = new PackageSupplyChainService(
                         firstStore,
                         directory.Path,
                         new SupplyChainOptions(),
                         new FixedScanner(PackageScanOutcome.Malicious)))
        {
            var result = await first.PublishAsync(new(
                TestPackageBuilder.Create("Rejected.Package", "1.0.0").Build(),
                "publisher",
                "repository"));
            Assert.Equal(PackagePublicationOutcome.Rejected, result.Outcome);
        }

        await using var secondStore = new DurablePackageStore(directory.Path);
        await using var second = new PackageSupplyChainService(
            secondStore,
            directory.Path,
            new SupplyChainOptions(),
            new FixedScanner(PackageScanOutcome.Clean));

        Assert.Null(await secondStore.FindAsync("Rejected.Package", "1.0.0"));
        Assert.Equal(
            PackageModerationState.Rejected,
            (await second.GetStatusAsync("Rejected.Package", "1.0.0"))!.State);
        Assert.NotEmpty(await second.GetValidationResultsAsync("Rejected.Package", "1.0.0"));
        Assert.NotEmpty(await second.GetAuditHistoryAsync());

        Assert.True(await second.ModerateAsync(
            "Rejected.Package",
            "1.0.0",
            PackageModerationState.Published,
            "administrator",
            "false positive"));
        Assert.NotNull(await secondStore.FindAsync("Rejected.Package", "1.0.0"));
        Assert.True(await second.DeleteControlledAsync(
            "Rejected.Package",
            "1.0.0",
            "administrator",
            "retention exception"));
        Assert.Null(await secondStore.FindAsync("Rejected.Package", "1.0.0"));
    }

    [Fact]
    public async Task Untracked_blob_after_interrupted_publication_recovers_quarantined()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var initializedStore = new DurablePackageStore(directory.Path))
        await using (var initialized = new PackageSupplyChainService(
                         initializedStore,
                         directory.Path))
        {
        }

        await using (var interrupted = new DurablePackageStore(directory.Path))
        {
            await interrupted.AddAsync(
                TestPackageBuilder.Create("Interrupted.Package", "1.0.0").Build());
        }

        await using var recoveredStore = new DurablePackageStore(directory.Path);
        await using var recovered = new PackageSupplyChainService(
            recoveredStore,
            directory.Path);

        Assert.Null(await recoveredStore.FindAsync("Interrupted.Package", "1.0.0"));
        Assert.Equal(
            PackageModerationState.Quarantined,
            (await recovered.GetStatusAsync("Interrupted.Package", "1.0.0"))!.State);
    }

    [Fact]
    public async Task Missing_policy_database_recovers_durable_packages_quarantined()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var firstStore = new DurablePackageStore(directory.Path))
        await using (var first = new PackageSupplyChainService(
                         firstStore,
                         directory.Path,
                         scanner: new FixedScanner(PackageScanOutcome.Malicious)))
        {
            var result = await first.PublishAsync(new(
                TestPackageBuilder.Create("Rejected.Recovery", "1.0.0").Build(),
                "publisher",
                "repository"));
            Assert.Equal(PackagePublicationOutcome.Rejected, result.Outcome);
        }

        File.Delete(Path.Combine(directory.Path, "supply-chain.db"));

        await using var recoveredStore = new DurablePackageStore(directory.Path);
        await using var recovered = new PackageSupplyChainService(
            recoveredStore,
            directory.Path);

        Assert.Null(await recoveredStore.FindAsync("Rejected.Recovery", "1.0.0"));
        Assert.Equal(
            PackageModerationState.Quarantined,
            (await recovered.GetStatusAsync("Rejected.Recovery", "1.0.0"))!.State);
    }

    [Fact]
    public async Task Search_uses_latest_published_version_and_deleted_tombstones_cannot_republish()
    {
        await using var packageStore = new InMemoryPackageStore();
        await using var store = new PackageSupplyChainService(
            packageStore,
            scanner: new FixedScanner(PackageScanOutcome.Clean));
        await store.PublishAsync(new(
            TestPackageBuilder.Create("Visible.Package", "1.0.0").Build(),
            "publisher",
            "repository"));
        Assert.True(await store.ModerateAsync(
            "Visible.Package",
            "1.0.0",
            PackageModerationState.Published,
            "administrator",
            "approved"));

        await using var quarantinedPackageStore = new InMemoryPackageStore();
        await using var quarantinedStore = new PackageSupplyChainService(
            quarantinedPackageStore,
            scanner: new FixedScanner(PackageScanOutcome.Inconclusive));
        await quarantinedStore.AddAsync(
            TestPackageBuilder.Create("Search.Package", "1.0.0").Build());
        await quarantinedStore.PublishAsync(new(
            TestPackageBuilder.Create("Search.Package", "2.0.0").Build(),
            "publisher",
            "repository",
            Administrator: true));

        var search = await quarantinedPackageStore.SearchAsync(
            "Search.Package",
            includePrerelease: false,
            skip: 0,
            take: 20);
        Assert.Equal(1, search.TotalHits);
        var searchItem = Assert.Single(search.Items);
        Assert.Equal("1.0.0", searchItem.Package.NormalizedVersion);
        Assert.Equal(
            "1.0.0",
            Assert.Single(searchItem.Versions).NormalizedVersion);

        Assert.True(await store.DeleteControlledAsync(
            "Visible.Package",
            "1.0.0",
            "administrator",
            "delete"));
        Assert.False(await store.ModerateAsync(
            "Visible.Package",
            "1.0.0",
            PackageModerationState.Published,
            "administrator",
            "restore"));
    }

    private sealed class FixedScanner(PackageScanOutcome outcome) : IPackagePolicyScanner
    {
        public ValueTask<PackageScanResult> ScanAsync(
            TestPackage package,
            CancellationToken token = default) =>
            ValueTask.FromResult(new PackageScanResult(outcome, outcome.ToString()));
    }

    private sealed class BlockingScanner : IPackagePolicyScanner
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<PackageScanResult> ScanAsync(
            TestPackage package,
            CancellationToken token = default)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(token);
            return new PackageScanResult(PackageScanOutcome.Clean, "clean");
        }
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
                "NuExtVault.SupplyChainTests",
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
