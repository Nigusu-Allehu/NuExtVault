using System.Collections.Immutable;
using System.Text;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Kernel.Capabilities;
using NuExtVault.Operations;

namespace NuExtVault.UnitTests;

public sealed class DurableOwnerIdentityMigrationTests
{
    private const string LegacyOwner = "NuTest.PackageStaging";
    private const string CurrentOwner = "NuExtVault.PackageStaging";
    private static readonly OwnerIdentityMigration Migration =
        new(LegacyOwner, CurrentOwner, new string('a', 64));

    [Fact]
    public async Task Legacy_state_content_and_journal_migrate_as_one_owner()
    {
        using var root = new TemporaryDirectory();
        var contentId = await SeedLegacyStoreAsync(root.Path);

        DurableOwnerIdentityMigrator.Migrate(root.Path, [Migration]);

        using var state = CurrentState(root.Path);
        using var content = new StagedContentStore(root.Path, "restarted");
        using var journal = new PublicationJournal(root.Path);
        Assert.Equal(
            """{"groupId":"legacy"}""",
            Encoding.UTF8.GetString((await state.ReadAsync(
                CurrentOwner,
                "group.legacy",
                CancellationToken.None))!.Value));
        Assert.NotNull(content.Find(CurrentOwner, contentId));
        Assert.Null(content.Find(LegacyOwner, contentId));
        Assert.NotNull(journal.Find(CurrentOwner, "publish-legacy"));
        Assert.Null(journal.Find(LegacyOwner, "publish-legacy"));
        Assert.DoesNotContain(
            LegacyOwner,
            Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
                .SelectMany(File.ReadAllLines),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Reopening_an_already_migrated_store_is_an_idempotent_no_op()
    {
        using var root = new TemporaryDirectory();
        await SeedLegacyStoreAsync(root.Path);
        DurableOwnerIdentityMigrator.Migrate(root.Path, [Migration]);
        var snapshot = Snapshot(root.Path);

        DurableOwnerIdentityMigrator.Migrate(root.Path, [Migration]);

        var reopened = Snapshot(root.Path);
        Assert.True(
            snapshot.SequenceEqual(reopened),
            $"Before:{Environment.NewLine}{string.Join(Environment.NewLine, snapshot)}" +
            $"{Environment.NewLine}After:{Environment.NewLine}" +
            string.Join(Environment.NewLine, reopened));
    }

    [Theory]
    [InlineData((int)OwnerIdentityMigrationFailPoint.AfterTransactionalState)]
    [InlineData((int)OwnerIdentityMigrationFailPoint.AfterStagedContent)]
    [InlineData((int)OwnerIdentityMigrationFailPoint.AfterPublicationJournal)]
    public async Task An_exact_journal_resumes_after_interruption(int failPointValue)
    {
        var failPoint = (OwnerIdentityMigrationFailPoint)failPointValue;
        using var root = new TemporaryDirectory();
        var contentId = await SeedLegacyStoreAsync(root.Path);
        var interrupted = new DurableOwnerIdentityMigrator(root.Path, [Migration])
        {
            FaultInjector = point =>
            {
                if (point == failPoint)
                {
                    throw new IOException("simulated crash");
                }
            }
        };
        Assert.Throws<IOException>(interrupted.Migrate);

        DurableOwnerIdentityMigrator.Migrate(root.Path, [Migration]);

        using var state = CurrentState(root.Path);
        using var content = new StagedContentStore(root.Path, "restarted");
        using var journal = new PublicationJournal(root.Path);
        Assert.NotNull(await state.ReadAsync(CurrentOwner, "group.legacy", CancellationToken.None));
        Assert.NotNull(content.Find(CurrentOwner, contentId));
        Assert.NotNull(journal.Find(CurrentOwner, "publish-legacy"));
        Assert.False(File.Exists(Path.Combine(
            root.Path,
            DurableOwnerIdentityMigrator.JournalFileName)));
    }

    [Fact]
    public async Task An_interrupted_journal_rejects_a_changed_declaration_set()
    {
        using var root = new TemporaryDirectory();
        await SeedLegacyStoreAsync(root.Path);
        var interrupted = new DurableOwnerIdentityMigrator(root.Path, [Migration])
        {
            FaultInjector = point =>
            {
                if (point == OwnerIdentityMigrationFailPoint.AfterTransactionalState)
                {
                    throw new IOException("simulated crash");
                }
            }
        };
        Assert.Throws<IOException>(interrupted.Migrate);

        var exception = Assert.Throws<InvalidDataException>(() =>
            DurableOwnerIdentityMigrator.Migrate(
                root.Path,
                [
                    Migration,
                    new OwnerIdentityMigration(
                        "Legacy.Other",
                        "Current.Other",
                        new string('b', 64))
                ]));

        Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_interrupted_journal_rejects_changed_administrator_authorization()
    {
        using var root = new TemporaryDirectory();
        await SeedLegacyStoreAsync(root.Path);
        var interrupted = new DurableOwnerIdentityMigrator(root.Path, [Migration])
        {
            FaultInjector = point =>
            {
                if (point == OwnerIdentityMigrationFailPoint.AfterTransactionalState)
                {
                    throw new IOException("simulated crash");
                }
            }
        };
        Assert.Throws<IOException>(interrupted.Migrate);

        var exception = Assert.Throws<InvalidDataException>(() =>
            DurableOwnerIdentityMigrator.Migrate(
                root.Path,
                [Migration with { AuthorizationDigest = new string('c', 64) }]));

        Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_interrupted_journal_rejects_startup_without_current_authorization()
    {
        using var root = new TemporaryDirectory();
        await SeedLegacyStoreAsync(root.Path);
        var interrupted = new DurableOwnerIdentityMigrator(root.Path, [Migration])
        {
            FaultInjector = point =>
            {
                if (point == OwnerIdentityMigrationFailPoint.AfterTransactionalState)
                {
                    throw new IOException("simulated crash");
                }
            }
        };
        Assert.Throws<IOException>(interrupted.Migrate);

        var exception = Assert.Throws<InvalidDataException>(
            () => DurableOwnerIdentityMigrator.Migrate(root.Path, []));

        Assert.Contains("authorization", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Populated_predecessors_converging_on_one_successor_fail_before_mutation()
    {
        using var root = new TemporaryDirectory();
        await SeedLegacyStoreAsync(root.Path);
        const string otherLegacy = "NuTest.OtherStaging";
        var stateRoot = Path.Combine(root.Path, "extension-state");
        using (var other = new TransactionalStateStore(
                   stateRoot,
                   [
                       new StateParticipantDescriptor(
                           LegacyOwner,
                           "1.0.0",
                           "package-staging",
                           1,
                           true),
                       new StateParticipantDescriptor(otherLegacy, "1.0.0", "other", 1)
                   ]))
        {
            await other.WriteAsync(
                otherLegacy,
                "record",
                "{}"u8.ToArray(),
                null,
                CancellationToken.None);
        }
        var migrations = new[]
        {
            Migration,
            new OwnerIdentityMigration(otherLegacy, CurrentOwner, new string('b', 64))
        };
        var predecessorDirectories = Directory.EnumerateDirectories(
                stateRoot,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToArray();

        var exception = Assert.Throws<InvalidDataException>(
            () => DurableOwnerIdentityMigrator.Migrate(root.Path, migrations));

        Assert.Contains("converge", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(
            root.Path,
            DurableOwnerIdentityMigrator.JournalFileName)));
        Assert.All(predecessorDirectories, directory => Assert.True(Directory.Exists(directory)));
    }

    [Theory]
    [InlineData((int)OwnerIdentityMigrationFailPoint.AfterTransactionalState)]
    [InlineData((int)OwnerIdentityMigrationFailPoint.AfterStagedContent)]
    [InlineData((int)OwnerIdentityMigrationFailPoint.AfterPublicationJournal)]
    public async Task Backup_refuses_every_interrupted_owner_migration_phase(int failPointValue)
    {
        using var root = new TemporaryDirectory();
        await SeedLegacyStoreAsync(root.Path);
        var interrupted = new DurableOwnerIdentityMigrator(root.Path, [Migration])
        {
            FaultInjector = point =>
            {
                if (point == (OwnerIdentityMigrationFailPoint)failPointValue)
                {
                    throw new IOException("simulated crash");
                }
            }
        };
        Assert.Throws<IOException>(interrupted.Migrate);
        var backupPath = Path.Combine(
            Path.GetDirectoryName(root.Path)!,
            $"{Guid.NewGuid():N}.zip");

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => StorageBackup.CreateAsync(root.Path, backupPath));
            Assert.Contains("restart", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(backupPath));
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    [Fact]
    public async Task A_committed_legacy_state_write_is_recovered_before_owner_migration()
    {
        using var root = new TemporaryDirectory();
        var stateRoot = Path.Combine(root.Path, "extension-state");
        using (var state = new TransactionalStateStore(
                   stateRoot,
                   [new StateParticipantDescriptor(
                       LegacyOwner,
                       "1.0.0",
                       "package-staging",
                       1,
                       true)]))
        {
            var before = await state.WriteAsync(
                LegacyOwner,
                "group.interrupted",
                "before"u8.ToArray(),
                null,
                CancellationToken.None);
            state.WriteFaultInjector = point =>
            {
                if (point == StateWriteFailPoint.AfterCommitJournal)
                {
                    throw new IOException("simulated old-server crash");
                }
            };
            await Assert.ThrowsAsync<ExtensionStateException>(async () =>
                await state.WriteAsync(
                    LegacyOwner,
                    "group.interrupted",
                    "committed"u8.ToArray(),
                    expectedETag: before.ETag,
                    CancellationToken.None));
        }

        DurableOwnerIdentityMigrator.Migrate(root.Path, [Migration]);

        using var migrated = CurrentState(root.Path);
        Assert.Equal(
            "committed",
            Encoding.UTF8.GetString((await migrated.ReadAsync(
                CurrentOwner,
                "group.interrupted",
                CancellationToken.None))!.Value));
    }

    [Fact]
    public async Task Mixed_old_and_new_owners_without_a_journal_fail_closed()
    {
        using var root = new TemporaryDirectory();
        await SeedLegacyStoreAsync(root.Path);
        using (var content = new StagedContentStore(root.Path, "current"))
        {
            await content.WriteAsync(
                CurrentOwner,
                new MemoryStream("new"u8.ToArray()),
                "application/octet-stream",
                64,
                null,
                null,
                CancellationToken.None);
        }

        var exception = Assert.Throws<InvalidDataException>(
            () => { DurableOwnerIdentityMigrator.Migrate(root.Path, [Migration]); });

        Assert.Contains("both", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_legacy_identity_is_not_a_runtime_alias_after_migration()
    {
        using var root = new TemporaryDirectory();
        await SeedLegacyStoreAsync(root.Path);
        DurableOwnerIdentityMigrator.Migrate(root.Path, [Migration]);
        using var state = CurrentState(root.Path);

        await Assert.ThrowsAsync<ExtensionStateException>(async () =>
            await state.ReadAsync(LegacyOwner, "group.legacy", CancellationToken.None));
    }

    private static TransactionalStateStore CurrentState(string root) =>
        new(
            Path.Combine(root, "extension-state"),
            [new StateParticipantDescriptor(
                CurrentOwner,
                "1.0.0",
                "package-staging",
                1,
                true)]);

    private static async Task<string> SeedLegacyStoreAsync(string root)
    {
        using (var state = new TransactionalStateStore(
                   Path.Combine(root, "extension-state"),
                   [new StateParticipantDescriptor(
                       LegacyOwner,
                       "1.0.0",
                       "package-staging",
                       1,
                       true)]))
        {
            await state.WriteAsync(
                LegacyOwner,
                "group.legacy",
                """{"groupId":"legacy"}"""u8.ToArray(),
                null,
                CancellationToken.None);
        }

        string contentId;
        using (var content = new StagedContentStore(root, "legacy-host"))
        {
            var written = await content.WriteAsync(
                LegacyOwner,
                new MemoryStream("legacy-content"u8.ToArray()),
                "application/octet-stream",
                1024,
                "Contoso.Legacy",
                "1.0.0",
                CancellationToken.None);
            contentId = written.Record!.ContentId;
        }

        using (var journal = new PublicationJournal(root))
        {
            await journal.BeginAsync(
                new PublicationJournalEntry(
                    "legacy-entry",
                    LegacyOwner,
                    "publish-legacy",
                    contentId,
                    null,
                    "group.legacy",
                    1,
                    "Contoso.Legacy",
                    "1.0.0",
                    new string('a', 64),
                    PublicationJournalPhase.Pending,
                    "Failed",
                    null,
                    null,
                    Convert.ToBase64String("""{"groupId":"legacy"}"""u8.ToArray()),
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None);
        }

        return contentId;
    }

    private static ImmutableArray<string> Snapshot(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path =>
                $"{Path.GetRelativePath(root, path)}:{Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}")];
}
