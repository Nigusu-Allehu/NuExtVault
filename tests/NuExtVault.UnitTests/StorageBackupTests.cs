using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NuExtVault.Hosting;
using NuExtVault.Kernel.Capabilities;
using NuExtVault.Operations;
using NuExtVault.Packages;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.UnitTests;

public sealed class StorageBackupTests
{
    [Fact]
    public async Task Restore_rejects_an_ambiguous_owner_migration_graph()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var backupPath = Path.Combine(source.Path, "backup.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            StorageBackup.RestoreAsync(
                backupPath,
                destination.Path,
                KernelStateParticipants.BuiltIn,
                [
                    new OwnerIdentityMigration("Legacy.Owner", "Current.One"),
                    new OwnerIdentityMigration("Legacy.Owner", "Current.Two")
                ],
                CancellationToken.None));

        Assert.Contains("multiple successors", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Legacy_required_staging_state_restores_then_migrates_to_the_current_owner()
    {
        const string legacy = "NuTest.PackageStaging";
        const string current = "NuExtVault.PackageStaging";
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var legacyParticipant = new StateParticipantDescriptor(
            legacy,
            "1.0.0",
            "package-staging",
            1,
            true);
        using (var state = new TransactionalStateStore(
                   Path.Combine(source.Path, StorageBackup.ExtensionStateDirectoryName),
                   [legacyParticipant]))
        {
            await state.WriteAsync(
                legacy,
                "group.backup",
                """{"groupId":"backup"}"""u8.ToArray(),
                null,
                CancellationToken.None);
        }

        string contentId;
        using (var content = new StagedContentStore(source.Path, "legacy-host"))
        {
            contentId = (await content.WriteAsync(
                legacy,
                new MemoryStream("backup-content"u8.ToArray()),
                "application/octet-stream",
                1024,
                "Contoso.Backup",
                "1.0.0",
                CancellationToken.None)).Record!.ContentId;
        }
        using (var journal = new PublicationJournal(source.Path))
        {
            await journal.BeginAsync(
                new PublicationJournalEntry(
                    "backup-entry",
                    legacy,
                    "backup-key",
                    contentId,
                    null,
                    "group.backup",
                    1,
                    "Contoso.Backup",
                    "1.0.0",
                    new string('a', 64),
                    PublicationJournalPhase.Pending,
                    "Failed",
                    null,
                    null,
                    Convert.ToBase64String("""{"groupId":"backup"}"""u8.ToArray()),
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch),
                CancellationToken.None);
        }
        var backupPath = Path.Combine(source.Path, "legacy.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);
        var currentParticipant = legacyParticipant with { ExtensionId = current };
        var migration = new OwnerIdentityMigration(legacy, current);

        await StorageBackup.RestoreAsync(
            backupPath,
            destination.Path,
            [currentParticipant],
            [migration],
            CancellationToken.None);
        DurableOwnerIdentityMigrator.Migrate(destination.Path, [migration]);

        using var restoredState = new TransactionalStateStore(
            Path.Combine(destination.Path, StorageBackup.ExtensionStateDirectoryName),
            [currentParticipant]);
        using var restoredContent = new StagedContentStore(destination.Path, "restored");
        using var restoredJournal = new PublicationJournal(destination.Path);
        Assert.NotNull(await restoredState.ReadAsync(
            current,
            "group.backup",
            CancellationToken.None));
        Assert.NotNull(restoredContent.Find(current, contentId));
        Assert.NotNull(restoredJournal.Find(current, "backup-key"));
    }

    [Fact]
    public async Task Backup_restores_packages_extension_state_and_legacy_cache_into_clean_storage()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var sourceStore = new InMemoryPackageStore(source.Path);
        await sourceStore.AddAsync(
            TestPackageBuilder.Create("Recovered.Package", "1.2.3").Build());
        var vulnerabilityDirectory = Path.Combine(
            source.Path,
            "vulnerabilities",
            "snapshot");
        Directory.CreateDirectory(vulnerabilityDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(vulnerabilityDirectory, "metadata.json"),
            """{"id":"snapshot"}""");
        var extensionState = Path.Combine(
            source.Path,
            "extension-state",
            "owner",
            "snapshot.json");
        Directory.CreateDirectory(Path.GetDirectoryName(extensionState)!);
        await File.WriteAllTextAsync(extensionState, """{"state":"snapshot"}""");
        var backupPath = Path.Combine(source.Path, "backup.zip");

        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        await StorageBackup.RestoreAsync(backupPath, destination.Path);

        var restoredStore = new InMemoryPackageStore(destination.Path);
        Assert.NotNull(await restoredStore.FindAsync("Recovered.Package", "1.2.3"));
        Assert.True(File.Exists(
            Path.Combine(
                destination.Path,
                "vulnerabilities",
                "snapshot",
                "metadata.json")));
        Assert.Equal(
            """{"state":"snapshot"}""",
            await File.ReadAllTextAsync(
                Path.Combine(
                    destination.Path,
                    "extension-state",
                    "owner",
                    "snapshot.json")));
        Assert.Contains(
            manifest.Files,
            file => file.Path == "packages/recovered.package/1.2.3/recovered.package.1.2.3.nupkg");
        Assert.Contains(
            manifest.Files,
            file => file.Path == "vulnerabilities/snapshot/metadata.json");
        Assert.Contains(
            manifest.Files,
            file => file.Path == "extension-state/owner/snapshot.json");
    }

    [Fact]
    public async Task Restore_rejects_content_that_does_not_match_the_manifest()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var sourceStore = new InMemoryPackageStore(source.Path);
        await sourceStore.AddAsync(
            TestPackageBuilder.Create("Tampered.Package", "1.0.0").Build());
        var backupPath = Path.Combine(source.Path, "backup.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);
        using (var archive = ZipFile.Open(backupPath, ZipArchiveMode.Update))
        {
            var packageEntry = Assert.Single(
                archive.Entries,
                entry => entry.FullName.EndsWith(".nupkg", StringComparison.Ordinal));
            var packagePath = packageEntry.FullName;
            packageEntry.Delete();
            var replacement = archive.CreateEntry(packagePath);
            await using var writer = new StreamWriter(replacement.Open());
            await writer.WriteAsync("tampered");
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => StorageBackup.RestoreAsync(backupPath, destination.Path));

        Assert.Contains("integrity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [Path.Combine(destination.Path, ".storage.lock")],
            Directory.EnumerateFileSystemEntries(destination.Path));
    }

    [Fact]
    public async Task Backup_restores_current_durable_database_and_security_state()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await using (var store = new DurablePackageStore(source.Path))
        await using (var supplyChain = new PackageSupplyChainService(store, source.Path))
        {
            var result = await supplyChain.PublishAsync(new PackagePublicationRequest(
                TestPackageBuilder.Create("Durable.Backup", "1.0.0").Build(),
                "publisher",
                "default",
                Administrator: false));
            Assert.Equal(PackagePublicationOutcome.Published, result.Outcome);
        }

        var securityDirectory = Path.Combine(source.Path, "security");
        Directory.CreateDirectory(securityDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(securityDirectory, "package-owners.json"),
            """{"Durable.Backup":"publisher"}""");
        var packageRelativePath = Path.Combine(
            "durable.backup",
            "1.0.0",
            "durable.backup.1.0.0.nupkg");
        var publishedPackage = Path.Combine(source.Path, "packages", packageRelativePath);
        var pendingDelete = Path.Combine(source.Path, "trash", packageRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(pendingDelete)!);
        File.Move(publishedPackage, pendingDelete);
        var backupPath = Path.Combine(source.Path, "backup.zip");

        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        await StorageBackup.RestoreAsync(backupPath, destination.Path);

        Assert.Contains(manifest.Files, file => file.Path == "packages.db");
        Assert.Contains(manifest.Files, file => file.Path == "supply-chain.db");
        Assert.Contains(
            manifest.Files,
            file => file.Path == "security/package-owners.json");
        Assert.Contains(
            manifest.Files,
            file => file.Path == "trash/durable.backup/1.0.0/durable.backup.1.0.0.nupkg");
        await using var restoredStore = new DurablePackageStore(destination.Path);
        await using var restoredSupplyChain =
            new PackageSupplyChainService(restoredStore, destination.Path);
        Assert.NotNull(await restoredStore.FindAsync("Durable.Backup", "1.0.0"));
        Assert.NotNull(await restoredSupplyChain.GetStatusAsync("Durable.Backup", "1.0.0"));
    }

    [Fact]
    public async Task Restore_rejects_a_live_target_without_removing_its_packages()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var sourceStore = new InMemoryPackageStore(source.Path);
        await sourceStore.AddAsync(
            TestPackageBuilder.Create("Backup.Source", "1.0.0").Build());
        var backupPath = Path.Combine(source.Path, "backup.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);
        await using var liveStore = new DurablePackageStore(destination.Path);
        await liveStore.AddAsync(
            TestPackageBuilder.Create("Live.Target", "1.0.0").Build());

        var exception = await Assert.ThrowsAsync<StorageCheckpointUnavailableException>(
            () => StorageBackup.RestoreAsync(backupPath, destination.Path));

        Assert.IsAssignableFrom<IOException>(exception);
        Assert.Contains("in use", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await liveStore.FindAsync("Live.Target", "1.0.0"));
    }

    [Fact]
    public async Task Backup_records_typed_participants_and_one_checkpoint()
    {
        using var source = TemporaryDirectory.Create();
        await WriteStateAsync(source.Path, KernelStateParticipants.BuiltIn, "snapshot-v1", "value");
        var backupPath = Path.Combine(source.Path, "backup.zip");

        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);

        Assert.Equal(2, manifest.Version);
        Assert.NotNull(manifest.CheckpointId);
        var participant = Assert.Single(manifest.Participants!);
        Assert.Equal(BuiltInExtensionIds.Vulnerabilities, participant.ExtensionId);
        Assert.Equal(KernelStateParticipants.VulnerabilitySchemaName, participant.SchemaName);
        Assert.Equal(1, participant.SchemaVersion);
        Assert.Equal(1, participant.RecordCount);
        Assert.NotEmpty(participant.Sha256);
    }

    [Fact]
    public async Task Backup_fails_unavailable_while_storage_is_live()
    {
        using var source = TemporaryDirectory.Create();
        await using var liveStore = new DurablePackageStore(source.Path);
        await liveStore.AddAsync(TestPackageBuilder.Create("Live.Source", "1.0.0").Build());

        var exception = await Assert.ThrowsAsync<StorageCheckpointUnavailableException>(
            () => StorageBackup.CreateAsync(
                source.Path,
                Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip")));

        Assert.IsAssignableFrom<IOException>(exception);
    }

    [Fact]
    public async Task Restore_round_trips_extension_state_into_the_transactional_store()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await WriteStateAsync(source.Path, KernelStateParticipants.BuiltIn, "snapshot-v1", "value");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);

        await StorageBackup.RestoreAsync(backupPath, destination.Path);

        using var restored = new TransactionalStateStore(
            Path.Combine(destination.Path, "extension-state"),
            KernelStateParticipants.BuiltIn);
        var record = await restored.ReadAsync(
            BuiltInExtensionIds.Vulnerabilities, "snapshot-v1", CancellationToken.None);
        Assert.Equal("value", Encoding.UTF8.GetString(record!.Value));
        Assert.False(File.Exists(
            Path.Combine(destination.Path, StorageBackup.RestoreJournalName)));
    }

    [Fact]
    public async Task Restore_rejects_a_backup_that_requires_an_unknown_extension()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await WriteStateAsync(
            source.Path,
            [new StateParticipantDescriptor("retired.required", "1.0.0", "retired", 1, true)],
            "key",
            "value");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => StorageBackup.RestoreAsync(backupPath, destination.Path));

        Assert.Contains("retired.required", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            [Path.Combine(destination.Path, ".storage.lock")],
            Directory.EnumerateFileSystemEntries(destination.Path));
    }

    [Fact]
    public async Task Restore_rejects_a_newer_participant_schema()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await WriteStateAsync(source.Path, KernelStateParticipants.BuiltIn, "snapshot-v1", "value");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        await ReplaceManifestAsync(
            backupPath,
            manifest with
            {
                Participants = [manifest.Participants![0] with { SchemaVersion = 9 }]
            });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => StorageBackup.RestoreAsync(backupPath, destination.Path));

        Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Backup_fails_when_persisted_state_is_newer_than_this_build()
    {
        using var source = TemporaryDirectory.Create();
        await WriteStateAsync(
            source.Path,
            [
                new StateParticipantDescriptor(
                    BuiltInExtensionIds.Vulnerabilities,
                    "9.0.0",
                    KernelStateParticipants.VulnerabilitySchemaName,
                    SchemaVersion: 9)
            ],
            "snapshot-v1",
            "value");

        await Assert.ThrowsAsync<StateSchemaCompatibilityException>(
            () => StorageBackup.CreateAsync(
                source.Path,
                Path.Combine(source.Path, "backup.zip")));
    }

    [Fact]
    public async Task Restore_rejects_missing_required_participant_state()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await WriteStateAsync(source.Path, KernelStateParticipants.BuiltIn, "snapshot-v1", "value");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => StorageBackup.RestoreAsync(
                backupPath,
                destination.Path,
                [
                    .. KernelStateParticipants.BuiltIn,
                    new StateParticipantDescriptor("nuget.staging", "1.0.0", "staging", 1, true)
                ],
                CancellationToken.None));

        Assert.Contains("nuget.staging", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_rejects_participant_state_that_fails_its_integrity_hash()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await WriteStateAsync(source.Path, KernelStateParticipants.BuiltIn, "snapshot-v1", "value");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        await ReplaceManifestAsync(
            backupPath,
            manifest with
            {
                Participants =
                [
                    manifest.Participants![0] with { Sha256 = new string('0', 64) }
                ]
            });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => StorageBackup.RestoreAsync(backupPath, destination.Path));

        Assert.Contains("integrity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_quarantines_state_for_an_inactive_extension()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await WriteStateAsync(
            source.Path,
            [
                .. KernelStateParticipants.BuiltIn,
                new StateParticipantDescriptor("retired.extension", "1.0.0", "retired", 1)
            ],
            "snapshot-v1",
            "value",
            "retired.extension");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);

        await StorageBackup.RestoreAsync(backupPath, destination.Path);

        var stateRoot = Path.Combine(destination.Path, "extension-state");
        var quarantine = Path.Combine(stateRoot, "quarantine");
        Assert.True(Directory.Exists(quarantine));
        Assert.NotEmpty(Directory.EnumerateFiles(quarantine, "*", SearchOption.AllDirectories));
        using var restored = new TransactionalStateStore(
            stateRoot,
            [
                .. KernelStateParticipants.BuiltIn,
                new StateParticipantDescriptor("retired.extension", "1.0.0", "retired", 1)
            ]);
        Assert.Null(await restored.ReadAsync(
            "retired.extension", "snapshot-v1", CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_interrupted_restore_commit_is_completed_before_the_next_restore(
        bool journalNamesTheAbsolutePath)
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var sourceStore = new InMemoryPackageStore(source.Path);
        await sourceStore.AddAsync(
            TestPackageBuilder.Create("Interrupted.Package", "1.0.0").Build());
        var backupPath = Path.Combine(source.Path, "backup.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);

        // A crash between the commit journal and the directory moves must roll forward.
        // The journal may only ever name the sibling directory the commit generated.
        var staging = Path.Combine(
            Path.GetDirectoryName(destination.Path)!,
            $".nuextvault-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(staging, "packages", "interrupted.package"));
        await File.WriteAllTextAsync(
            Path.Combine(staging, "packages", "interrupted.package", "marker.txt"),
            "staged");
        await File.WriteAllTextAsync(
            Path.Combine(destination.Path, StorageBackup.RestoreJournalName),
            JsonSerializer.Serialize(new
            {
                Version = 1,
                StagingDirectory = journalNamesTheAbsolutePath
                    ? staging
                    : Path.GetFileName(staging),
                CommittedAt = DateTimeOffset.UtcNow
            }));

        StorageBackup.RecoverInterruptedRestore(destination.Path);

        Assert.True(File.Exists(Path.Combine(
            destination.Path, "packages", "interrupted.package", "marker.txt")));
        Assert.False(File.Exists(
            Path.Combine(destination.Path, StorageBackup.RestoreJournalName)));
        Assert.False(Directory.Exists(staging));
        await Assert.ThrowsAsync<IOException>(
            () => StorageBackup.RestoreAsync(backupPath, destination.Path));
    }

    [Theory]
    [InlineData("absolute-external-directory")]
    [InlineData("generated-name-in-another-parent")]
    [InlineData("relative-escape")]
    [InlineData("tampered-name-in-the-right-parent")]
    public async Task An_interrupted_restore_journal_that_names_a_foreign_directory_is_rejected(
        string shape)
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var sourceStore = new InMemoryPackageStore(source.Path);
        await sourceStore.AddAsync(
            TestPackageBuilder.Create("Guarded.Package", "1.0.0").Build());
        var backupPath = Path.Combine(source.Path, "backup.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);
        var parent = Path.GetDirectoryName(destination.Path)!;
        var grandparent = Path.GetDirectoryName(parent)!;
        var (external, stagingDirectory) = shape switch
        {
            "absolute-external-directory" =>
                Decoy(Path.Combine(parent, $"external-{Guid.NewGuid():N}"), absolute: true),
            "generated-name-in-another-parent" =>
                Decoy(Path.Combine(grandparent, GeneratedStagingName()), absolute: true),
            "relative-escape" =>
                Decoy(Path.Combine(grandparent, GeneratedStagingName()), absolute: false),
            _ => Decoy(
                Path.Combine(parent, ".nuextvault-restore-not-hex"),
                absolute: true)
        };

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(destination.Path, StorageBackup.RestoreJournalName),
                JsonSerializer.Serialize(new
                {
                    Version = 1,
                    StagingDirectory = stagingDirectory,
                    CommittedAt = DateTimeOffset.UtcNow
                }));

            var exception = Assert.Throws<InvalidDataException>(
                () => StorageBackup.RecoverInterruptedRestore(destination.Path));

            Assert.Contains("journal", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                Directory.Exists(external),
                "A crafted journal must never move or delete an external directory.");
            Assert.True(File.Exists(Path.Combine(external, "keep.txt")));
            Assert.True(File.Exists(Path.Combine(external, "packages", "payload.txt")));
            Assert.False(Directory.Exists(Path.Combine(destination.Path, "packages")));
            Assert.True(
                File.Exists(Path.Combine(destination.Path, StorageBackup.RestoreJournalName)),
                "An untrusted journal is left for an operator instead of being applied.");
            await Assert.ThrowsAsync<InvalidDataException>(
                () => StorageBackup.RestoreAsync(backupPath, destination.Path));

            // Recovery is deterministic: once the untrusted journal is gone the same
            // storage directory restores normally.
            File.Delete(Path.Combine(destination.Path, StorageBackup.RestoreJournalName));
            await StorageBackup.RestoreAsync(backupPath, destination.Path);
            Assert.NotNull(await new InMemoryPackageStore(destination.Path)
                .FindAsync("Guarded.Package", "1.0.0"));
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }

        static string GeneratedStagingName() =>
            $".nuextvault-restore-{Guid.NewGuid():N}";

        static (string External, string StagingDirectory) Decoy(string path, bool absolute)
        {
            Directory.CreateDirectory(Path.Combine(path, "packages"));
            File.WriteAllText(Path.Combine(path, "packages", "payload.txt"), "external");
            File.WriteAllText(Path.Combine(path, "keep.txt"), "external");
            return (path, absolute ? path : $"../{Path.GetFileName(path)}");
        }
    }

    [Fact]
    public async Task A_version_one_backup_remains_restorable()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var legacyState = Path.Combine(source.Path, "extension-state", "owner", "snapshot.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyState)!);
        await File.WriteAllTextAsync(legacyState, """{"state":"v1"}""");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        await ReplaceManifestAsync(
            backupPath,
            new StorageBackupManifest(1, manifest.CreatedAt, manifest.Files));

        var restored = await StorageBackup.RestoreAsync(backupPath, destination.Path);

        Assert.Equal(1, restored.Version);
        Assert.Null(restored.Participants);
        Assert.Equal(
            """{"state":"v1"}""",
            await File.ReadAllTextAsync(Path.Combine(
                destination.Path, "extension-state", "owner", "snapshot.json")));
    }

    [Fact]
    public async Task Backup_excludes_the_transactional_state_commit_journal()
    {
        using var source = TemporaryDirectory.Create();
        await WriteStateAsync(source.Path, KernelStateParticipants.BuiltIn, "snapshot-v1", "value");
        var stateRoot = Path.Combine(source.Path, "extension-state");
        var owner = Assert.Single(Directory.EnumerateDirectories(
            Path.Combine(stateRoot, TransactionalStateStore.ActiveDirectoryName)));

        // Reserved control-file names are excluded by the archive walk itself, so they are
        // placed where opening the store neither replays nor removes them. They survive the
        // checkpoint and can only be missing from the archive because the filter dropped them.
        var reserved = new[]
        {
            Path.Combine(owner, TransactionalStateStore.RestoreJournalFileName),
            Path.Combine(
                stateRoot,
                TransactionalStateStore.QuarantineDirectoryName,
                TransactionalStateStore.WriteJournalFileName)
        };
        foreach (var path in reserved)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, """{"Version":1}""");
        }

        var carried = Path.Combine(owner, "carried.txt");
        await File.WriteAllTextAsync(carried, "carried");
        var backupPath = Path.Combine(source.Path, "backup.zip");

        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);

        Assert.All(reserved, path => Assert.True(
            File.Exists(path),
            $"'{path}' must still exist, so its absence from the archive is the filter."));
        Assert.DoesNotContain(
            manifest.Files,
            file => file.Path.EndsWith(".commit", StringComparison.Ordinal));
        Assert.Contains(
            manifest.Files,
            file => file.Path.EndsWith("/carried.txt", StringComparison.Ordinal));
        using var archive = ZipFile.OpenRead(backupPath);
        Assert.DoesNotContain(
            archive.Entries,
            entry => entry.FullName.EndsWith(".commit", StringComparison.Ordinal));
        Assert.Contains(
            archive.Entries,
            entry => entry.FullName.EndsWith("/carried.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restore_rejects_a_crafted_backup_that_carries_a_state_commit_journal()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await WriteStateAsync(source.Path, KernelStateParticipants.BuiltIn, "snapshot-v1", "value");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        var victim = Path.Combine(Path.GetDirectoryName(destination.Path)!, $"victim-{Guid.NewGuid():N}");
        var secret = Path.Combine(Path.GetDirectoryName(destination.Path)!, $"secret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(victim, TransactionalStateStore.ActiveDirectoryName));
        await File.WriteAllTextAsync(
            Path.Combine(victim, TransactionalStateStore.ActiveDirectoryName, "payload.txt"),
            "victim");
        Directory.CreateDirectory(secret);
        await File.WriteAllTextAsync(Path.Combine(secret, "payload.txt"), "secret");
        var journal = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            StagingDirectory = $"../{Path.GetFileName(victim)}",
            TrashDirectory = $"../{Path.GetFileName(secret)}",
            RestoreId = 1L
        });
        var journalPath =
            $"extension-state/{TransactionalStateStore.RestoreJournalFileName}";
        await InjectEntryAsync(backupPath, journalPath, journal);
        await ReplaceManifestAsync(
            backupPath,
            new StorageBackupManifest(
                1,
                manifest.CreatedAt,
                [
                    .. manifest.Files,
                    new StorageBackupFile(
                        journalPath,
                        journal.LongLength,
                        Convert.ToHexStringLower(SHA256.HashData(journal)))
                ]));

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => StorageBackup.RestoreAsync(backupPath, destination.Path));

            Assert.Contains("extension-state", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(
                destination.Path,
                "extension-state",
                TransactionalStateStore.RestoreJournalFileName)));
            Assert.False(Directory.Exists(Path.Combine(destination.Path, "extension-state")));
            Assert.True(File.Exists(Path.Combine(
                victim,
                TransactionalStateStore.ActiveDirectoryName,
                "payload.txt")));
            Assert.True(File.Exists(Path.Combine(secret, "payload.txt")));
        }
        finally
        {
            Directory.Delete(victim, recursive: true);
            Directory.Delete(secret, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backup_capture_never_mutates_persisted_extension_state(bool transactional)
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var stateRoot = Path.Combine(source.Path, "extension-state");
        if (transactional)
        {
            await WriteStateAsync(
                source.Path, KernelStateParticipants.BuiltIn, "snapshot-v1", "value");
        }
        else
        {
            // Storage written before the transactional layout existed. Capturing it must
            // not import, migrate, or rewrite it; the next server start owns that.
            await new ExtensionStateStore(stateRoot).WriteRawAsync(
                BuiltInExtensionIds.Vulnerabilities,
                "snapshot-v1",
                Encoding.UTF8.GetBytes("legacy"),
                CancellationToken.None);
        }

        var before = SnapshotTree(stateRoot);

        var manifest = await StorageBackup.CreateAsync(
            source.Path,
            Path.Combine(destination.Path, "backup.zip"));

        Assert.Equal(before, SnapshotTree(stateRoot));
        Assert.NotNull(manifest.Participants);
        if (transactional)
        {
            Assert.Equal(1, Assert.Single(manifest.Participants!).RecordCount);
        }
        else
        {
            Assert.Empty(manifest.Participants!);
            Assert.Contains(
                manifest.Files,
                file => file.Path.StartsWith("extension-state/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_backup_of_version_one_state_restores_and_is_adopted_on_the_next_open()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await new ExtensionStateStore(Path.Combine(source.Path, "extension-state")).WriteRawAsync(
            BuiltInExtensionIds.Vulnerabilities,
            "snapshot-v1",
            Encoding.UTF8.GetBytes("legacy"),
            CancellationToken.None);
        var backupPath = Path.Combine(source.Path, "backup.zip");
        await StorageBackup.CreateAsync(source.Path, backupPath);
        var target = Path.Combine(destination.Path, "restored");

        var restored = await StorageBackup.RestoreAsync(backupPath, target);

        Assert.Empty(restored.Participants!);
        using var store = new TransactionalStateStore(
            Path.Combine(target, "extension-state"),
            KernelStateParticipants.BuiltIn);
        var record = await store.ReadAsync(
            BuiltInExtensionIds.Vulnerabilities, "snapshot-v1", CancellationToken.None);
        Assert.Equal("legacy", Encoding.UTF8.GetString(record!.Value));
    }

    [Fact]
    public async Task Backup_captures_a_committed_but_unpublished_state_batch()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var stateRoot = Path.Combine(source.Path, "extension-state");
        using (var store = new TransactionalStateStore(stateRoot, KernelStateParticipants.BuiltIn))
        {
            await store.WriteAsync(
                BuiltInExtensionIds.Vulnerabilities,
                "snapshot-v1",
                Encoding.UTF8.GetBytes("first"),
                null,
                CancellationToken.None);
            store.WriteFaultInjector = point =>
            {
                if (point == StateWriteFailPoint.AfterCommitJournal)
                {
                    throw new IOException("Injected publish failure.");
                }
            };

            await Assert.ThrowsAsync<ExtensionStateException>(
                () => store.WriteAsync(
                    BuiltInExtensionIds.Vulnerabilities,
                    "snapshot-v1",
                    Encoding.UTF8.GetBytes("second"),
                    null,
                    CancellationToken.None).AsTask());
        }

        var journal = Path.Combine(stateRoot, TransactionalStateStore.WriteJournalFileName);
        Assert.True(File.Exists(journal), "The interrupted batch must leave its commit journal.");
        var backupPath = Path.Combine(destination.Path, "backup.zip");

        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);

        // Completing a transaction the store already committed is the one mutation a
        // capture performs, so the archive holds the batch that commit made
        // authoritative rather than the state it replaced.
        Assert.False(File.Exists(journal));
        Assert.Equal(1, Assert.Single(manifest.Participants!).RecordCount);
        var target = Path.Combine(destination.Path, "restored");
        await StorageBackup.RestoreAsync(backupPath, target);
        using var restored = new TransactionalStateStore(
            Path.Combine(target, "extension-state"),
            KernelStateParticipants.BuiltIn);
        var record = await restored.ReadAsync(
            BuiltInExtensionIds.Vulnerabilities, "snapshot-v1", CancellationToken.None);
        Assert.Equal("second", Encoding.UTF8.GetString(record!.Value));
    }

    [Fact]
    public async Task Backup_and_restore_never_materialize_a_whole_state_record()
    {
        const int records = 8;
        const int recordBytes = 1024 * 1024;
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var stateRoot = Path.Combine(source.Path, "extension-state");
        using (var store = new TransactionalStateStore(stateRoot, KernelStateParticipants.BuiltIn))
        {
            for (var index = 0; index < records; index++)
            {
                await store.WriteAsync(
                    BuiltInExtensionIds.Vulnerabilities,
                    $"record-{index}",
                    Payload(index, recordBytes),
                    null,
                    CancellationToken.None);
            }
        }

        var backupPath = Path.Combine(destination.Path, "backup.zip");
        var restored = Path.Combine(destination.Path, "restored");
        StorageBackupManifest manifest;
        using (var probe = new StatePayloadProbe())
        {
            manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
            await StorageBackup.RestoreAsync(backupPath, restored);

            Assert.Equal(0L, probe.Count);
        }

        var participant = Assert.Single(manifest.Participants!);
        Assert.Equal(records, participant.RecordCount);
        using var restoredStore = new TransactionalStateStore(
            Path.Combine(restored, "extension-state"),
            KernelStateParticipants.BuiltIn);
        var record = await restoredStore.ReadAsync(
            BuiltInExtensionIds.Vulnerabilities, "record-3", CancellationToken.None);
        Assert.Equal(Payload(3, recordBytes), record!.Value);

        // The streamed manifest hash is the hash of the materialized checkpoint, so a
        // bounded capture and a frozen checkpoint describe the same content.
        using var sourceStore = new TransactionalStateStore(
            stateRoot, KernelStateParticipants.BuiltIn);
        using var checkpoint = await sourceStore.CreateCheckpointAsync(CancellationToken.None);
        var exported = await sourceStore.ExportCheckpointAsync(checkpoint, CancellationToken.None);
        Assert.Equal(Assert.Single(exported.Participants).ComputeIntegrity(), participant.Sha256);
    }

    [Fact]
    public async Task Restore_rejects_staged_state_that_the_manifest_declares_no_participant_for()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await WriteStateAsync(source.Path, KernelStateParticipants.BuiltIn, "snapshot-v1", "value");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        await ReplaceManifestAsync(backupPath, manifest with { Participants = [] });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => StorageBackup.RestoreAsync(backupPath, destination.Path));

        Assert.Contains(
            BuiltInExtensionIds.Vulnerabilities,
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(destination.Path, "extension-state")));
    }

    [Fact]
    public async Task Restore_rejects_undeclared_participant_state_in_a_crafted_archive()
    {
        using var source = TemporaryDirectory.Create();
        using var crafted = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await WriteStateAsync(source.Path, KernelStateParticipants.BuiltIn, "snapshot-v1", "value");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        await WriteStateAsync(
            crafted.Path,
            [new StateParticipantDescriptor("undeclared.extension", "1.0.0", "undeclared", 1)],
            "key",
            "smuggled");
        var owner = Assert.Single(Directory.EnumerateDirectories(Path.Combine(
            crafted.Path,
            "extension-state",
            TransactionalStateStore.ActiveDirectoryName)));

        // The archive carries a complete, internally valid participant tree the manifest
        // never declares. Committing it would make the next server start adopt state no
        // operator ever backed up.
        var files = manifest.Files.ToList();
        foreach (var file in Directory.EnumerateFiles(owner))
        {
            var content = await File.ReadAllBytesAsync(file);
            var path =
                $"extension-state/{TransactionalStateStore.ActiveDirectoryName}/" +
                $"{Path.GetFileName(owner)}/{Path.GetFileName(file)}";
            await InjectEntryAsync(backupPath, path, content);
            files.Add(new StorageBackupFile(
                path,
                content.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(content))));
        }

        await ReplaceManifestAsync(backupPath, manifest with { Files = files });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => StorageBackup.RestoreAsync(backupPath, destination.Path));

        Assert.Contains("undeclared.extension", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(destination.Path, "extension-state")));
    }

    [Fact]
    public async Task Restore_rejects_a_crafted_version_one_mirror_that_smuggles_undeclared_records()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        var stateRoot = Path.Combine(source.Path, "extension-state");

        // A participant tree that declares the vulnerability extension and holds no
        // records at all, so its declared record count and integrity describe nothing.
        using (var store = new TransactionalStateStore(stateRoot, KernelStateParticipants.BuiltIn))
        {
            Assert.NotEmpty(store.Participants);
        }

        var backupPath = Path.Combine(source.Path, "backup.zip");
        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        Assert.Equal(0, Assert.Single(manifest.Participants!).RecordCount);

        // The archive smuggles a valid-looking version 1 mirror record for the declared
        // owner. Committing it would let the next store open adopt a record the typed
        // participant record count and integrity never covered.
        var envelope = ExtensionStateStore.CreateCompatibilityEnvelope(
            "snapshot-v1",
            Encoding.UTF8.GetBytes("smuggled"));
        var path =
            $"extension-state/{OwnerDirectoryName(BuiltInExtensionIds.Vulnerabilities)}/" +
            $"{KeyFileName("snapshot-v1")}.json";
        await InjectEntryAsync(backupPath, path, envelope);
        await ReplaceManifestAsync(
            backupPath,
            manifest with
            {
                Files =
                [
                    .. manifest.Files,
                    new StorageBackupFile(
                        path,
                        envelope.LongLength,
                        Convert.ToHexStringLower(SHA256.HashData(envelope)))
                ]
            });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => StorageBackup.RestoreAsync(backupPath, destination.Path));

        Assert.Contains("version 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(destination.Path, "extension-state")));

        using var restored = new TransactionalStateStore(
            Path.Combine(destination.Path, "extension-state"),
            KernelStateParticipants.BuiltIn);
        Assert.Null(await restored.ReadAsync(
            BuiltInExtensionIds.Vulnerabilities, "snapshot-v1", CancellationToken.None));
    }

    [Fact]
    public async Task Restore_rejects_a_version_one_mirror_that_diverges_from_the_declared_records()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await WriteStateAsync(source.Path, KernelStateParticipants.BuiltIn, "snapshot-v1", "value");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);
        var path =
            $"extension-state/{OwnerDirectoryName(BuiltInExtensionIds.Vulnerabilities)}/" +
            $"{KeyFileName("snapshot-v1")}.json";
        Assert.Contains(manifest.Files, file => file.Path == path);

        // The mirror is no longer the projection of the record the manifest declares, so
        // a downgrade — and any later adoption — would read content nothing validated.
        var envelope = ExtensionStateStore.CreateCompatibilityEnvelope(
            "snapshot-v1",
            Encoding.UTF8.GetBytes("tampered"));
        await ReplaceEntryAsync(backupPath, path, envelope);
        await ReplaceManifestAsync(
            backupPath,
            manifest with
            {
                Files =
                [
                    .. manifest.Files.Where(file => file.Path != path),
                    new StorageBackupFile(
                        path,
                        envelope.LongLength,
                        Convert.ToHexStringLower(SHA256.HashData(envelope)))
                ]
            });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => StorageBackup.RestoreAsync(backupPath, destination.Path));

        Assert.Contains("version 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(destination.Path, "extension-state")));
    }

    [Fact]
    public async Task Restore_rejects_a_version_one_mirror_of_an_owner_the_manifest_never_declares()
    {
        using var source = TemporaryDirectory.Create();
        using var destination = TemporaryDirectory.Create();
        await WriteStateAsync(
            source.Path,
            [new StateParticipantDescriptor("other.extension", "1.0.0", "other", 1)],
            "key",
            "value");
        var backupPath = Path.Combine(source.Path, "backup.zip");
        var manifest = await StorageBackup.CreateAsync(source.Path, backupPath);

        // Version 1 state for an extension this server registers but that the archive's
        // participant set never declares. The next open would adopt it into the
        // authoritative tree without any declared record count or integrity.
        var envelope = ExtensionStateStore.CreateCompatibilityEnvelope(
            "snapshot-v1",
            Encoding.UTF8.GetBytes("smuggled"));
        var path =
            $"extension-state/{OwnerDirectoryName(BuiltInExtensionIds.Vulnerabilities)}/" +
            $"{KeyFileName("snapshot-v1")}.json";
        await InjectEntryAsync(backupPath, path, envelope);
        await ReplaceManifestAsync(
            backupPath,
            manifest with
            {
                Files =
                [
                    .. manifest.Files,
                    new StorageBackupFile(
                        path,
                        envelope.LongLength,
                        Convert.ToHexStringLower(SHA256.HashData(envelope)))
                ]
            });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => StorageBackup.RestoreAsync(
                backupPath,
                destination.Path,
                [
                    .. KernelStateParticipants.BuiltIn,
                    new StateParticipantDescriptor("other.extension", "1.0.0", "other", 1)
                ],
                CancellationToken.None));

        Assert.Contains("version 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(destination.Path, "extension-state")));
    }

    private static string OwnerDirectoryName(string extensionId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(extensionId)));

    private static string KeyFileName(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static byte[] Payload(int index, int length)
    {
        var payload = new byte[length];
        for (var position = 0; position < payload.Length; position++)
        {
            payload[position] = (byte)((position + index) % 251);
        }

        return payload;
    }

    private static IReadOnlyList<string> SnapshotTree(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(file =>
                    $"{Path.GetRelativePath(root, file).Replace('\\', '/')}|" +
                    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))))
                .Order(StringComparer.Ordinal)
        ];
    }

    private static async Task InjectEntryAsync(string backupPath, string path, byte[] content)
    {
        using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Update);
        var entry = archive.CreateEntry(path);
        await using var stream = entry.Open();
        await stream.WriteAsync(content);
    }

    private static async Task ReplaceEntryAsync(string backupPath, string path, byte[] content)
    {
        using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Update);
        archive.GetEntry(path)!.Delete();
        var entry = archive.CreateEntry(path);
        await using var stream = entry.Open();
        await stream.WriteAsync(content);
    }

    private static async Task WriteStateAsync(
        string storageDirectory,
        ImmutableArray<StateParticipantDescriptor> participants,
        string key,
        string value,
        string? ownerId = null)
    {
        using var store = new TransactionalStateStore(
            Path.Combine(storageDirectory, "extension-state"),
            participants);
        await store.WriteAsync(
            ownerId ?? participants[0].ExtensionId,
            key,
            Encoding.UTF8.GetBytes(value),
            null,
            CancellationToken.None);
    }

    private static async Task ReplaceManifestAsync(
        string backupPath,
        StorageBackupManifest manifest)
    {
        using var archive = ZipFile.Open(backupPath, ZipArchiveMode.Update);
        archive.GetEntry("manifest.json")!.Delete();
        var entry = archive.CreateEntry("manifest.json");
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, manifest);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NuExtVault.UnitTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
