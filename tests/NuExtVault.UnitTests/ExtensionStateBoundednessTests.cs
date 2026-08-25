using System.Collections.Immutable;
using System.Text;
using NuExtVault.Hosting;
using NuExtVault.Kernel.Capabilities;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.UnitTests;

/// <summary>
/// Step 12A boundedness coverage. Opening a durable store reads participant
/// descriptors and record headers only, a payload is materialized when it is read or
/// when a migration actually has to rewrite it, participant integrity is computed by
/// streaming rather than by holding every record, and neither the version 1 mirror of
/// an unregistered extension nor pre-transactional state is lost.
/// </summary>
public sealed class ExtensionStateBoundednessTests
{
    private const string Owner = BuiltInExtensionIds.Vulnerabilities;
    private const string SecondOwner = "second.extension";
    private const string LegacyOnlyOwner = "legacy.only.extension";

    [Fact]
    public async Task Reopening_a_store_loads_descriptors_and_headers_without_a_payload()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        long firstToken;
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            firstToken = (await store.WriteAsync(
                Owner, "key-0", Utf8("value-0"), null, CancellationToken.None)).ETag;
            await store.WriteAsync(Owner, "key-1", Utf8("value-1"), null, CancellationToken.None);
            await store.WriteAsync(Owner, "key-2", Utf8("value-2"), null, CancellationToken.None);
        }

        using var probe = new StatePayloadProbe();
        using var reopened = new TransactionalStateStore(root, Participants());

        Assert.Equal(0L, probe.Count);

        // The index still carries every committed record identity, so it was rebuilt
        // from record headers rather than from record payloads.
        var conflict = await Assert.ThrowsAsync<StateConcurrencyException>(
            () => reopened.WriteAsync(
                Owner, "key-0", Utf8("rejected"), firstToken - 1, CancellationToken.None).AsTask());
        Assert.Equal(firstToken, conflict.ActualETag);
        Assert.Equal(0L, probe.Count);

        var record = await reopened.ReadAsync(Owner, "key-1", CancellationToken.None);

        Assert.Equal("value-1", Text(record!.Value));
        Assert.Equal(1L, probe.Count);
        Assert.Equal(record.Value.LongLength, probe.Bytes);
    }

    [Fact]
    public async Task A_reopen_neither_reads_nor_validates_a_record_payload()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            await store.WriteAsync(Owner, "key", Utf8("value"), null, CancellationToken.None);
        }

        // The payload no longer matches the header this store committed. Opening the
        // store must not notice, because opening it must not read the payload.
        var file = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(root, TransactionalStateStore.ActiveDirectoryName),
            $"*{TransactionalStateStore.RecordExtension}",
            SearchOption.AllDirectories));
        var content = await File.ReadAllBytesAsync(file);
        content[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(file, content);

        using var reopened = new TransactionalStateStore(root, Participants());

        var failure = await Assert.ThrowsAsync<ExtensionStateException>(
            () => reopened.ReadAsync(Owner, "key", CancellationToken.None).AsTask());
        Assert.Contains("integrity", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_migration_materializes_each_record_once_to_migrate_and_once_to_mirror()
    {
        const int records = 4;
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            for (var index = 0; index < records; index++)
            {
                await store.WriteAsync(
                    Owner, $"key-{index}", Utf8($"v{index}"), null, CancellationToken.None);
            }
        }

        using var probe = new StatePayloadProbe();
        using var migrated = new TransactionalStateStore(root, [MigratingVulnerabilities()]);

        // One read migrates the record and one read rebuilds the version 1 mirror
        // projection from the committed tree. Neither the compatibility scan nor a
        // second descriptor pass may read the persisted set again.
        Assert.Equal(2L * records, probe.Count);
        Assert.Equal(
            "v0+2",
            Text((await migrated.ReadAsync(Owner, "key-0", CancellationToken.None))!.Value));
    }

    [Fact]
    public async Task Pre_transactional_state_is_adopted_at_schema_one_and_migrated()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");

        // State written before the transactional layout existed: version 1 records with
        // no participant descriptor at all.
        await new ExtensionStateStore(root).WriteRawAsync(
            Owner, "snapshot", Utf8("legacy"), CancellationToken.None);

        using var store = new TransactionalStateStore(root, [MigratingVulnerabilities()]);

        Assert.Equal(
            "legacy+2",
            Text((await store.ReadAsync(Owner, "snapshot", CancellationToken.None))!.Value));
        var persisted = Assert.Single(TransactionalStateStore.ReadPersistedParticipants(root));
        Assert.Equal(2, persisted.SchemaVersion);
        var mirrored = await new ExtensionStateStore(root).ReadRawAsync(
            Owner, "snapshot", CancellationToken.None);
        Assert.Equal("legacy+2", Text(mirrored!));
    }

    [Fact]
    public async Task A_restore_keeps_the_version_one_state_of_an_unregistered_extension()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using var store = new TransactionalStateStore(root, Participants());
        await store.WriteAsync(Owner, "key", Utf8("active"), null, CancellationToken.None);
        await store.WriteAsync(Owner, "stale", Utf8("dropped"), null, CancellationToken.None);

        // Version 1 state of an extension this build never registered. It is not a
        // projection of the transactional tree, so rebuilding the mirror must leave it
        // alone.
        await new ExtensionStateStore(root).WriteRawAsync(
            LegacyOnlyOwner, "kept", Utf8("legacy-only"), CancellationToken.None);

        var data = new StateCheckpointData(
            StateCheckpointData.CurrentManifestVersion,
            1,
            DateTimeOffset.UnixEpoch,
            [
                new StateCheckpointParticipant(
                    Owner,
                    "1.0.0",
                    KernelStateParticipants.VulnerabilitySchemaName,
                    1,
                    Required: false,
                    [new StateCheckpointRecord("key", Utf8("restored"), 9)])
            ]);
        using var staged = await store.StageRestoreAsync(data, CancellationToken.None);
        await store.CommitRestoreAsync(staged, CancellationToken.None);

        var legacy = new ExtensionStateStore(root);
        Assert.Equal(
            "legacy-only",
            Text((await legacy.ReadRawAsync(LegacyOnlyOwner, "kept", CancellationToken.None))!));
        Assert.Equal(
            "restored",
            Text((await legacy.ReadRawAsync(Owner, "key", CancellationToken.None))!));
        Assert.Null(await legacy.ReadRawAsync(Owner, "stale", CancellationToken.None));
    }

    [Fact]
    public async Task An_oversized_version_one_record_is_refused_before_it_is_materialized()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        await new ExtensionStateStore(root).WriteRawAsync(
            Owner, "big", new byte[4096], CancellationToken.None);

        using var probe = new StatePayloadProbe();
        var exception = Assert.Throws<StateQuotaExceededException>(
            () => new TransactionalStateStore(
                root,
                Participants(),
                new StateStoreQuotas(MaximumRecordBytes: 64, MaximumOwnerBytes: 128)).Dispose());

        Assert.Equal(0L, probe.Count);
        Assert.Contains("record", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Creating_a_durable_checkpoint_freezes_files_without_loading_them()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using var store = new TransactionalStateStore(root, Participants());
        await store.WriteAsync(Owner, "a", Utf8("alpha"), null, CancellationToken.None);
        await store.WriteAsync(Owner, "b", Utf8("beta"), null, CancellationToken.None);

        using var probe = new StatePayloadProbe();
        using var checkpoint = await store.CreateCheckpointAsync(CancellationToken.None);

        Assert.Equal(0L, probe.Count);

        // Freezing copies the committed record files, so a later mutation still cannot
        // change what the checkpoint exports even though creating it read no payload.
        await store.WriteAsync(Owner, "a", Utf8("mutated"), null, CancellationToken.None);
        var exported = await store.ExportCheckpointAsync(checkpoint, CancellationToken.None);

        var participant = Assert.Single(exported.Participants);
        Assert.Equal(
            "alpha",
            Text(Assert.Single(participant.Records, record => record.Key == "a").Value));
        Assert.Equal(2L, probe.Count);
    }

    [Fact]
    public async Task Streaming_participant_summaries_match_the_materialized_checkpoint()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using var store = new TransactionalStateStore(
            root,
            [
                new StateParticipantDescriptor(
                    Owner, "1.0.0", KernelStateParticipants.VulnerabilitySchemaName, 1),
                new StateParticipantDescriptor(SecondOwner, "2.5.0", "second-schema", 1)
            ]);
        await store.WriteAsync(Owner, "b", Utf8("beta"), null, CancellationToken.None);
        await store.WriteAsync(Owner, "a", Utf8("alpha"), null, CancellationToken.None);
        await store.WriteAsync(SecondOwner, "z", Utf8("zulu"), null, CancellationToken.None);
        using var checkpoint = await store.CreateCheckpointAsync(CancellationToken.None);
        var exported = await store.ExportCheckpointAsync(checkpoint, CancellationToken.None);
        var active = Path.Combine(root, TransactionalStateStore.ActiveDirectoryName);

        ImmutableArray<StateParticipantSummary> summaries;
        using (var probe = new StatePayloadProbe())
        {
            summaries = await TransactionalStateStore.SummarizeParticipantSetAsync(
                active, CancellationToken.None);
            Assert.Equal(0L, probe.Count);
        }

        Assert.Equal(exported.Participants.Length, summaries.Length);
        foreach (var participant in exported.Participants)
        {
            var summary = Assert.Single(
                summaries,
                candidate => candidate.ExtensionId == participant.ExtensionId);
            Assert.Equal(participant.ExtensionVersion, summary.ExtensionVersion);
            Assert.Equal(participant.SchemaName, summary.SchemaName);
            Assert.Equal(participant.SchemaVersion, summary.SchemaVersion);
            Assert.Equal(participant.Required, summary.Required);
            Assert.Equal(participant.Records.Length, summary.RecordCount);
            Assert.Equal(participant.ComputeIntegrity(), summary.Integrity);
        }
    }

    [Fact]
    public async Task Streaming_participant_summaries_enforce_the_record_quota()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using var store = new TransactionalStateStore(root, Participants());
        await store.WriteAsync(Owner, "a", Utf8("alpha"), null, CancellationToken.None);
        await store.WriteAsync(Owner, "b", Utf8("beta"), null, CancellationToken.None);
        var active = Path.Combine(root, TransactionalStateStore.ActiveDirectoryName);

        await Assert.ThrowsAsync<StateQuotaExceededException>(
            () => TransactionalStateStore.SummarizeParticipantSetAsync(
                active,
                CancellationToken.None,
                new StateStoreQuotas(MaximumRecordsPerOwner: 1)).AsTask());
    }

    private static StateParticipantDescriptor MigratingVulnerabilities() =>
        new(
            Owner,
            "1.0.0",
            KernelStateParticipants.VulnerabilitySchemaName,
            2,
            Required: false,
            [new StateSchemaMigration(1, 2, value => Utf8($"{Text(value)}+2"))]);

    private static ImmutableArray<StateParticipantDescriptor> Participants() =>
        KernelStateParticipants.BuiltIn;

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(byte[] value) => Encoding.UTF8.GetString(value);
}
