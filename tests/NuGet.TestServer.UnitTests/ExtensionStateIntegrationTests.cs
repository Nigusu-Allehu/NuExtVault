using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Vulnerabilities;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Vulnerabilities;
using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.UnitTests;

/// <summary>
/// Step 12A integration coverage. Extension state is exercised through the composed
/// server, the granted capability handle, and the durable layout the kernel owns rather
/// than through the store class in isolation.
/// </summary>
public sealed class ExtensionStateIntegrationTests
{
    private const string Owner = BuiltInExtensionIds.Vulnerabilities;

    [Fact]
    public void Composition_resolves_one_active_transactional_state_owner()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Standard);

        var store = host.Services.GetService<TransactionalStateStore>();

        Assert.NotNull(store);
        Assert.Null(host.Services.GetService<ExtensionStateStore>());
        Assert.Contains(
            store!.Participants,
            participant => participant.ExtensionId == BuiltInExtensionIds.Vulnerabilities);
    }

    [Fact]
    public async Task Capability_writes_reach_the_kernel_owned_durable_layout()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Standard);
        var state = StateCapability(host);
        var store = host.Services.GetRequiredService<TransactionalStateStore>();

        var token = await state.WriteEntryAsync(
            "snapshot-v1", new StateValue("composed"), null, CancellationToken.None);
        var record = await store.ReadAsync(Owner, "snapshot-v1", CancellationToken.None);

        Assert.Equal(token, record!.ETag);
        Assert.Equal(KernelStateParticipants.VulnerabilitySchemaName, record.SchemaName);
        Assert.Equal("composed", JsonSerializer.Deserialize<StateValue>(record.Value)!.Value);
    }

    [Fact]
    public async Task Capability_rejects_a_stale_concurrency_token()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Standard);
        var state = StateCapability(host);

        var first = await state.WriteEntryAsync(
            "snapshot-v1", new StateValue("first"), null, CancellationToken.None);
        await state.WriteEntryAsync(
            "snapshot-v1", new StateValue("second"), first, CancellationToken.None);

        await Assert.ThrowsAsync<StateConcurrencyException>(
            () => state.WriteEntryAsync(
                "snapshot-v1", new StateValue("third"), first, CancellationToken.None).AsTask());
        var entry = await state.ReadEntryAsync<StateValue>("snapshot-v1", CancellationToken.None);
        Assert.Equal("second", entry!.Value.Value);
    }

    [Fact]
    public async Task Concurrency_tokens_and_checkpoint_identity_survive_a_durable_restart()
    {
        using var directory = new TemporaryDirectory();
        long staleToken;
        long lastToken;
        long firstCheckpoint;
        using (var store = new TransactionalStateStore(directory.Path, Participants()))
        {
            staleToken = (await store.WriteAsync(
                Owner, "key", Utf8("first"), null, CancellationToken.None)).ETag;
            lastToken = (await store.WriteAsync(
                Owner, "key", Utf8("second"), staleToken, CancellationToken.None)).ETag;
            await store.DeleteAsync(Owner, "key", lastToken, CancellationToken.None);
            using var checkpoint = await store.CreateCheckpointAsync(CancellationToken.None);
            firstCheckpoint = checkpoint.CheckpointId;
        }

        using (var store = new TransactionalStateStore(directory.Path, Participants()))
        {
            var recreated = await store.WriteAsync(
                Owner, "key", Utf8("third"), null, CancellationToken.None);
            using var checkpoint = await store.CreateCheckpointAsync(CancellationToken.None);

            Assert.True(
                recreated.ETag > lastToken,
                $"Token {recreated.ETag} must exceed the pre-restart high-water mark {lastToken}.");
            Assert.True(
                checkpoint.CheckpointId > firstCheckpoint,
                "Checkpoint identity must stay monotonic across a restart.");
            await Assert.ThrowsAsync<StateConcurrencyException>(
                () => store.WriteAsync(
                    Owner, "key", Utf8("stale"), staleToken, CancellationToken.None).AsTask());
        }
    }

    [Fact]
    public async Task Durable_state_survives_a_restart_and_keeps_its_value()
    {
        using var directory = new TemporaryDirectory();
        using (var store = new TransactionalStateStore(directory.Path, Participants()))
        {
            await store.WriteAsync(Owner, "key", Utf8("persisted"), null, CancellationToken.None);
        }

        using var reopened = new TransactionalStateStore(directory.Path, Participants());
        var record = await reopened.ReadAsync(Owner, "key", CancellationToken.None);

        Assert.Equal("persisted", Text(record!.Value));
    }

    [Fact]
    public async Task Version_one_state_is_imported_and_mirrored_for_a_downgrade()
    {
        using var directory = new TemporaryDirectory();
        var legacy = new ExtensionStateStore(directory.Path);
        await legacy.WriteAsync(
            Owner, "snapshot-v1", new StateValue("legacy"), CancellationToken.None);

        using var store = new TransactionalStateStore(directory.Path, Participants());
        var imported = await store.ReadAsync(Owner, "snapshot-v1", CancellationToken.None);
        await store.WriteAsync(
            Owner,
            "snapshot-v1",
            JsonSerializer.SerializeToUtf8Bytes(new StateValue("upgraded")),
            imported!.ETag,
            CancellationToken.None);

        Assert.Equal("legacy", JsonSerializer.Deserialize<StateValue>(imported.Value)!.Value);
        var downgraded = await new ExtensionStateStore(directory.Path)
            .ReadAsync<StateValue>(Owner, "snapshot-v1", CancellationToken.None);
        Assert.Equal("upgraded", downgraded!.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Checkpoint_freezes_participant_state_at_creation(bool durable)
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionalStateStore(
            durable ? directory.Path : null, Participants());
        await store.WriteAsync(Owner, "key", Utf8("frozen"), null, CancellationToken.None);

        using var checkpoint = await store.CreateCheckpointAsync(CancellationToken.None);
        await store.WriteAsync(Owner, "key", Utf8("mutated"), null, CancellationToken.None);
        var exported = await store.ExportCheckpointAsync(checkpoint, CancellationToken.None);

        var record = Assert.Single(Assert.Single(exported.Participants).Records);
        Assert.Equal("frozen", Text(record.Value));
    }

    [Fact]
    public async Task Released_checkpoints_cannot_be_exported()
    {
        using var store = new TransactionalStateStore(root: null, Participants());
        var checkpoint = await store.CreateCheckpointAsync(CancellationToken.None);
        checkpoint.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ExportCheckpointAsync(checkpoint, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Expired_checkpoint_leases_are_released_and_rejected()
    {
        using var directory = new TemporaryDirectory();
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        using var store = new TransactionalStateStore(
            directory.Path, Participants(), timeProvider: clock);
        await store.WriteAsync(Owner, "key", Utf8("value"), null, CancellationToken.None);
        using var checkpoint = await store.CreateCheckpointAsync(
            CancellationToken.None, TimeSpan.FromMinutes(1));

        clock.Advance(TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ExportCheckpointAsync(checkpoint, CancellationToken.None).AsTask());
        using var next = await store.CreateCheckpointAsync(CancellationToken.None);
        Assert.False(Directory.Exists(checkpoint.FrozenDirectory!));
    }

    [Fact]
    public async Task Restore_fails_when_a_required_participant_is_unknown()
    {
        using var store = new TransactionalStateStore(root: null, Participants());
        var data = Checkpoint(
            new StateCheckpointParticipant(
                "unknown.owner",
                "1.0.0",
                "unknown-schema",
                1,
                Required: true,
                [new StateCheckpointRecord("key", Utf8("value"), 1)]));

        await Assert.ThrowsAsync<StateSchemaCompatibilityException>(
            () => store.StageRestoreAsync(data, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Restore_fails_when_required_participant_state_is_missing()
    {
        using var store = new TransactionalStateStore(
            root: null,
            [new StateParticipantDescriptor(Owner, "1.0.0", "schema", 1, Required: true)]);

        await Assert.ThrowsAsync<StateSchemaCompatibilityException>(
            () => store.StageRestoreAsync(Checkpoint(), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Restore_fails_for_a_newer_schema_version()
    {
        using var store = new TransactionalStateStore(
            root: null,
            [new StateParticipantDescriptor(Owner, "1.0.0", "schema", 1)]);
        var data = Checkpoint(
            new StateCheckpointParticipant(Owner, "1.0.0", "schema", 9, Required: false, []));

        await Assert.ThrowsAsync<StateSchemaCompatibilityException>(
            () => store.StageRestoreAsync(data, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Restore_fails_when_the_migration_path_is_incomplete()
    {
        using var store = new TransactionalStateStore(
            root: null,
            [
                new StateParticipantDescriptor(
                    Owner,
                    "1.0.0",
                    "schema",
                    SchemaVersion: 3,
                    Required: false,
                    [new StateSchemaMigration(2, 3, value => value)])
            ]);
        var data = Checkpoint(
            new StateCheckpointParticipant(
                Owner, "1.0.0", "schema", 1, Required: false,
                [new StateCheckpointRecord("key", Utf8("value"), 1)]));

        var exception = await Assert.ThrowsAsync<StateSchemaCompatibilityException>(
            () => store.StageRestoreAsync(data, CancellationToken.None).AsTask());

        Assert.Contains("migration path", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_failing_migration_leaves_authoritative_state_unchanged(bool durable)
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionalStateStore(
            durable ? directory.Path : null,
            [
                new StateParticipantDescriptor(
                    Owner,
                    "1.0.0",
                    "schema",
                    SchemaVersion: 2,
                    Required: false,
                    [
                        new StateSchemaMigration(
                            1,
                            2,
                            _ => throw new InvalidOperationException("migration failed"))
                    ])
            ]);
        await store.WriteAsync(Owner, "key", Utf8("original"), null, CancellationToken.None);
        var data = Checkpoint(
            new StateCheckpointParticipant(
                Owner, "1.0.0", "schema", 1, Required: false,
                [new StateCheckpointRecord("key", Utf8("replacement"), 1)]));

        await Assert.ThrowsAsync<StateSchemaCompatibilityException>(
            () => store.StageRestoreAsync(data, CancellationToken.None).AsTask());

        var record = await store.ReadAsync(Owner, "key", CancellationToken.None);
        Assert.Equal("original", Text(record!.Value));
        if (durable)
        {
            Assert.Empty(Directory.EnumerateDirectories(directory.Path, ".staging-*"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restore_applies_ordered_migrations_and_commits_once(bool durable)
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionalStateStore(
            durable ? directory.Path : null,
            [
                new StateParticipantDescriptor(
                    Owner,
                    "1.0.0",
                    "schema",
                    SchemaVersion: 3,
                    Required: false,
                    [
                        new StateSchemaMigration(1, 2, value => Utf8($"{Text(value)}+2")),
                        new StateSchemaMigration(2, 3, value => Utf8($"{Text(value)}+3"))
                    ])
            ]);
        await store.WriteAsync(Owner, "stale", Utf8("removed"), null, CancellationToken.None);
        var data = Checkpoint(
            new StateCheckpointParticipant(
                Owner, "1.0.0", "schema", 1, Required: false,
                [new StateCheckpointRecord("key", Utf8("v1"), 7)]));

        using var staged = await store.StageRestoreAsync(data, CancellationToken.None);
        Assert.Equal(
            "removed",
            Text((await store.ReadAsync(Owner, "stale", CancellationToken.None))!.Value));

        await store.CommitRestoreAsync(staged, CancellationToken.None);

        Assert.Equal(
            "v1+2+3",
            Text((await store.ReadAsync(Owner, "key", CancellationToken.None))!.Value));
        Assert.Null(await store.ReadAsync(Owner, "stale", CancellationToken.None));
    }

    [Fact]
    public async Task Aborted_restore_leaves_authoritative_state_and_storage_unchanged()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionalStateStore(directory.Path, Participants());
        await store.WriteAsync(Owner, "key", Utf8("original"), null, CancellationToken.None);
        var data = Checkpoint(VulnerabilityParticipant(
            new StateCheckpointRecord("key", Utf8("replacement"), 3)));

        var staged = await store.StageRestoreAsync(data, CancellationToken.None);
        await store.AbortRestoreAsync(staged, CancellationToken.None);

        Assert.Equal(
            "original",
            Text((await store.ReadAsync(Owner, "key", CancellationToken.None))!.Value));
        Assert.Empty(Directory.EnumerateDirectories(directory.Path, ".staging-*"));
    }

    [Fact]
    public async Task Extra_inactive_state_is_quarantined_and_reported()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionalStateStore(directory.Path, Participants());
        var data = Checkpoint(
            new StateCheckpointParticipant(
                "retired.extension", "1.0.0", "retired", 1, Required: false,
                [new StateCheckpointRecord("key", Utf8("retired"), 4)]));

        using var staged = await store.StageRestoreAsync(data, CancellationToken.None);
        var report = await store.CommitRestoreAsync(staged, CancellationToken.None);

        Assert.Equal("retired.extension", Assert.Single(report.QuarantinedExtensions));
        Assert.Contains(report.Warnings, warning => warning.Contains("quarantined"));
        Assert.True(Directory.Exists(Path.Combine(directory.Path, "quarantine")));
        await Assert.ThrowsAsync<ExtensionStateException>(
            () => store.ReadAsync("retired.extension", "key", CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_restore_of_only_inactive_state_clears_active_state(bool durable)
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionalStateStore(
            durable ? directory.Path : null, Participants());
        await store.WriteAsync(Owner, "key", Utf8("original"), null, CancellationToken.None);
        var data = Checkpoint(
            new StateCheckpointParticipant(
                "retired.extension", "1.0.0", "retired", 1, Required: false,
                [new StateCheckpointRecord("kept", Utf8("retired"), 4)]));

        using var staged = await store.StageRestoreAsync(data, CancellationToken.None);
        var report = await store.CommitRestoreAsync(staged, CancellationToken.None);

        Assert.Equal("retired.extension", Assert.Single(report.QuarantinedExtensions));
        Assert.Null(await store.ReadAsync(Owner, "key", CancellationToken.None));
        if (!durable)
        {
            return;
        }

        Assert.True(Directory.Exists(Path.Combine(
            directory.Path, TransactionalStateStore.QuarantineDirectoryName)));
        using var reopened = new TransactionalStateStore(directory.Path, Participants());
        Assert.Null(await reopened.ReadAsync(Owner, "key", CancellationToken.None));
    }

    [Fact]
    public async Task An_interrupted_durable_commit_is_completed_on_the_next_open()
    {
        using var directory = new TemporaryDirectory();
        var data = Checkpoint(VulnerabilityParticipant(
            new StateCheckpointRecord("first", Utf8("restored-first"), 11),
            new StateCheckpointRecord("second", Utf8("restored-second"), 12)));
        using (var store = new TransactionalStateStore(directory.Path, Participants()))
        {
            await store.WriteAsync(Owner, "first", Utf8("original"), null, CancellationToken.None);
            var staged = await store.StageRestoreAsync(data, CancellationToken.None);

            // A crash between the commit journal and the directory swap must replay forward.
            await File.WriteAllTextAsync(
                Path.Combine(directory.Path, TransactionalStateStore.RestoreJournalFileName),
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    StagingDirectory = Path.GetFileName(staged.StagingDirectory!),
                    TrashDirectory = ".trash-1a",
                    RestoreId = staged.RestoreId
                }));
        }

        using var reopened = new TransactionalStateStore(directory.Path, Participants());

        Assert.Equal(
            "restored-first",
            Text((await reopened.ReadAsync(Owner, "first", CancellationToken.None))!.Value));
        Assert.Equal(
            "restored-second",
            Text((await reopened.ReadAsync(Owner, "second", CancellationToken.None))!.Value));
        Assert.False(File.Exists(
            Path.Combine(directory.Path, TransactionalStateStore.RestoreJournalFileName)));
        Assert.Empty(Directory.EnumerateDirectories(directory.Path, ".staging-*"));
    }

    [Fact]
    public async Task An_interrupted_durable_stage_is_discarded_on_the_next_open()
    {
        using var directory = new TemporaryDirectory();
        var data = Checkpoint(VulnerabilityParticipant(
            new StateCheckpointRecord("first", Utf8("restored"), 11)));
        using (var store = new TransactionalStateStore(directory.Path, Participants()))
        {
            await store.WriteAsync(Owner, "first", Utf8("original"), null, CancellationToken.None);
            _ = await store.StageRestoreAsync(data, CancellationToken.None);
        }

        using var reopened = new TransactionalStateStore(directory.Path, Participants());

        Assert.Equal(
            "original",
            Text((await reopened.ReadAsync(Owner, "first", CancellationToken.None))!.Value));
        Assert.Empty(Directory.EnumerateDirectories(directory.Path, ".staging-*"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_writers_contend_for_one_key(bool durable)
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionalStateStore(
            durable ? directory.Path : null, Participants());
        var seed = await store.WriteAsync(Owner, "key", Utf8("seed"), null, CancellationToken.None);
        using var start = new ManualResetEventSlim(false);

        var writers = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
        {
            start.Wait();
            try
            {
                await store.WriteAsync(
                    Owner, "key", Utf8($"writer-{index}"), seed.ETag, CancellationToken.None);
                return true;
            }
            catch (StateConcurrencyException)
            {
                return false;
            }
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(writers);

        Assert.Equal(1, results.Count(succeeded => succeeded));
        var record = await store.ReadAsync(Owner, "key", CancellationToken.None);
        Assert.StartsWith("writer-", Text(record!.Value), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Concurrent_writers_to_distinct_keys_all_commit(bool durable)
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionalStateStore(
            durable ? directory.Path : null, Participants());
        using var start = new ManualResetEventSlim(false);

        var writers = Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
        {
            start.Wait();
            return await store.WriteAsync(
                Owner, $"key-{index}", Utf8($"value-{index}"), null, CancellationToken.None);
        })).ToArray();
        start.Set();
        var records = await Task.WhenAll(writers);

        Assert.Equal(32, records.Select(record => record.ETag).Distinct().Count());
        for (var index = 0; index < 32; index++)
        {
            var record = await store.ReadAsync(Owner, $"key-{index}", CancellationToken.None);
            Assert.Equal($"value-{index}", Text(record!.Value));
        }
    }

    [Fact]
    public async Task Atomic_multi_key_edits_apply_or_fail_together()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionalStateStore(directory.Path, Participants());
        var first = await store.WriteAsync(Owner, "a", Utf8("a1"), null, CancellationToken.None);
        await store.WriteAsync(Owner, "b", Utf8("b1"), null, CancellationToken.None);

        await Assert.ThrowsAsync<StateConcurrencyException>(
            () => store.CompareAndSwapAsync(
                Owner,
                [
                    new StateEdit("a", first.ETag, Utf8("a2")),
                    new StateEdit("b", first.ETag, Utf8("b2"))
                ],
                CancellationToken.None).AsTask());

        Assert.Equal("a1", Text((await store.ReadAsync(Owner, "a", CancellationToken.None))!.Value));
        Assert.Equal("b1", Text((await store.ReadAsync(Owner, "b", CancellationToken.None))!.Value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quotas_bound_keys_records_owner_bytes_and_owner_count(bool durable)
    {
        using var directory = new TemporaryDirectory();
        var quotas = new StateStoreQuotas(
            MaximumKeyLength: 16,
            MaximumRecordBytes: 64,
            MaximumRecordsPerOwner: 3,
            MaximumOwnerBytes: 128,
            MaximumOwners: 1);
        using var store = new TransactionalStateStore(
            durable ? directory.Path : null,
            [
                new StateParticipantDescriptor(Owner, "1.0.0", "schema", 1),
                new StateParticipantDescriptor("second.owner", "1.0.0", "schema", 1)
            ],
            quotas);

        await Assert.ThrowsAsync<StateQuotaExceededException>(
            () => store.WriteAsync(
                Owner, new string('k', 17), Utf8("v"), null, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<StateQuotaExceededException>(
            () => store.WriteAsync(
                Owner, "big", new byte[65], null, CancellationToken.None).AsTask());

        await store.WriteAsync(Owner, "a", new byte[48], null, CancellationToken.None);
        await store.WriteAsync(Owner, "b", new byte[48], null, CancellationToken.None);
        await Assert.ThrowsAsync<StateQuotaExceededException>(
            () => store.WriteAsync(
                Owner, "c", new byte[48], null, CancellationToken.None).AsTask());

        await store.WriteAsync(Owner, "c", new byte[16], null, CancellationToken.None);
        await Assert.ThrowsAsync<StateQuotaExceededException>(
            () => store.WriteAsync(
                Owner, "d", new byte[1], null, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<StateQuotaExceededException>(
            () => store.WriteAsync(
                "second.owner", "a", new byte[1], null, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Capability_enforces_the_maximum_stream_bytes_limit()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionalStateStore(directory.Path, Participants());
        var capability = new ExtensionStateCapability(
            "host",
            Owner,
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                BuiltInCapabilityNames.ExtensionStateRead,
                BuiltInCapabilityNames.ExtensionStateWrite),
            new CapabilityAuditLog(),
            new CapabilityLimits(MaximumStreamBytes: 32),
            store);

        await Assert.ThrowsAsync<CapabilityStreamLimitExceededException>(
            () => capability.WriteEntryAsync(
                "key",
                new StateValue(new string('v', 64)),
                null,
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Parallel_hosts_keep_isolated_state()
    {
        var hosts = Enumerable.Range(0, 4)
            .Select(_ => TestServerApplication.Build(ServerProfiles.Standard))
            .ToArray();
        try
        {
            for (var index = 0; index < hosts.Length; index++)
            {
                await StateCapability(hosts[index]).WriteAsync(
                    "snapshot-v1", new StateValue($"host-{index}"), CancellationToken.None);
            }

            for (var index = 0; index < hosts.Length; index++)
            {
                var value = await StateCapability(hosts[index])
                    .ReadAsync<StateValue>("snapshot-v1", CancellationToken.None);
                Assert.Equal($"host-{index}", value!.Value);
            }
        }
        finally
        {
            foreach (var host in hosts)
            {
                host.Dispose();
            }
        }
    }

    [Fact]
    public async Task Durable_records_store_the_payload_without_re_encoding_it()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionalStateStore(directory.Path, Participants());
        var payload = Utf8("""{"snapshot":"raw-payload-bytes"}""");

        await store.WriteAsync(Owner, "snapshot-v1", payload, null, CancellationToken.None);

        var file = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(directory.Path, TransactionalStateStore.ActiveDirectoryName),
            $"*{TransactionalStateStore.RecordExtension}",
            SearchOption.AllDirectories));
        var content = await File.ReadAllBytesAsync(file);
        Assert.EndsWith(Text(payload), Text(content), StringComparison.Ordinal);
        Assert.True(
            content.Length < payload.Length + 512,
            "A record must be a bounded header plus the payload, not a re-encoded copy.");
    }

    [Fact]
    public async Task Composed_hosts_still_read_the_legacy_vulnerability_cache()
    {
        using var storage = new TemporaryDirectory();
        var cache = new VulnerabilitySnapshotCache(
            Path.Combine(storage.Path, "vulnerabilities"));
        await cache.SaveAsync(EmbeddedVulnerabilitySnapshot.Load());
        var composition = ServerComposition.Create(
            ServerProfiles.Standard,
            storageDirectory: storage.Path,
            authentication: AuthenticationConfiguration.Anonymous);
        using var application = ServerApplication.Build(composition);
        var state = application.Services.GetRequiredService<CapabilityBroker>()
            .ForOwner(BuiltInExtensionIds.Vulnerabilities)
            .GetRequired<IExtensionStateCapability>(BuiltInCapabilityNames.ExtensionStateRead);

        var files = await state.ReadLegacyFileSetAsync(
            VulnerabilityExtension.LegacyStateName, CancellationToken.None);

        Assert.NotNull(files);
        Assert.NotEmpty(files!.Files);
    }

    private static IExtensionStateCapability StateCapability(TestServerApplication host) =>
        host.Services.GetRequiredService<CapabilityBroker>()
            .ForOwner(BuiltInExtensionIds.Vulnerabilities)
            .GetRequired<IExtensionStateCapability>(BuiltInCapabilityNames.ExtensionStateRead);

    private static ImmutableArray<StateParticipantDescriptor> Participants() =>
        KernelStateParticipants.BuiltIn;

    private static StateCheckpointParticipant VulnerabilityParticipant(
        params StateCheckpointRecord[] records) =>
        new(
            Owner,
            "1.0.0",
            KernelStateParticipants.VulnerabilitySchemaName,
            1,
            Required: false,
            [.. records]);

    private static StateCheckpointData Checkpoint(
        params StateCheckpointParticipant[] participants) =>
        new(
            StateCheckpointData.CurrentManifestVersion,
            1,
            DateTimeOffset.UnixEpoch,
            [.. participants]);

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(byte[] value) => Encoding.UTF8.GetString(value);

    private sealed record StateValue(string Value);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
