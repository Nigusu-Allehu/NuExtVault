using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NuExtVault.Authentication;
using NuExtVault.Extensions.Sdk;
using NuExtVault.ExternalExtensionTestKit;
using NuExtVault.Hosting;
using NuExtVault.Kernel.Capabilities;
using NuExtVault.Operations;
using NuExtVault.Packages;

namespace NuExtVault.UnitTests;

/// <summary>
/// Step 22 ("Package Staging as reference external extension") unit coverage. The
/// staging extension stays external-only, absent by default, and unrecognized by the
/// kernel; the kernel-owned staged content store, publication journal, promotion
/// coordinator, and generic manifest state registration are proven directly.
/// </summary>
[Collection(nameof(PackageStagingAssetsCollection))]
public sealed class PackageStagingExtensionTests(PackageStagingAssetsFixture fixture)
{
    private ContosoFlavorsAssets Assets => fixture.StagingAssets;

    // ---- external-only / default-absent / no-recognition fitness gate ------

    [Theory]
    [InlineData("embedded")]
    [InlineData("standard")]
    [InlineData("production")]
    public void No_default_profile_includes_the_staging_extension(string profileName)
    {
        var profile = profileName switch
        {
            "embedded" => ServerProfiles.Embedded,
            "standard" => ServerProfiles.Standard,
            _ => ServerProfiles.Production
        };

        Assert.DoesNotContain(
            profile.Extensions,
            extension => extension.Id.Contains("Staging", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Default_composition_has_no_staging_routes_or_resources()
    {
        var composition = ServerComposition.Create(
            ServerProfiles.Embedded,
            authentication: AuthenticationConfiguration.Anonymous);

        Assert.DoesNotContain(
            composition.ExtensionGraph.Routes.Select(route => route.Path),
            path => path.Contains("staging", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            composition.ExtensionGraph.Resources.Select(
                resource => resource.Contribution.ResourceType),
            type => type.Contains("Staging", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_kernel_has_no_package_staging_recognition()
    {
        var builtInExtensionIds = typeof(BuiltInExtensionIds)
            .GetFields(System.Reflection.BindingFlags.Public |
                       System.Reflection.BindingFlags.NonPublic |
                       System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string?)field.GetValue(null))
            .ToArray();

        Assert.DoesNotContain(
            builtInExtensionIds,
            id => id?.Contains("Staging", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(
            OperationContracts.All.Select(contract => contract.Id.Value),
            id => id.Contains("Staging", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            KernelStateParticipants.BuiltIn.Select(participant => participant.ExtensionId),
            id => id.Contains("Staging", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_staging_assembly_references_only_the_public_sdk()
    {
        var assemblyPath = Path.Combine(
            RepositoryPaths.RepositoryRoot,
            "src",
            "NuExtVault.Extensions.PackageStaging",
            "bin",
            "Release",
            "net10.0",
            PackageStagingAssets.EntryAssemblyFileName);
        Assert.True(File.Exists(assemblyPath), $"Expected a packed assembly at '{assemblyPath}'.");

        var references = System.Reflection.Assembly
            .LoadFrom(assemblyPath)
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("NuExtVault.Extensions.Sdk", references);
        Assert.DoesNotContain("NuExtVault.Kernel", references);
        Assert.DoesNotContain("NuExtVault.Extensions.Official", references);
        Assert.DoesNotContain("NuExtVault", references);
    }

    // ---- manifest ---------------------------------------------------------

    [Fact]
    public void The_staging_manifest_is_schema_v1_valid()
    {
        var result = ExtensionManifestJson.Validate(Assets.ManifestJsonBytes);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Fact]
    public void The_staging_manifest_declares_admin_access_on_every_route()
    {
        var manifest = ExtensionManifestJson.Parse(Assets.ManifestJsonBytes);

        Assert.NotEmpty(manifest.Routes);
        Assert.All(manifest.Routes, route => Assert.Equal("admin", route.Access));
    }

    [Fact]
    public void The_staging_manifest_declares_state_capabilities_and_a_resource_route()
    {
        var manifest = ExtensionManifestJson.Parse(Assets.ManifestJsonBytes);
        var required = manifest.Capabilities
            .Where(capability => capability.Requirement == CapabilityRequirement.Required)
            .Select(capability => capability.Identity.Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(new ExtensionStateDeclaration("package-staging", 1, true), manifest.State);
        Assert.Contains(BuiltInCapabilityNames.HostClockRead, required);
        Assert.Contains(BuiltInCapabilityNames.ExtensionStateRead, required);
        Assert.Contains(BuiltInCapabilityNames.ExtensionStateWrite, required);
        Assert.Contains(BuiltInCapabilityNames.PackageContentWriteStaged, required);
        Assert.Contains(BuiltInCapabilityNames.PublicationRequest, required);
        var contribution = Assert.Single(manifest.Contributions);
        Assert.Equal("service-resource", contribution.Kind);
        Assert.Equal(new RouteIdentity("nuextvault.staging.list-groups"), contribution.Route);
    }

    [Fact]
    public void The_staging_manifest_declares_its_signed_pre_rename_identity()
    {
        var manifest = ExtensionManifestJson.Parse(Assets.ManifestJsonBytes);

        Assert.Equal(["NuTest.PackageStaging"], manifest.IdentityPredecessors.ToArray());
    }

    [Fact]
    public void The_staging_manifest_binds_uploads_to_streaming_bodies()
    {
        var manifest = ExtensionManifestJson.Parse(Assets.ManifestJsonBytes);

        Assert.Equal(
            RouteBodyBinding.Stream,
            Route(manifest, "nuextvault.staging.upload-package").Body);
        Assert.Equal(
            RouteBodyBinding.None,
            Route(manifest, "nuextvault.staging.list-groups").Body);
        Assert.Equal(
            RouteBodyBinding.Bounded,
            Route(manifest, "nuextvault.staging.create-group").Body);
        Assert.Contains(
            "Idempotency-Key",
            Route(manifest, "nuextvault.staging.upload-package").Headers);
    }

    [Fact]
    public void The_staging_manifest_sdk_range_matches_the_current_sdk()
    {
        var manifest = ExtensionManifestJson.Parse(Assets.ManifestJsonBytes);

        Assert.Equal(new SdkContractVersion(1, 3, 0), manifest.Sdk.Minimum);
        Assert.True(ExtensionSdkVersions.IsSupported(manifest.Sdk.Minimum));
        Assert.True(ExtensionSdkVersions.IsSupported(ExtensionSdkVersions.OldestSupported));
    }

    // ---- loader -----------------------------------------------------------

    [Fact]
    public void A_correctly_signed_staging_package_loads_through_the_trusted_loader()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey(
            publisher: PackageStagingAssets.Publisher);
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "staging.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));

        using var runtime = ExternalExtensionPackageLoader.Load(
            new ExternalExtensionConfiguration([.. roots.Roots], [trustRoot], TimeProvider.System));

        var result = Assert.Single(runtime.Diagnostics.Results);
        Assert.True(result.Succeeded, result.RedactedMessage);
        Assert.Equal(
            PackageStagingAssets.Id,
            Assert.Single(runtime.Modules).Contribution.Manifest.Identity.Id);
    }

    [Fact]
    public void A_staging_package_without_a_trust_root_is_rejected()
    {
        var (key, _) = ConformanceAttestationFixture.CreateTrustedKey(
            publisher: PackageStagingAssets.Publisher);
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "staging.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));

        using var runtime = ExternalExtensionPackageLoader.Load(
            new ExternalExtensionConfiguration([.. roots.Roots], [], TimeProvider.System));

        Assert.False(Assert.Single(runtime.Diagnostics.Results).Succeeded);
    }

    [Fact]
    public void A_staging_package_with_a_tampered_attestation_is_rejected()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey(
            publisher: PackageStagingAssets.Publisher);
        var valid = ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key);
        var entries = ExternalExtensionPackageBuilder.ReadEntries(valid);
        var tampered = ExternalExtensionPackageBuilder.WithEntry(
            valid,
            ExternalExtensionPackageBuilder.AttestationEntryName,
            ConformanceAttestationFixture.Tamper(
                entries[ExternalExtensionPackageBuilder.AttestationEntryName]));
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage("staging.nupkg", tampered);

        using var runtime = ExternalExtensionPackageLoader.Load(
            new ExternalExtensionConfiguration([.. roots.Roots], [trustRoot], TimeProvider.System));

        Assert.False(Assert.Single(runtime.Diagnostics.Results).Succeeded);
    }

    [Fact]
    public async Task Owner_migration_does_not_run_until_the_storage_lease_is_acquired()
    {
        const string legacyOwner = "NuTest.PackageStaging";
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey(
            publisher: PackageStagingAssets.Publisher);
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "staging.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));
        using var storage = new TemporaryDirectory();
        string contentId;
        using (var content = new StagedContentStore(storage.Path, "legacy-host"))
        {
            contentId = (await content.WriteAsync(
                legacyOwner,
                new MemoryStream("legacy-content"u8.ToArray()),
                "application/octet-stream",
                1024,
                "Contoso.Legacy",
                "1.0.0",
                CancellationToken.None)).Record!.ContentId;
        }

        using var lease = new FileStream(
            Path.Combine(storage.Path, ".storage.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.Throws<PackageStorageInUseException>(() =>
        {
            _ = ServerApplication.Build(
                ServerComposition.Create(
                    StagingProfile(),
                    storageDirectory: storage.Path,
                    authentication: AuthenticationConfiguration.Anonymous,
                    externalExtensions: new ExternalExtensionConfiguration(
                        [.. roots.Roots],
                        [trustRoot],
                        TimeProvider.System)));
        });

        using var unchanged = new StagedContentStore(storage.Path, "probe");
        Assert.NotNull(unchanged.Find(legacyOwner, contentId));
        Assert.Null(unchanged.Find(PackageStagingAssets.Id, contentId));
    }

    // ---- generic manifest state registration -------------------------------

    [Fact]
    public async Task A_manifest_declared_state_schema_is_registered_with_the_kernel_store()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey(
            publisher: PackageStagingAssets.Publisher);
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "staging.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(Assets, key));
        await using var application = ServerApplication.Build(
            ServerComposition.Create(
                StagingProfile(),
                authentication: AuthenticationConfiguration.Anonymous,
                externalExtensions: new ExternalExtensionConfiguration(
                    [.. roots.Roots],
                    [trustRoot],
                    TimeProvider.System)));

        var store = application.Services.GetRequiredService<TransactionalStateStore>();

        var participant = Assert.Single(
            store.Participants.Where(candidate =>
                candidate.ExtensionId == PackageStagingAssets.Id));
        Assert.Equal("package-staging", participant.SchemaName);
        Assert.Equal(1, participant.SchemaVersion);
        Assert.True(participant.Required);
        var record = await store.WriteAsync(
            PackageStagingAssets.Id,
            "group.probe",
            "{}"u8.ToArray(),
            null,
            CancellationToken.None);
        Assert.Equal("package-staging", record.SchemaName);
    }

    [Fact]
    public void An_extension_without_a_state_declaration_registers_no_schema()
    {
        using var application = ServerApplication.Build(
            ServerComposition.Create(
                ServerProfiles.Embedded,
                authentication: AuthenticationConfiguration.Anonymous));

        var store = application.Services.GetRequiredService<TransactionalStateStore>();

        Assert.Equal(
            KernelStateParticipants.BuiltIn.Select(p => p.ExtensionId).Order(StringComparer.Ordinal),
            store.Participants.Select(p => p.ExtensionId).Order(StringComparer.Ordinal));
    }

    internal static ServerProfile StagingProfile() =>
        ServerProfiles.Embedded with
        {
            Grants =
            [
                .. ServerProfiles.Embedded.Grants,
                new CapabilityGrant(BuiltInCapabilityNames.HostClockRead),
                new CapabilityGrant(BuiltInCapabilityNames.ExtensionStateRead),
                new CapabilityGrant(BuiltInCapabilityNames.ExtensionStateWrite),
                new CapabilityGrant(BuiltInCapabilityNames.PackageContentWriteStaged),
                new CapabilityGrant(BuiltInCapabilityNames.PublicationRequest)
            ]
        };

    private static RouteDeclaration Route(ExtensionManifest manifest, string id) =>
        manifest.Routes.Single(route => route.Identity.Value == id);
}

/// <summary>Kernel-owned staged content store behavior.</summary>
public sealed class StagedContentStoreTests
{
    private const string Owner = "NuExtVault.PackageStaging";

    [Fact]
    public async Task An_in_memory_store_is_private_to_its_host_instance()
    {
        using var first = new StagedContentStore(null, "host-a");
        using var second = new StagedContentStore(null, "host-b");

        var written = await first.WriteAsync(
            Owner, Content("one"), "application/octet-stream", 1024, null, null,
            CancellationToken.None);

        Assert.Equal(StagedContentWriteStatus.Succeeded, written.Status);
        Assert.NotNull(first.Find(Owner, written.Record!.ContentId));
        Assert.Null(second.Find(Owner, written.Record.ContentId));
        Assert.Empty(second.Records);
    }

    [Fact]
    public async Task Staged_content_is_bound_to_the_extension_that_staged_it()
    {
        using var store = new StagedContentStore(null, "host");
        var written = await store.WriteAsync(
            Owner, Content("one"), "application/octet-stream", 1024, null, null,
            CancellationToken.None);

        Assert.Null(store.Find("Other.Extension", written.Record!.ContentId));
        Assert.False(await store.TransitionAsync(
            "Other.Extension",
            written.Record.ContentId,
            StagedContentState.Released,
            CancellationToken.None));
        Assert.Equal(
            StagedContentState.Staged,
            store.Find(Owner, written.Record.ContentId)!.State);
    }

    [Fact]
    public async Task Staged_content_records_the_integrity_the_kernel_streamed()
    {
        using var store = new StagedContentStore(null, "host");

        var written = await store.WriteAsync(
            Owner, Content("payload"), "application/octet-stream", 1024, null, null,
            CancellationToken.None);

        Assert.Equal(
            Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData("payload"u8.ToArray())),
            written.Record!.ContentSha256);
        Assert.Equal(7, written.Record.ContentLength);
    }

    [Fact]
    public async Task Content_above_the_declared_limit_is_rejected_and_nothing_is_retained()
    {
        var root = CreateStorage();
        using var store = new StagedContentStore(root, "host");

        var written = await store.WriteAsync(
            Owner, Content(new string('x', 4096)), "application/octet-stream", 128, null, null,
            CancellationToken.None);

        Assert.Equal(StagedContentWriteStatus.ContentTooLarge, written.Status);
        Assert.Empty(store.Records);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.Combine(root, StagedContentStore.DirectoryName)),
            file => !file.EndsWith("index.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_owner_record_quota_rejects_further_staging()
    {
        using var store = new StagedContentStore(
            null,
            "host",
            new StagedContentQuotas(MaximumRecordsPerOwner: 2));

        await store.WriteAsync(
            Owner, Content("a"), "application/octet-stream", 64, null, null, CancellationToken.None);
        await store.WriteAsync(
            Owner, Content("b"), "application/octet-stream", 64, null, null, CancellationToken.None);
        var third = await store.WriteAsync(
            Owner, Content("c"), "application/octet-stream", 64, null, null, CancellationToken.None);

        Assert.Equal(StagedContentWriteStatus.QuotaExceeded, third.Status);
    }

    [Fact]
    public async Task Expired_leases_are_reclaimed_and_their_bytes_freed()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch);
        var root = CreateStorage();
        using var store = new StagedContentStore(
            root,
            "host",
            new StagedContentQuotas(DefaultLease: TimeSpan.FromMinutes(5)),
            clock);
        var written = await store.WriteAsync(
            Owner, Content("a"), "application/octet-stream", 64, null, null, CancellationToken.None);
        Assert.False(store.IsExpired(written.Record!));

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.True(store.IsExpired(store.Find(Owner, written.Record!.ContentId)!));
        Assert.Equal(1, await store.ReclaimExpiredAsync(CancellationToken.None));
        Assert.Null(store.Find(Owner, written.Record.ContentId));
        Assert.False(File.Exists(Path.Combine(
            root,
            StagedContentStore.DirectoryName,
            written.Record.ContentId + ".bin")));
    }

    [Fact]
    public async Task A_durable_store_survives_restart_and_drops_orphans()
    {
        var root = CreateStorage();
        string contentId;
        using (var store = new StagedContentStore(root, "host-1"))
        {
            var written = await store.WriteAsync(
                Owner, Content("durable"), "application/octet-stream", 64, "Contoso", "1.0.0",
                CancellationToken.None);
            contentId = written.Record!.ContentId;
        }

        var orphan = Path.Combine(
            root,
            StagedContentStore.DirectoryName,
            Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllTextAsync(orphan, "orphan");

        using var restarted = new StagedContentStore(root, "host-2");

        var record = restarted.Find(Owner, contentId);
        Assert.NotNull(record);
        Assert.Equal("Contoso", record!.PackageId);
        Assert.Equal(StagedContentState.Staged, record.State);
        await using var stream = restarted.Open(record);
        using var reader = new StreamReader(stream);
        Assert.Equal("durable", await reader.ReadToEndAsync());
        Assert.False(File.Exists(orphan));
    }

    [Fact]
    public async Task A_transition_is_terminal_and_not_repeatable()
    {
        using var store = new StagedContentStore(null, "host");
        var written = await store.WriteAsync(
            Owner, Content("a"), "application/octet-stream", 64, null, null, CancellationToken.None);

        Assert.True(await store.TransitionAsync(
            Owner, written.Record!.ContentId, StagedContentState.Promoted, CancellationToken.None));
        Assert.False(await store.TransitionAsync(
            Owner, written.Record.ContentId, StagedContentState.Released, CancellationToken.None));
        Assert.Null(store.Find(Owner, written.Record.ContentId));
    }

    [Fact]
    public async Task Cancellation_leaves_no_staged_record()
    {
        var root = CreateStorage();
        using var store = new StagedContentStore(root, "host");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.WriteAsync(
                Owner,
                Content("a"),
                "application/octet-stream",
                64,
                null,
                null,
                cancellation.Token));

        Assert.Empty(store.Records);
    }

    internal static string CreateStorage()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "nuextvault-staging-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Stream Content(string value) =>
        new MemoryStream(Encoding.UTF8.GetBytes(value), writable: false);
}

/// <summary>Kernel-owned publication journal behavior.</summary>
public sealed class PublicationJournalTests
{
    [Fact]
    public async Task The_same_idempotency_key_is_journalled_once()
    {
        using var journal = new PublicationJournal(null);

        var (first, createdFirst) = await journal.BeginAsync(Entry("key-1"), CancellationToken.None);
        var (second, createdSecond) = await journal.BeginAsync(Entry("key-1"), CancellationToken.None);

        Assert.True(createdFirst);
        Assert.False(createdSecond);
        Assert.Equal(first.EntryId, second.EntryId);
    }

    [Fact]
    public async Task Different_owners_never_share_an_idempotency_key()
    {
        using var journal = new PublicationJournal(null);

        await journal.BeginAsync(Entry("key-1"), CancellationToken.None);
        var (_, created) = await journal.BeginAsync(
            Entry("key-1") with { OwnerId = "Other.Extension" },
            CancellationToken.None);

        Assert.True(created);
    }

    [Fact]
    public async Task An_aborted_entry_never_consumes_the_idempotency_key()
    {
        using var journal = new PublicationJournal(null);
        var (entry, _) = await journal.BeginAsync(Entry("key-1"), CancellationToken.None);

        await journal.AbortAsync(entry, CancellationToken.None);

        Assert.Null(journal.Find(entry.OwnerId, "key-1"));
    }

    [Fact]
    public async Task Unfinished_entries_survive_restart_and_committed_ones_do_not_reappear()
    {
        var root = StagedContentStoreTests.CreateStorage();
        using (var journal = new PublicationJournal(root))
        {
            var (pending, _) = await journal.BeginAsync(Entry("pending"), CancellationToken.None);
            var (resolved, _) = await journal.BeginAsync(Entry("resolved"), CancellationToken.None);
            var (committed, _) = await journal.BeginAsync(Entry("committed"), CancellationToken.None);
            await journal.ResolveAsync(resolved, "Published", null, CancellationToken.None);
            await journal.ResolveAsync(committed, "Published", null, CancellationToken.None);
            await journal.CommitAsync(committed, 7, CancellationToken.None);
            Assert.Equal(PublicationJournalPhase.Pending, pending.Phase);
        }

        using var restarted = new PublicationJournal(root);

        Assert.Equal(
            ["pending", "resolved"],
            restarted.ReadUnfinished()
                .Select(entry => entry.IdempotencyKey)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(7, restarted.Find("NuExtVault.PackageStaging", "committed")!.StateToken);
    }

    private static PublicationJournalEntry Entry(string idempotencyKey) =>
        new(
            Guid.NewGuid().ToString("N"),
            "NuExtVault.PackageStaging",
            idempotencyKey,
            "content-1",
            null,
            "group.one",
            3,
            "Contoso.Sample",
            "1.0.0",
            new string('a', 64),
            PublicationJournalPhase.Pending,
            "Failed",
            null,
            null,
            Convert.ToBase64String("{}"u8.ToArray()),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
}

/// <summary>Promotion behavior of the kernel coordinator, without a transport.</summary>
public sealed class StagedPublicationCoordinatorTests
{
    private const string Owner = "NuExtVault.PackageStaging";

    [Fact]
    public async Task A_malformed_archive_is_rejected_and_nothing_stays_staged()
    {
        await using var harness = Harness.Create();

        var result = await harness.Coordinator.StagePackageAsync(
            Owner,
            new MemoryStream("not a nupkg"u8.ToArray()),
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal(StagedContentWriteOutcome.InvalidContent, result.Outcome);
        Assert.Null(result.Handle);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, result.FailureDetail!);
        Assert.All(
            harness.Coordinator.Content.Records,
            record => Assert.Equal(StagedContentState.Released, record.State));
    }

    [Fact]
    public async Task An_archive_with_an_unsafe_entry_path_is_rejected()
    {
        await using var harness = Harness.Create();

        var result = await harness.Coordinator.StagePackageAsync(
            Owner,
            new MemoryStream(UnsafeArchive()),
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal(StagedContentWriteOutcome.InvalidContent, result.Outcome);
    }

    [Fact]
    public async Task Staging_extracts_the_identity_with_kernel_parsing()
    {
        await using var harness = Harness.Create();

        var result = await harness.StageAsync("Contoso.Sample", "1.2.3");

        Assert.Equal(StagedContentWriteOutcome.Succeeded, result.Outcome);
        Assert.Equal(new StagedPackageIdentity("Contoso.Sample", "1.2.3"), result.Identity);
        Assert.NotNull(result.Handle);
        Assert.Equal(64, result.Handle!.ContentSha256.Length);
    }

    [Fact]
    public async Task Symbols_whose_identity_does_not_match_are_rejected()
    {
        await using var harness = Harness.Create();

        var result = await harness.Coordinator.StageSymbolsAsync(
            Owner,
            new MemoryStream(Nupkg("Contoso.Other", "1.2.3")),
            new StagedPackageIdentity("Contoso.Sample", "1.2.3"),
            1024 * 1024,
            CancellationToken.None);

        Assert.Equal(StagedContentWriteOutcome.IdentityMismatch, result.Outcome);
    }

    [Fact]
    public async Task Promotion_publishes_once_and_commits_the_declared_state()
    {
        await using var harness = Harness.Create();
        var staged = await harness.StageAsync("Contoso.Sample", "1.2.3");
        await harness.WriteStateAsync("group.one", "{\"v\":1}", null);

        var result = await harness.PromoteAsync(staged, "promote-1", "{\"v\":2}");

        Assert.Equal(PublicationRequestOutcome.Published, result.Outcome);
        Assert.Equal("Contoso.Sample", result.PackageId);
        Assert.Equal("1.2.3", result.PackageVersion);
        Assert.False(result.Replayed);
        Assert.Null(harness.Coordinator.Content.Find(Owner, staged.Handle!.HandleId));
        Assert.Equal("{\"v\":2}", await harness.ReadStateAsync("group.one"));
        Assert.NotNull(await harness.SupplyChain.GetStatusAsync("Contoso.Sample", "1.2.3"));
    }

    [Fact]
    public async Task A_retry_after_promotion_replays_the_recorded_result_once()
    {
        await using var harness = Harness.Create();
        var staged = await harness.StageAsync("Contoso.Sample", "1.2.3");
        await harness.WriteStateAsync("group.one", "{\"v\":1}", null);

        var first = await harness.PromoteAsync(staged, "promote-1", "{\"v\":2}");
        var second = await harness.PromoteAsync(staged, "promote-1", "{\"v\":2}");

        Assert.Equal(PublicationRequestOutcome.Published, first.Outcome);
        Assert.False(first.Replayed);
        Assert.Equal(PublicationRequestOutcome.Published, second.Outcome);
        Assert.True(second.Replayed);
        Assert.Equal(first.StateConcurrencyToken, second.StateConcurrencyToken);
        Assert.Single(harness.Coordinator.Journal.Entries);
        Assert.Equal("{\"v\":2}", await harness.ReadStateAsync("group.one"));
    }

    [Fact]
    public async Task A_new_key_over_already_promoted_content_fails_closed()
    {
        await using var harness = Harness.Create();
        var staged = await harness.StageAsync("Contoso.Sample", "1.2.3");
        await harness.WriteStateAsync("group.one", "{\"v\":1}", null);
        await harness.PromoteAsync(staged, "promote-1", "{\"v\":2}");

        var again = await harness.PromoteAsync(staged, "promote-2", "{\"v\":3}");

        Assert.Equal(PublicationRequestOutcome.HandleNotFound, again.Outcome);
        Assert.Single(harness.Coordinator.Journal.Entries);
    }

    [Fact]
    public async Task A_losing_compare_and_swap_never_publishes()
    {
        await using var harness = Harness.Create();
        var staged = await harness.StageAsync("Contoso.Sample", "1.2.3");
        await harness.WriteStateAsync("group.one", "{\"v\":1}", null);

        var result = await harness.Coordinator.PublishAsync(
            Owner,
            new StagedPublicationCommand(
                staged.Handle!.HandleId,
                null,
                "promote-1",
                "group.one",
                harness.StateToken("group.one") + 1,
                Encoding.UTF8.GetBytes("{\"v\":2}")),
            CancellationToken.None);

        Assert.Equal(PublicationRequestOutcome.StateConcurrencyConflict, result.Outcome);
        Assert.Null(await harness.SupplyChain.GetStatusAsync("Contoso.Sample", "1.2.3"));
        Assert.Empty(harness.Coordinator.Journal.Entries);
        Assert.Equal(
            StagedContentState.Staged,
            harness.Coordinator.Content.Find(Owner, staged.Handle.HandleId)!.State);
    }

    [Fact]
    public async Task An_expired_lease_fails_closed_and_releases_the_content()
    {
        await using var harness = Harness.Create(TimeSpan.FromMinutes(5));
        var staged = await harness.StageAsync("Contoso.Sample", "1.2.3");
        await harness.WriteStateAsync("group.one", "{\"v\":1}", null);
        harness.Clock.Advance(TimeSpan.FromMinutes(10));

        var result = await harness.PromoteAsync(staged, "promote-1", "{\"v\":2}");

        Assert.Equal(PublicationRequestOutcome.HandleExpired, result.Outcome);
        Assert.Null(harness.Coordinator.Content.Find(Owner, staged.Handle!.HandleId));
        Assert.Null(await harness.SupplyChain.GetStatusAsync("Contoso.Sample", "1.2.3"));
    }

    [Fact]
    public async Task Another_extension_cannot_promote_staged_content_it_does_not_own()
    {
        await using var harness = Harness.Create();
        var staged = await harness.StageAsync("Contoso.Sample", "1.2.3");

        var result = await harness.Coordinator.PublishAsync(
            "Other.Extension",
            new StagedPublicationCommand(
                staged.Handle!.HandleId,
                null,
                "promote-1",
                "group.one",
                null,
                "{}"u8.ToArray()),
            CancellationToken.None);

        Assert.Equal(PublicationRequestOutcome.HandleNotFound, result.Outcome);
        Assert.Null(await harness.SupplyChain.GetStatusAsync("Contoso.Sample", "1.2.3"));
    }

    [Fact]
    public async Task Recovery_finishes_an_interrupted_publication_without_republishing()
    {
        var root = StagedContentStoreTests.CreateStorage();
        string contentId;
        await using (var harness = Harness.Create(storageRoot: root))
        {
            var package = Nupkg("Contoso.Sample", "1.2.3");
            var staged = await harness.StageAsync(package);
            contentId = staged.Handle!.HandleId;
            await harness.WriteStateAsync("group.one", "{\"v\":1}", null);

            // A crash between the package transaction and the dependent state: the
            // journal records the intent and the package is published, nothing else ran.
            await harness.JournalPendingAsync(contentId, "group.one", "{\"v\":2}");
            await harness.PublishDirectlyAsync(package);
        }

        await using var restarted = Harness.Create(storageRoot: root);
        Assert.Single(restarted.Coordinator.Journal.ReadUnfinished());
        await restarted.Coordinator.RecoverAsync(CancellationToken.None);

        Assert.Empty(restarted.Coordinator.Journal.ReadUnfinished());
        Assert.Equal(
            PublicationRequestOutcome.Published.ToString(),
            restarted.Coordinator.Journal.Find(Owner, "promote-1")!.Outcome);
        Assert.Null(restarted.Coordinator.Content.Find(Owner, contentId));
        Assert.Equal("{\"v\":2}", await restarted.ReadStateAsync("group.one"));
    }

    [Fact]
    public async Task Recovery_fails_closed_when_the_interrupted_package_never_published()
    {
        var root = StagedContentStoreTests.CreateStorage();
        string contentId;
        await using (var harness = Harness.Create(storageRoot: root))
        {
            var staged = await harness.StageAsync("Contoso.Sample", "1.2.3");
            contentId = staged.Handle!.HandleId;
            await harness.WriteStateAsync("group.one", "{\"v\":1}", null);
            await harness.JournalPendingAsync(contentId, "group.one", "{\"v\":2}");
        }

        await using var restarted = Harness.Create(storageRoot: root);
        await restarted.Coordinator.RecoverAsync(CancellationToken.None);

        Assert.Empty(restarted.Coordinator.Journal.ReadUnfinished());
        Assert.Equal(
            StagedContentState.Staged,
            restarted.Coordinator.Content.Find(Owner, contentId)!.State);
        Assert.Equal("{\"v\":1}", await restarted.ReadStateAsync("group.one"));
    }

    internal static byte[] Nupkg(string id, string version)
    {
        using var package = TestPackageBuilder.Create(id, version).Build();
        return package.Content;
    }

    private static byte[] UnsafeArchive()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("../escape.nuspec");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("<package />");
        }

        return buffer.ToArray();
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly IPackageStore _store;

        private Harness(
            IPackageStore store,
            PackageSupplyChainService supplyChain,
            TransactionalStateStore state,
            StagedContentStore staged,
            PublicationJournal journal,
            ServerDiagnostics diagnostics,
            StagedPublicationCoordinator coordinator,
            FakeClock clock)
        {
            _store = store;
            SupplyChain = supplyChain;
            State = state;
            Staged = staged;
            Journal = journal;
            Diagnostics = diagnostics;
            Coordinator = coordinator;
            Clock = clock;
        }

        public PackageSupplyChainService SupplyChain { get; }

        public TransactionalStateStore State { get; }

        public StagedContentStore Staged { get; }

        public PublicationJournal Journal { get; }

        public ServerDiagnostics Diagnostics { get; }

        public StagedPublicationCoordinator Coordinator { get; }

        public FakeClock Clock { get; }

        public static Harness Create(TimeSpan? lease = null, string? storageRoot = null)
        {
            var clock = new FakeClock(DateTimeOffset.UnixEpoch);
            IPackageStore store = storageRoot is null
                ? new InMemoryPackageStore()
                : new DurablePackageStore(
                    Path.Combine(storageRoot, "packages-root"),
                    PackageTransferLimits.Default);
            var supplyChain = new PackageSupplyChainService(
                store,
                storageRoot is null ? null : Path.Combine(storageRoot, "packages-root"),
                timeProvider: clock);
            var state = new TransactionalStateStore(
                storageRoot is null ? null : Path.Combine(storageRoot, "extension-state"),
                [
                    new StateParticipantDescriptor(Owner, "1.0.0", "package-staging", 1, true)
                ]);
            var staged = new StagedContentStore(
                storageRoot,
                "host",
                new StagedContentQuotas(DefaultLease: lease),
                clock);
            var journal = new PublicationJournal(storageRoot, clock);
            var diagnostics = new ServerDiagnostics(store);
            return new Harness(
                store,
                supplyChain,
                state,
                staged,
                journal,
                diagnostics,
                new StagedPublicationCoordinator(
                    "host",
                    staged,
                    journal,
                    state,
                    store,
                    () => supplyChain,
                    PackageTransferLimits.Default,
                    diagnostics,
                    clock),
                clock);
        }

        public ValueTask<StagedContentWriteResult> StageAsync(string id, string version) =>
            StageAsync(Nupkg(id, version));

        public ValueTask<StagedContentWriteResult> StageAsync(byte[] package) =>
            Coordinator.StagePackageAsync(
                Owner,
                new MemoryStream(package),
                16 * 1024 * 1024,
                CancellationToken.None);

        public ValueTask<AtomicPublicationResult> PromoteAsync(
            StagedContentWriteResult staged,
            string idempotencyKey,
            string statePayload) =>
            Coordinator.PublishAsync(
                Owner,
                new StagedPublicationCommand(
                    staged.Handle!.HandleId,
                    null,
                    idempotencyKey,
                    "group.one",
                    StateToken("group.one"),
                    Encoding.UTF8.GetBytes(statePayload)),
                CancellationToken.None);

        public async ValueTask JournalPendingAsync(
            string contentId,
            string stateKey,
            string statePayload) =>
            await Journal.BeginAsync(
                new PublicationJournalEntry(
                    Guid.NewGuid().ToString("N"),
                    Owner,
                    "promote-1",
                    contentId,
                    null,
                    stateKey,
                    StateToken(stateKey),
                    "Contoso.Sample",
                    "1.2.3",
                    Coordinator.Content.Find(Owner, contentId)!.ContentSha256,
                    PublicationJournalPhase.Pending,
                    PublicationRequestOutcome.Failed.ToString(),
                    null,
                    null,
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(statePayload)),
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None);

        public async ValueTask PublishDirectlyAsync(byte[] content)
        {
            var package = TestPackage.FromContent(content);
            var result = await SupplyChain.PublishAsync(
                new PackagePublicationRequest(package, Owner, "staging", Administrator: true),
                CancellationToken.None);
            Assert.Equal(PackagePublicationOutcome.Published, result.Outcome);
        }

        public async ValueTask WriteStateAsync(string key, string json, long? expected) =>
            await State.WriteAsync(
                Owner,
                key,
                Encoding.UTF8.GetBytes(json),
                expected,
                CancellationToken.None);

        public long? StateToken(string key) =>
            State.ReadAsync(Owner, key, CancellationToken.None).AsTask().GetAwaiter().GetResult()?.ETag;

        public async ValueTask<string?> ReadStateAsync(string key)
        {
            var record = await State.ReadAsync(Owner, key, CancellationToken.None);
            return record is null ? null : Encoding.UTF8.GetString(record.Value);
        }

        public async ValueTask DisposeAsync()
        {
            Journal.Dispose();
            Staged.Dispose();
            State.Dispose();
            Diagnostics.Dispose();
            await SupplyChain.DisposeAsync();
            await _store.DisposeAsync();
        }
    }
}

/// <summary>A deterministic clock for lease, expiry, and journal assertions.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}

/// <summary>Packs the optional staging extension once per unit-test collection.</summary>
public sealed class PackageStagingAssetsFixture : IAsyncLifetime
{
    public ContosoFlavorsAssets StagingAssets { get; private set; } = null!;

    public async Task InitializeAsync() =>
        StagingAssets = await PackageStagingAssets.BuildAsync("staging-unit");

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition(nameof(PackageStagingAssetsCollection))]
public sealed class PackageStagingAssetsCollection :
    ICollectionFixture<PackageStagingAssetsFixture>;
