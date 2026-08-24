using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.UnitTests;

/// <summary>
/// Step 12A hardening coverage: a journal is only ever a store-generated name, a durable
/// batch is all-or-nothing, a batch the commit journal made authoritative is completed by
/// recovery — including the version 1 downgrade mirror it projects — and a schema
/// migration never destroys persisted state that belongs to an extension this build does
/// not activate.
/// </summary>
public sealed class ExtensionStateHardeningTests
{
    private const string Owner = BuiltInExtensionIds.Vulnerabilities;
    private const string RetiredOwner = "retired.extension";

    [Theory]
    [InlineData("../victim", "../secret")]
    [InlineData("..\\victim", "..\\secret")]
    [InlineData(".staging-1/../../victim", ".trash-1/../../secret")]
    [InlineData("v2", "../secret")]
    [InlineData("victim", "secret")]
    public async Task A_crafted_restore_journal_is_rejected_and_moves_nothing(
        string stagingDirectory,
        string trashDirectory)
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            await store.WriteAsync(Owner, "key", Utf8("original"), null, CancellationToken.None);
        }

        var victim = CreateExternalDirectory(temporary.Path, "victim");
        var secret = CreateExternalDirectory(temporary.Path, "secret");
        WriteJournalFile(root, stagingDirectory, trashDirectory);

        var exception = Assert.Throws<ExtensionStateException>(
            () => new TransactionalStateStore(root, Participants()).Dispose());
        Assert.Contains("journal", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            File.Exists(Path.Combine(victim, TransactionalStateStore.ActiveDirectoryName, "payload.txt")),
            "A crafted journal must never move an external directory.");
        Assert.True(
            File.Exists(Path.Combine(secret, "payload.txt")),
            "A crafted journal must never delete an external directory.");

        File.Delete(Path.Combine(root, TransactionalStateStore.RestoreJournalFileName));
        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Equal(
            "original",
            Text((await reopened.ReadAsync(Owner, "key", CancellationToken.None))!.Value));
    }

    [Fact]
    public async Task A_durable_batch_that_fails_while_staging_keeps_the_previous_state()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        long firstETag;
        long secondETag;
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            firstETag = (await store.WriteAsync(
                Owner, "a", Utf8("a1"), null, CancellationToken.None)).ETag;
            secondETag = (await store.WriteAsync(
                Owner, "b", Utf8("b1"), null, CancellationToken.None)).ETag;
            store.WriteFaultInjector = failPoint =>
            {
                if (failPoint == StateWriteFailPoint.AfterStage)
                {
                    throw new IOException("Injected staging failure.");
                }
            };

            await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.CompareAndSwapAsync(
                    Owner,
                    [
                        new StateEdit("a", firstETag, Utf8("a2")),
                        new StateEdit("b", secondETag, Utf8("b2"))
                    ],
                    CancellationToken.None).AsTask());

            store.WriteFaultInjector = null;
            var current = await store.ReadAsync(Owner, "a", CancellationToken.None);
            Assert.Equal("a1", Text(current!.Value));
            Assert.Equal(firstETag, current.ETag);
            Assert.Equal("b1", Text((await store.ReadAsync(Owner, "b", CancellationToken.None))!.Value));
        }

        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Equal(
            "a1",
            Text((await reopened.ReadAsync(Owner, "a", CancellationToken.None))!.Value));
        Assert.Equal(
            "b1",
            Text((await reopened.ReadAsync(Owner, "b", CancellationToken.None))!.Value));
        AssertNoPendingWork(root);
    }

    [Fact]
    public async Task A_cancelled_durable_batch_keeps_the_previous_state()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            await store.WriteAsync(Owner, "a", Utf8("a1"), null, CancellationToken.None);
            await store.WriteAsync(Owner, "b", Utf8("b1"), null, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            store.WriteFaultInjector = failPoint =>
            {
                if (failPoint == StateWriteFailPoint.AfterStage)
                {
                    cancellation.Cancel();
                }
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.CompareAndSwapAsync(
                    Owner,
                    [
                        new StateEdit("a", null, Utf8("a2")),
                        new StateEdit("b", null, Utf8("b2"))
                    ],
                    cancellation.Token).AsTask());

            store.WriteFaultInjector = null;
            Assert.Equal("a1", Text((await store.ReadAsync(Owner, "a", CancellationToken.None))!.Value));
            Assert.Equal("b1", Text((await store.ReadAsync(Owner, "b", CancellationToken.None))!.Value));
        }

        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Equal(
            "a1",
            Text((await reopened.ReadAsync(Owner, "a", CancellationToken.None))!.Value));
        Assert.Equal(
            "b1",
            Text((await reopened.ReadAsync(Owner, "b", CancellationToken.None))!.Value));
        AssertNoPendingWork(root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_durable_batch_interrupted_after_the_commit_journal_never_stays_partial(
        bool interruptWhilePublishing)
    {
        var failPoint = interruptWhilePublishing
            ? StateWriteFailPoint.BeforePublishRecord
            : StateWriteFailPoint.AfterCommitJournal;
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            await store.WriteAsync(Owner, "a", Utf8("a1"), null, CancellationToken.None);
            await store.WriteAsync(Owner, "b", Utf8("b1"), null, CancellationToken.None);
            var published = 0;
            store.WriteFaultInjector = point =>
            {
                if (point == failPoint &&
                    (point != StateWriteFailPoint.BeforePublishRecord ||
                     Interlocked.Increment(ref published) == 2))
                {
                    throw new IOException("Injected publish failure.");
                }
            };

            await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.CompareAndSwapAsync(
                    Owner,
                    [
                        new StateEdit("a", null, Utf8("a2")),
                        new StateEdit("b", null, Utf8("b2"))
                    ],
                    CancellationToken.None).AsTask());

            store.WriteFaultInjector = null;
            var refused = await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.WriteAsync(
                    Owner, "c", Utf8("c1"), null, CancellationToken.None).AsTask());
            Assert.Contains("pending write journal", refused.Message, StringComparison.Ordinal);
        }

        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Equal(
            "a2",
            Text((await reopened.ReadAsync(Owner, "a", CancellationToken.None))!.Value));
        Assert.Equal(
            "b2",
            Text((await reopened.ReadAsync(Owner, "b", CancellationToken.None))!.Value));
        AssertNoPendingWork(root);
    }

    /// <summary>
    /// A batch the commit journal made authoritative is rolled forward on the next open.
    /// The version 1 downgrade mirror is a projection of that batch, so recovery has to
    /// republish it too: an immediate downgrade after the crash must read the recovered
    /// value rather than the value the interrupted batch replaced. Every named point
    /// inside the batch is covered, including after the mirror was refreshed but before
    /// the journal was retired.
    /// </summary>
    [Theory]
    [InlineData(nameof(StateWriteFailPoint.AfterCommitJournal))]
    [InlineData(nameof(StateWriteFailPoint.BeforePublishRecord))]
    [InlineData(nameof(StateWriteFailPoint.BeforeMirrorRefresh))]
    [InlineData(nameof(StateWriteFailPoint.AfterMirrorRefresh))]
    public async Task Recovering_a_committed_batch_refreshes_the_downgrade_mirror(
        string failPointName)
    {
        var failPoint = Enum.Parse<StateWriteFailPoint>(failPointName);
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            await store.WriteAsync(Owner, "key", Utf8("before"), null, CancellationToken.None);
            await store.WriteAsync(Owner, "other", Utf8("other-1"), null, CancellationToken.None);
            Assert.Equal("before", await ReadDowngradedAsync(root, "key"));

            store.WriteFaultInjector = point =>
            {
                if (point == failPoint)
                {
                    throw new IOException("Injected crash.");
                }
            };

            var failure = await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.WriteAsync(
                    Owner, "key", Utf8("after"), null, CancellationToken.None).AsTask());
            Assert.Contains("pending write journal", failure.Message, StringComparison.Ordinal);
        }

        Assert.True(
            File.Exists(Path.Combine(root, TransactionalStateStore.WriteJournalFileName)),
            "An interrupted batch must leave the journal that completes it.");

        // A mirror this batch did not touch is left exactly as it was, so recovery is
        // scoped to the owner and keys the interrupted batch owned.
        await new ExtensionStateStore(root).WriteRawAsync(
            Owner, "other", Utf8("untouched"), CancellationToken.None);

        using var recovered = new TransactionalStateStore(root, Participants());

        Assert.Equal(
            "after",
            Text((await recovered.ReadAsync(Owner, "key", CancellationToken.None))!.Value));
        Assert.Equal("after", await ReadDowngradedAsync(root, "key"));
        Assert.Equal("untouched", await ReadDowngradedAsync(root, "other"));
        AssertNoPendingWork(root);
    }

    [Fact]
    public async Task Recovering_a_committed_batch_more_than_once_changes_nothing()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            await store.WriteAsync(Owner, "a", Utf8("a1"), null, CancellationToken.None);
            await store.WriteAsync(Owner, "b", Utf8("b1"), null, CancellationToken.None);
            store.WriteFaultInjector = point =>
            {
                if (point == StateWriteFailPoint.AfterCommitJournal)
                {
                    throw new IOException("Injected crash.");
                }
            };

            await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.CompareAndSwapAsync(
                    Owner,
                    [
                        new StateEdit("a", null, Utf8("a2")),
                        new StateEdit("b", null, Utf8("b2"))
                    ],
                    CancellationToken.None).AsTask());
        }

        TransactionalStateStore.Recover(root);
        var mirrored = await ReadDowngradedAsync(root, "a");
        TransactionalStateStore.Recover(root);
        TransactionalStateStore.Recover(root);

        Assert.Equal("a2", mirrored);
        Assert.Equal("a2", await ReadDowngradedAsync(root, "a"));
        Assert.Equal("b2", await ReadDowngradedAsync(root, "b"));
        AssertNoPendingWork(root);
        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Equal(
            "a2",
            Text((await reopened.ReadAsync(Owner, "a", CancellationToken.None))!.Value));
        Assert.Equal(
            "b2",
            Text((await reopened.ReadAsync(Owner, "b", CancellationToken.None))!.Value));
    }

    [Fact]
    public async Task A_locked_authoritative_record_leaves_a_committed_delete_for_recovery()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        long eTag;
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            eTag = (await store.WriteAsync(
                Owner, "key", Utf8("original"), null, CancellationToken.None)).ETag;
            var authoritative = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(root, TransactionalStateStore.ActiveDirectoryName),
                $"*{TransactionalStateStore.RecordExtension}",
                SearchOption.AllDirectories));
            await using var locked = new FileStream(
                authoritative,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.DeleteAsync(Owner, "key", eTag, CancellationToken.None).AsTask());
        }

        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Null(await reopened.ReadAsync(Owner, "key", CancellationToken.None));
        Assert.Null(await new ExtensionStateStore(root).ReadRawAsync(
            Owner, "key", CancellationToken.None));
    }

    [Fact]
    public async Task A_locked_compatibility_record_cannot_revive_a_reported_delete()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        long eTag;
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            eTag = (await store.WriteAsync(
                Owner, "key", Utf8("original"), null, CancellationToken.None)).ETag;
            var compatibility = new ExtensionStateStore(root).GetCompatibilityPath(Owner, "key");
            await using var locked = new FileStream(
                compatibility,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.DeleteAsync(Owner, "key", eTag, CancellationToken.None).AsTask());
        }

        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Null(await reopened.ReadAsync(Owner, "key", CancellationToken.None));
        Assert.Null(await new ExtensionStateStore(root).ReadRawAsync(
            Owner, "key", CancellationToken.None));
    }

    [Fact]
    public async Task A_delete_failure_before_the_commit_journal_retains_the_value_and_etag()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        long eTag;
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            eTag = (await store.WriteAsync(
                Owner, "key", Utf8("original"), null, CancellationToken.None)).ETag;
            store.WriteFaultInjector = point =>
            {
                if (point == StateWriteFailPoint.BeforeDeleteCommitJournal)
                {
                    throw new IOException("Injected pre-commit failure.");
                }
            };

            await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.DeleteAsync(Owner, "key", eTag, CancellationToken.None).AsTask());

            store.WriteFaultInjector = null;
            var retained = await store.ReadAsync(Owner, "key", CancellationToken.None);
            Assert.Equal("original", Text(retained!.Value));
            Assert.Equal(eTag, retained.ETag);
        }

        using var reopened = new TransactionalStateStore(root, Participants());
        var durable = await reopened.ReadAsync(Owner, "key", CancellationToken.None);
        Assert.Equal("original", Text(durable!.Value));
        Assert.Equal(eTag, durable.ETag);
        AssertNoPendingWork(root);
    }

    [Fact]
    public async Task A_delete_cancelled_before_commit_retains_the_value_and_etag()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using var cancellation = new CancellationTokenSource();
        long eTag;
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            eTag = (await store.WriteAsync(
                Owner, "key", Utf8("original"), null, CancellationToken.None)).ETag;
            store.WriteFaultInjector = point =>
            {
                if (point == StateWriteFailPoint.BeforeDeleteCommitJournal)
                {
                    cancellation.Cancel();
                }
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.DeleteAsync(Owner, "key", eTag, cancellation.Token).AsTask());
        }

        using var reopened = new TransactionalStateStore(root, Participants());
        var retained = await reopened.ReadAsync(Owner, "key", CancellationToken.None);
        Assert.Equal("original", Text(retained!.Value));
        Assert.Equal(eTag, retained.ETag);
        AssertNoPendingWork(root);
    }

    [Fact]
    public async Task Cancellation_after_the_delete_commit_point_rolls_forward()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using var cancellation = new CancellationTokenSource();
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            var record = await store.WriteAsync(
                Owner, "key", Utf8("original"), null, CancellationToken.None);
            store.WriteFaultInjector = point =>
            {
                if (point == StateWriteFailPoint.AfterDeleteCommitJournal)
                {
                    cancellation.Cancel();
                }
            };

            await store.DeleteAsync(Owner, "key", record.ETag, cancellation.Token);
            Assert.Null(await store.ReadAsync(Owner, "key", CancellationToken.None));
        }

        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Null(await reopened.ReadAsync(Owner, "key", CancellationToken.None));
        AssertNoPendingWork(root);
    }

    [Theory]
    [InlineData(nameof(StateWriteFailPoint.AfterDeleteCommitJournal))]
    [InlineData(nameof(StateWriteFailPoint.BeforeDeleteAuthoritativeRemoval))]
    [InlineData(nameof(StateWriteFailPoint.AfterDeleteAuthoritativeRemoval))]
    [InlineData(nameof(StateWriteFailPoint.BeforeDeleteMirrorRemoval))]
    [InlineData(nameof(StateWriteFailPoint.AfterDeleteMirrorRemoval))]
    public async Task An_interrupted_committed_delete_recovers_idempotently(string failPointName)
    {
        var failPoint = Enum.Parse<StateWriteFailPoint>(failPointName);
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        long deletedETag;
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            deletedETag = (await store.WriteAsync(
                Owner, "key", Utf8("original"), null, CancellationToken.None)).ETag;
            store.WriteFaultInjector = point =>
            {
                if (point == failPoint)
                {
                    throw new IOException("Injected committed delete interruption.");
                }
            };

            var failure = await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.DeleteAsync(
                    Owner, "key", deletedETag, CancellationToken.None).AsTask());
            Assert.Contains("committed", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(await store.ReadAsync(Owner, "key", CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(
                root, TransactionalStateStore.WriteJournalFileName)));
        }

        TransactionalStateStore.Recover(root);
        TransactionalStateStore.Recover(root);
        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Null(await reopened.ReadAsync(Owner, "key", CancellationToken.None));
        Assert.Null(await new ExtensionStateStore(root).ReadRawAsync(
            Owner, "key", CancellationToken.None));
        var recreated = await reopened.WriteAsync(
            Owner, "key", Utf8("recreated"), null, CancellationToken.None);
        Assert.True(recreated.ETag > deletedETag);
        AssertNoPendingWork(root);
    }

    [Fact]
    public async Task Checkpoint_and_restore_refuse_a_committed_delete_awaiting_recovery()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            var original = await store.WriteAsync(
                Owner, "key", Utf8("original"), null, CancellationToken.None);
            var restoreData = new StateCheckpointData(
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
                        [new StateCheckpointRecord("key", Utf8("restored"), original.ETag + 1)])
                ]);
            using var staged = await store.StageRestoreAsync(
                restoreData, CancellationToken.None);
            store.WriteFaultInjector = point =>
            {
                if (point == StateWriteFailPoint.BeforeDeleteAuthoritativeRemoval)
                {
                    throw new IOException("Injected committed delete interruption.");
                }
            };
            await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.DeleteAsync(
                    Owner, "key", original.ETag, CancellationToken.None).AsTask());

            await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.CreateCheckpointAsync(CancellationToken.None).AsTask());
            await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.CommitRestoreAsync(staged, CancellationToken.None).AsTask());
        }

        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Null(await reopened.ReadAsync(Owner, "key", CancellationToken.None));
        AssertNoPendingWork(root);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"../../victim\"]")]
    [InlineData("[\"not-a-hash-name\"]")]
    [InlineData("[\"AB\"]")]
    public async Task A_crafted_write_journal_is_rejected_and_publishes_nothing(string recordsJson)
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            await store.WriteAsync(Owner, "key", Utf8("original"), null, CancellationToken.None);
        }

        var victim = CreateExternalDirectory(temporary.Path, "victim");
        var journalPath = Path.Combine(root, TransactionalStateStore.WriteJournalFileName);
        File.WriteAllText(
            journalPath,
            $$"""
            {"Version":1,"StagingDirectory":".staging-1",
             "OwnerDirectory":"{{new string('a', 64)}}","BatchId":1,"Records":{{recordsJson}}}
            """);

        var exception = Assert.Throws<ExtensionStateException>(
            () => new TransactionalStateStore(root, Participants()).Dispose());

        Assert.Contains("journal", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            File.Exists(Path.Combine(victim, "payload.txt")),
            "A crafted journal must never touch an external directory.");
        File.Delete(journalPath);
        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Equal(
            "original",
            Text((await reopened.ReadAsync(Owner, "key", CancellationToken.None))!.Value));
        Assert.Equal("original", await ReadDowngradedAsync(root, "key"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"../../victim\"]")]
    [InlineData("[\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"," +
                "\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"]")]
    public async Task A_crafted_delete_journal_is_rejected_and_removes_nothing(
        string deletedRecordsJson)
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            await store.WriteAsync(Owner, "key", Utf8("original"), null, CancellationToken.None);
        }

        var victim = CreateExternalDirectory(temporary.Path, "victim");
        var journalPath = Path.Combine(root, TransactionalStateStore.WriteJournalFileName);
        File.WriteAllText(
            journalPath,
            $$"""
            {"Version":2,"StagingDirectory":".staging-1",
             "OwnerDirectory":"{{new string('a', 64)}}","BatchId":1,"Records":[],
             "DeletedRecords":{{deletedRecordsJson}}}
            """);

        var exception = Assert.Throws<ExtensionStateException>(
            () => new TransactionalStateStore(root, Participants()).Dispose());

        Assert.Contains("journal", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            File.Exists(Path.Combine(victim, "payload.txt")),
            "A crafted delete journal must never touch an external file.");
        File.Delete(journalPath);
        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Equal(
            "original",
            Text((await reopened.ReadAsync(Owner, "key", CancellationToken.None))!.Value));
        Assert.Equal("original", await ReadDowngradedAsync(root, "key"));
    }

    [Fact]
    public async Task A_schema_migration_preserves_persisted_state_of_an_inactive_extension()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(
            root,
            [
                new StateParticipantDescriptor(
                    Owner, "1.0.0", KernelStateParticipants.VulnerabilitySchemaName, 1),
                new StateParticipantDescriptor(RetiredOwner, "1.0.0", "retired", 1)
            ]))
        {
            await store.WriteAsync(Owner, "key", Utf8("v1"), null, CancellationToken.None);
            await store.WriteAsync(
                RetiredOwner, "kept", Utf8("retired-data"), null, CancellationToken.None);
        }

        using var migrated = new TransactionalStateStore(root, [MigratingVulnerabilities()]);

        Assert.Equal(
            "v1+2",
            Text((await migrated.ReadAsync(Owner, "key", CancellationToken.None))!.Value));
        var preserved = await ReadQuarantinedAsync(root, RetiredOwner);
        Assert.Equal("retired", preserved.SchemaName);
        var record = Assert.Single(preserved.Records);
        Assert.Equal("kept", record.Key);
        Assert.Equal("retired-data", Text(record.Value));
    }

    [Fact]
    public async Task Persisted_state_of_an_inactive_extension_is_recoverable_after_migration()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(
            root,
            [
                new StateParticipantDescriptor(
                    Owner, "1.0.0", KernelStateParticipants.VulnerabilitySchemaName, 1),
                new StateParticipantDescriptor(RetiredOwner, "1.0.0", "retired", 1)
            ]))
        {
            await store.WriteAsync(Owner, "key", Utf8("v1"), null, CancellationToken.None);
            await store.WriteAsync(
                RetiredOwner, "kept", Utf8("retired-data"), null, CancellationToken.None);
        }

        using (var migrated = new TransactionalStateStore(root, [MigratingVulnerabilities()]))
        {
            Assert.Equal(
                "v1+2",
                Text((await migrated.ReadAsync(Owner, "key", CancellationToken.None))!.Value));
        }

        var quarantined = await ReadQuarantinedAsync(root, RetiredOwner);
        using var restored = new TransactionalStateStore(
            root,
            [
                MigratingVulnerabilities(),
                new StateParticipantDescriptor(RetiredOwner, "1.0.0", "retired", 1)
            ]);
        var report = await RestoreQuarantinedAsync(restored, quarantined);

        Assert.Empty(report.QuarantinedExtensions);
        Assert.Equal(
            "retired-data",
            Text((await restored.ReadAsync(RetiredOwner, "kept", CancellationToken.None))!.Value));
    }

    [Fact]
    public async Task A_version_one_record_from_a_downgraded_build_survives_the_next_migration()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(
            root,
            [
                new StateParticipantDescriptor(
                    Owner, "1.0.0", KernelStateParticipants.VulnerabilitySchemaName, 1)
            ]))
        {
            await store.WriteAsync(Owner, "key", Utf8("v1"), null, CancellationToken.None);
        }

        // An older build was run against the same storage and only wrote the version 1
        // record. A migration republishes the mirror from the authoritative tree, so the
        // record has to be adopted before the migration rather than after it.
        await new ExtensionStateStore(root).WriteRawAsync(
            Owner, "downgraded", Utf8("only-v1"), CancellationToken.None);

        using var migrated = new TransactionalStateStore(root, [MigratingVulnerabilities()]);

        Assert.Equal(
            "v1+2",
            Text((await migrated.ReadAsync(Owner, "key", CancellationToken.None))!.Value));
        Assert.Equal(
            "only-v1+2",
            Text((await migrated.ReadAsync(Owner, "downgraded", CancellationToken.None))!.Value));
        var mirrored = await new ExtensionStateStore(root).ReadRawAsync(
            Owner, "downgraded", CancellationToken.None);
        Assert.Equal(
            "only-v1+2",
            Text(mirrored ?? throw new InvalidOperationException(
                "The downgrade mirror must keep the migrated record readable.")));
    }

    [Fact]
    public async Task Persisted_state_of_a_required_but_inactive_extension_fails_to_open()
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(
            root,
            [
                new StateParticipantDescriptor(
                    Owner, "1.0.0", KernelStateParticipants.VulnerabilitySchemaName, 1),
                new StateParticipantDescriptor(
                    RetiredOwner, "1.0.0", "retired", 1, Required: true)
            ]))
        {
            await store.WriteAsync(Owner, "key", Utf8("v1"), null, CancellationToken.None);
            await store.WriteAsync(
                RetiredOwner, "kept", Utf8("retired-data"), null, CancellationToken.None);
        }

        var exception = Assert.Throws<StateSchemaCompatibilityException>(
            () => new TransactionalStateStore(root, [MigratingVulnerabilities()]).Dispose());

        Assert.Contains(RetiredOwner, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_built_in_vulnerability_state_is_optional_because_it_is_rebuildable()
    {
        var participant = Assert.Single(
            KernelStateParticipants.BuiltIn,
            candidate => candidate.ExtensionId == BuiltInExtensionIds.Vulnerabilities);

        Assert.False(
            participant.Required,
            "A vulnerability snapshot can be absent, refreshed, or rebuilt from the embedded " +
            "snapshot, so a backup without it must still restore.");
        Assert.True(
            new StateParticipantDescriptor("required.extension", "1.0.0", "schema", 1, Required: true)
                .Validate().Required,
            "The participant model must still support state an extension cannot rebuild.");
    }

    private static async Task<StateRestoreReport> RestoreQuarantinedAsync(
        TransactionalStateStore store,
        StateCheckpointParticipant quarantined)
    {
        using var checkpoint = await store.CreateCheckpointAsync(CancellationToken.None);
        var exported = await store.ExportCheckpointAsync(checkpoint, CancellationToken.None);
        var data = exported with
        {
            Participants =
            [
                .. exported.Participants.Where(participant => !string.Equals(
                    participant.ExtensionId,
                    quarantined.ExtensionId,
                    StringComparison.Ordinal)),
                quarantined
            ]
        };
        using var staged = await store.StageRestoreAsync(data, CancellationToken.None);
        return await store.CommitRestoreAsync(staged, CancellationToken.None);
    }

    private static async Task<StateCheckpointParticipant> ReadQuarantinedAsync(
        string root,
        string extensionId)
    {
        var quarantine = Path.Combine(root, TransactionalStateStore.QuarantineDirectoryName);
        Assert.True(Directory.Exists(quarantine), "Inactive persisted state must be quarantined.");
        foreach (var directory in Directory.EnumerateDirectories(quarantine))
        {
            var participants = await TransactionalStateStore.ReadParticipantSetAsync(
                directory,
                CancellationToken.None);
            var match = participants.FirstOrDefault(participant => string.Equals(
                participant.ExtensionId,
                extensionId,
                StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        Assert.Fail($"Quarantined state for '{extensionId}' was not preserved.");
        throw new InvalidOperationException();
    }

    /// <summary>
    /// Reads a record through the version 1 reader an older server build uses, which is
    /// what an immediate downgrade after a crash would see.
    /// </summary>
    private static async Task<string> ReadDowngradedAsync(string root, string key)
    {
        var mirrored = await new ExtensionStateStore(root).ReadRawAsync(
            Owner,
            key,
            CancellationToken.None);
        return Text(mirrored ?? throw new InvalidOperationException(
            $"The downgrade mirror must keep '{key}' readable."));
    }

    private static StateParticipantDescriptor MigratingVulnerabilities() =>
        new(
            Owner,
            "1.0.0",
            KernelStateParticipants.VulnerabilitySchemaName,
            2,
            Required: false,
            [new StateSchemaMigration(1, 2, value => Utf8($"{Text(value)}+2"))]);

    private static void AssertNoPendingWork(string root)
    {
        Assert.False(
            File.Exists(Path.Combine(root, TransactionalStateStore.WriteJournalFileName)),
            "A completed open must leave no pending write journal.");
        Assert.Empty(Directory.EnumerateDirectories(
            root,
            $"{TransactionalStateStore.StagingPrefix}*"));
        Assert.Empty(Directory.EnumerateDirectories(
            root,
            $"{TransactionalStateStore.TrashPrefix}*"));
    }

    private static string CreateExternalDirectory(string parent, string name)
    {
        var directory = Path.Combine(parent, name);
        Directory.CreateDirectory(
            Path.Combine(directory, TransactionalStateStore.ActiveDirectoryName));
        File.WriteAllText(Path.Combine(directory, "payload.txt"), name);
        File.WriteAllText(
            Path.Combine(directory, TransactionalStateStore.ActiveDirectoryName, "payload.txt"),
            name);
        return directory;
    }

    private static void WriteJournalFile(
        string root,
        string stagingDirectory,
        string trashDirectory) =>
        File.WriteAllText(
            Path.Combine(root, TransactionalStateStore.RestoreJournalFileName),
            JsonSerializer.Serialize(new
            {
                Version = 1,
                StagingDirectory = stagingDirectory,
                TrashDirectory = trashDirectory,
                RestoreId = 1L
            }));

    /// <summary>
    /// Version 1 adoption is a single all-or-nothing admission. The aggregate record and
    /// byte quotas the resulting owner would hold are validated before the first record is
    /// persisted, so a rejected adoption can never leave part of itself in the
    /// authoritative tree for a later open to accept without revalidating it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Version_one_adoption_over_an_owner_quota_persists_nothing_on_every_open(
        bool byRecordCount)
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        var legacy = new ExtensionStateStore(root);
        for (var index = 0; index < 3; index++)
        {
            await legacy.WriteRawAsync(Owner, $"key-{index}", new byte[48], CancellationToken.None);
        }

        var quotas = byRecordCount
            ? new StateStoreQuotas(
                MaximumRecordBytes: 64,
                MaximumRecordsPerOwner: 2,
                MaximumOwnerBytes: 1024)
            : new StateStoreQuotas(
                MaximumRecordBytes: 64,
                MaximumRecordsPerOwner: 16,
                MaximumOwnerBytes: 100);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var exception = Assert.Throws<StateQuotaExceededException>(
                () => new TransactionalStateStore(root, Participants(), quotas).Dispose());

            Assert.Contains(Owner, exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(
                root,
                $"*{TransactionalStateStore.RecordExtension}",
                SearchOption.AllDirectories));
            Assert.Equal(
                new byte[48],
                await new ExtensionStateStore(root).ReadRawAsync(
                    Owner, "key-0", CancellationToken.None));
        }

        // The refusal is a quota decision, not corruption, so a store whose quotas admit
        // the same version 1 state still adopts all of it.
        using var admitted = new TransactionalStateStore(root, Participants());
        for (var index = 0; index < 3; index++)
        {
            Assert.NotNull(await admitted.ReadAsync(
                Owner, $"key-{index}", CancellationToken.None));
        }
    }

    /// <summary>
    /// A committed tree that is already over quota is refused on every open rather than
    /// being loaded as authoritative state, so an over-quota tree can never become the
    /// baseline a later write extends.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_over_quota_committed_tree_fails_closed_on_every_open(bool byRecordCount)
    {
        using var temporary = new TemporaryDirectory();
        var root = Path.Combine(temporary.Path, "extension-state");
        using (var store = new TransactionalStateStore(root, Participants()))
        {
            for (var index = 0; index < 3; index++)
            {
                await store.WriteAsync(
                    Owner, $"key-{index}", new byte[48], null, CancellationToken.None);
            }
        }

        var quotas = byRecordCount
            ? new StateStoreQuotas(
                MaximumRecordBytes: 64,
                MaximumRecordsPerOwner: 2,
                MaximumOwnerBytes: 1024)
            : new StateStoreQuotas(
                MaximumRecordBytes: 64,
                MaximumRecordsPerOwner: 16,
                MaximumOwnerBytes: 100);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var exception = Assert.Throws<StateQuotaExceededException>(
                () => new TransactionalStateStore(root, Participants(), quotas).Dispose());
            Assert.Contains(Owner, exception.Message, StringComparison.Ordinal);
        }

        using var reopened = new TransactionalStateStore(root, Participants());
        Assert.Equal(
            new byte[48],
            (await reopened.ReadAsync(Owner, "key-1", CancellationToken.None))!.Value);
    }

    private static ImmutableArray<StateParticipantDescriptor> Participants() =>
        KernelStateParticipants.BuiltIn;

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(byte[] value) => Encoding.UTF8.GetString(value);
}
