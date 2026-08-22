using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Operations;

public static class StorageBackup
{
    private const string ManifestEntryName = "manifest.json";
    private const int CurrentManifestVersion = 2;
    private const long MaximumManifestBytes = 10 * 1024 * 1024;
    private const long RestoreFreeSpaceReserveBytes = 256 * 1024 * 1024;
    internal const string ExtensionStateDirectoryName = "extension-state";
    internal const string RestoreJournalName = ".restore.commit";
    private const string RestoreStagingPrefix = ".nuget-test-server-restore-";
    private const int RestoreStagingSuffixLength = 32;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private static readonly string[] ReservedExtensionStateFileNames =
    [
        TransactionalStateStore.RestoreJournalFileName,
        TransactionalStateStore.WriteJournalFileName
    ];
    private static readonly string[] IncludedDirectories =
        ["packages", "security", "trash", "vulnerabilities", ExtensionStateDirectoryName];
    private static readonly string[] IncludedFiles =
    [
        "packages.db",
        "packages.db-shm",
        "packages.db-wal",
        "supply-chain.db",
        "supply-chain.db-shm",
        "supply-chain.db-wal"
    ];

    public static async Task<StorageBackupManifest> CreateAsync(
        string storageDirectory,
        string backupPath,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var root = Path.GetFullPath(storageDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Storage directory '{root}' does not exist.");
        }

        var destination = Path.GetFullPath(backupPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            throw new IOException($"Backup '{destination}' already exists.");
        }

        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";

        // The exclusive storage lease is the offline checkpoint boundary: package,
        // publication, and extension state are captured while no writer can mutate them.
        using var storageLease = AcquireStorageLease(root);
        try
        {
            var (participants, checkpointId) = await CaptureStateCheckpointAsync(root, token);
            var files = new List<StorageBackupFile>();
            await using (var stream = File.Create(temporary))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var directoryName in IncludedDirectories)
                {
                    var directory = Path.Combine(root, directoryName);
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(
                                 directory,
                                 "*",
                                 SearchOption.AllDirectories))
                    {
                        token.ThrowIfCancellationRequested();
                        var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                        if (IsTransient(relativePath) ||
                            IsReservedExtensionStateFile(relativePath))
                        {
                            continue;
                        }

                        await AddEntryAsync(archive, relativePath, file, files, token);
                    }
                }

                foreach (var fileName in IncludedFiles)
                {
                    var file = Path.Combine(root, fileName);
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    await AddEntryAsync(archive, fileName, file, files, token);
                }

                var manifest = new StorageBackupManifest(
                    CurrentManifestVersion,
                    DateTimeOffset.UtcNow,
                    files,
                    participants,
                    checkpointId);
                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, cancellationToken: token);
            }

            File.Move(temporary, destination);
            return await ReadManifestAsync(destination, token);
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    public static Task<StorageBackupManifest> RestoreAsync(
        string backupPath,
        string storageDirectory,
        CancellationToken token = default) =>
        RestoreAsync(backupPath, storageDirectory, KernelStateParticipants.BuiltIn, token);

    internal static async Task<StorageBackupManifest> RestoreAsync(
        string backupPath,
        string storageDirectory,
        ImmutableArray<StateParticipantDescriptor> expectedParticipants,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        var source = Path.GetFullPath(backupPath);
        var destination = Path.GetFullPath(storageDirectory);
        Directory.CreateDirectory(destination);
        using var storageLease = AcquireStorageLease(destination);
        RecoverInterruptedRestore(destination);
        EnsureRestoreTargetIsClean(destination);
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Storage directory must have a parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(
            parent,
            $"{RestoreStagingPrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var journalPath = Path.Combine(destination, RestoreJournalName);

        try
        {
            using var archive = ZipFile.OpenRead(source);
            var entries = archive.Entries.ToDictionary(
                entry => entry.FullName,
                StringComparer.Ordinal);
            if (!entries.TryGetValue(ManifestEntryName, out var manifestEntry))
            {
                throw new InvalidDataException("Backup has no integrity manifest.");
            }

            if (manifestEntry.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("Backup integrity manifest is too large.");
            }

            StorageBackupManifest manifest;
            await using (var stream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<StorageBackupManifest>(
                    stream,
                    cancellationToken: token)
                    ?? throw new InvalidDataException("Backup integrity manifest is empty.");
            }

            if (manifest.Version is < 1 or > CurrentManifestVersion)
            {
                throw new InvalidDataException(
                    $"Backup manifest version '{manifest.Version}' is not supported.");
            }

            if (manifest.Files is null)
            {
                throw new InvalidDataException("Backup integrity manifest has no file list.");
            }

            var quarantined = ValidateParticipants(manifest, expectedParticipants);
            EnsureFreeSpace(manifest, parent);
            foreach (var file in manifest.Files)
            {
                token.ThrowIfCancellationRequested();
                await StageFileAsync(staging, entries, file, token);
            }

            await ValidateStagedParticipantStateAsync(
                staging,
                manifest,
                expectedParticipants,
                quarantined,
                token);
            Commit(destination, staging);
            return manifest;
        }
        catch
        {
            if (!File.Exists(journalPath))
            {
                RemoveRestoredContent(destination);
            }

            throw;
        }
        finally
        {
            if (!File.Exists(journalPath) && Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    /// <summary>
    /// Completes a restore whose commit was interrupted. The journal is the single commit
    /// point, so a journal that survives a crash always rolls forward. A journal only ever
    /// names the sibling staging directory the commit generated, so a journal that was
    /// tampered with can never direct a move or a recursive delete outside that directory.
    /// </summary>
    internal static void RecoverInterruptedRestore(string storageDirectory)
    {
        var destination = Path.GetFullPath(storageDirectory);
        var journalPath = Path.Combine(destination, RestoreJournalName);
        if (!File.Exists(journalPath))
        {
            return;
        }

        RestoreCommitJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<RestoreCommitJournal>(
                File.ReadAllBytes(journalPath));
        }
        catch (JsonException)
        {
            // A journal that is not complete JSON was torn by a crash before the commit
            // point, so the restore it describes never became authoritative.
            journal = null;
        }

        if (journal is null)
        {
            File.Delete(journalPath);
            return;
        }

        var staging = ResolveStagingDirectory(destination, journal);
        ApplyCommit(destination, staging);
        File.Delete(journalPath);
    }

    /// <summary>
    /// Resolves the staging directory a journal names, accepting only the exact sibling
    /// directory shape a commit generates. An untrusted journal is left in place for an
    /// operator instead of being applied, so recovery never touches an external path.
    /// </summary>
    private static string ResolveStagingDirectory(string destination, RestoreCommitJournal journal)
    {
        var parent = Path.GetDirectoryName(destination);
        string? resolved = null;
        if (journal is { Version: 1, StagingDirectory.Length: > 0 } &&
            !string.IsNullOrEmpty(parent))
        {
            try
            {
                resolved = Path.GetFullPath(Path.Combine(parent, journal.StagingDirectory));
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                resolved = null;
            }
        }

        if (resolved is null ||
            !IsGeneratedStagingName(Path.GetFileName(resolved)) ||
            !string.Equals(Path.GetDirectoryName(resolved), parent, PathComparison))
        {
            throw new InvalidDataException(
                $"The restore commit journal in '{destination}' is not a journal this server " +
                "wrote. Remove the untrusted journal before restoring.");
        }

        return resolved;
    }

    private static bool IsGeneratedStagingName(string name) =>
        name.Length == RestoreStagingPrefix.Length + RestoreStagingSuffixLength &&
        name.StartsWith(RestoreStagingPrefix, StringComparison.Ordinal) &&
        name.AsSpan(RestoreStagingPrefix.Length).ToString().All(char.IsAsciiHexDigitLower);

    private static void Commit(string destination, string staging)
    {
        var journalPath = Path.Combine(destination, RestoreJournalName);
        var temporary = Path.Combine(destination, $".{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(
                   temporary,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(JsonSerializer.SerializeToUtf8Bytes(
                new RestoreCommitJournal(
                    1,
                    Path.GetFileName(staging),
                    DateTimeOffset.UtcNow)));
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, journalPath, overwrite: true);
        ApplyCommit(destination, staging);
        File.Delete(journalPath);
    }

    private static void ApplyCommit(string destination, string staging)
    {
        if (Directory.Exists(staging))
        {
            foreach (var directoryName in IncludedDirectories)
            {
                var stagedDirectory = Path.Combine(staging, directoryName);
                var target = Path.Combine(destination, directoryName);
                if (Directory.Exists(stagedDirectory) && !Directory.Exists(target))
                {
                    Directory.Move(stagedDirectory, target);
                }
            }

            foreach (var fileName in IncludedFiles)
            {
                var stagedFile = Path.Combine(staging, fileName);
                var target = Path.Combine(destination, fileName);
                if (File.Exists(stagedFile) && !File.Exists(target))
                {
                    File.Move(stagedFile, target);
                }
            }

            Directory.Delete(staging, recursive: true);
        }
    }

    private static void RemoveRestoredContent(string destination)
    {
        foreach (var directoryName in IncludedDirectories)
        {
            var restoredDirectory = Path.Combine(destination, directoryName);
            if (Directory.Exists(restoredDirectory))
            {
                Directory.Delete(restoredDirectory, recursive: true);
            }
        }

        foreach (var fileName in IncludedFiles)
        {
            var restoredFile = Path.Combine(destination, fileName);
            if (File.Exists(restoredFile))
            {
                File.Delete(restoredFile);
            }
        }
    }

    /// <summary>
    /// Describes the extension state this archive captures without opening it for use.
    /// The exclusive storage lease is the checkpoint boundary, so the committed record
    /// tree is read exactly as it stands: a capture never imports version 1 records,
    /// never migrates a schema, and never rewrites a participant descriptor. Completing a
    /// transaction the store already committed is the one explicit exception, because the
    /// archive has to contain the batch that commit made authoritative. Record identity
    /// and integrity are streamed, so this costs one buffer rather than the size of the
    /// captured state.
    /// </summary>
    private static async Task<(IReadOnlyList<StorageBackupParticipant> Participants, long CheckpointId)>
        CaptureStateCheckpointAsync(string root, CancellationToken token)
    {
        var stateRoot = Path.Combine(root, ExtensionStateDirectoryName);
        if (!Directory.Exists(stateRoot))
        {
            return ([], 0);
        }

        TransactionalStateStore.Recover(stateRoot);
        var summaries = await TransactionalStateStore.SummarizeParticipantSetAsync(
            Path.Combine(stateRoot, TransactionalStateStore.ActiveDirectoryName),
            token);
        EnsureCapturedSchemasAreSupported(summaries);
        return (
            [
                .. summaries.Select(summary => new StorageBackupParticipant(
                    summary.ExtensionId,
                    summary.ExtensionVersion,
                    summary.SchemaName,
                    summary.SchemaVersion,
                    summary.Required,
                    summary.RecordCount,
                    summary.Integrity))
            ],
            summaries.Length == 0 ? 0 : summaries.Max(summary => summary.HighWaterETag));
    }

    /// <summary>
    /// Fails a capture of persisted state this build cannot serve, so an archive never
    /// records a schema the server it came from could not open. State owned by an
    /// extension this build does not provide is captured unchanged; a restore decides
    /// whether it can be activated.
    /// </summary>
    private static void EnsureCapturedSchemasAreSupported(
        ImmutableArray<StateParticipantSummary> summaries)
    {
        foreach (var summary in summaries)
        {
            var match = KernelStateParticipants.BuiltIn.FirstOrDefault(candidate => string.Equals(
                candidate.ExtensionId,
                summary.ExtensionId,
                StringComparison.Ordinal));
            if (match is null)
            {
                continue;
            }

            if (!string.Equals(summary.SchemaName, match.SchemaName, StringComparison.Ordinal))
            {
                throw new StateSchemaCompatibilityException(
                    $"Extension '{summary.ExtensionId}' owns schema '{match.SchemaName}' but the " +
                    $"persisted state declares '{summary.SchemaName}'.");
            }

            if (summary.SchemaVersion > match.SchemaVersion)
            {
                throw new StateSchemaCompatibilityException(
                    $"Extension '{summary.ExtensionId}' persisted schema version " +
                    $"{summary.SchemaVersion} is newer than the supported version " +
                    $"{match.SchemaVersion}.");
            }
        }
    }

    private static IReadOnlyList<string> ValidateParticipants(
        StorageBackupManifest manifest,
        ImmutableArray<StateParticipantDescriptor> expected)
    {
        if (manifest.Participants is null)
        {
            // Version 1 backups predate typed participants. Their extension state restores
            // as files and is adopted when the server next opens the transactional store.
            return [];
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var participant in manifest.Participants)
        {
            if (string.IsNullOrWhiteSpace(participant.ExtensionId) ||
                !declared.Add(participant.ExtensionId) ||
                participant.RecordCount < 0)
            {
                throw new InvalidDataException(
                    "Backup integrity manifest declares an invalid or repeated extension state " +
                    "participant.");
            }
        }

        var quarantined = new List<string>();
        foreach (var participant in manifest.Participants)
        {
            var match = expected.FirstOrDefault(candidate => string.Equals(
                candidate.ExtensionId,
                participant.ExtensionId,
                StringComparison.Ordinal));
            if (match is null)
            {
                if (participant.Required)
                {
                    throw new InvalidDataException(
                        $"Backup requires extension '{participant.ExtensionId}' which this " +
                        "server does not provide.");
                }

                quarantined.Add(participant.ExtensionId);
                continue;
            }

            if (!string.Equals(participant.SchemaName, match.SchemaName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Backup extension '{participant.ExtensionId}' declares schema " +
                    $"'{participant.SchemaName}' but this server owns '{match.SchemaName}'.");
            }

            if (participant.SchemaVersion > match.SchemaVersion)
            {
                throw new InvalidDataException(
                    $"Backup extension '{participant.ExtensionId}' schema version " +
                    $"{participant.SchemaVersion} is newer than the supported version " +
                    $"{match.SchemaVersion}.");
            }

            if (match.ResolveMigrationPath(participant.SchemaVersion) is null)
            {
                throw new InvalidDataException(
                    $"Backup extension '{participant.ExtensionId}' has no complete migration " +
                    $"path from schema version {participant.SchemaVersion} to " +
                    $"{match.SchemaVersion}.");
            }
        }

        foreach (var required in expected.Where(participant => participant.Required))
        {
            if (!manifest.Participants.Any(participant => string.Equals(
                    participant.ExtensionId,
                    required.ExtensionId,
                    StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"Backup is missing required extension state for '{required.ExtensionId}'.");
            }
        }

        return quarantined;
    }

    /// <summary>
    /// Confirms the staged records reproduce every declared participant, and only the
    /// declared participants, before any of them can become authoritative, and moves state
    /// for inactive extensions aside. Version 2 archives are checked in both directions, so
    /// an archive can neither hide a declared participant nor deliver a participant tree the
    /// manifest never declared and that the next server start would have to import,
    /// migrate, or quarantine. The version 1 mirror an archive carries beside that tree is
    /// held to the same standard, because the next server start adopts mirror-only records
    /// of a registered owner into the authoritative tree. Records are streamed, so
    /// validating a large archive costs one buffer rather than its whole record set.
    /// </summary>
    private static async Task ValidateStagedParticipantStateAsync(
        string staging,
        StorageBackupManifest manifest,
        ImmutableArray<StateParticipantDescriptor> expected,
        IReadOnlyList<string> quarantined,
        CancellationToken token)
    {
        if (manifest.Participants is null)
        {
            // Version 1 backups predate typed participants and carry no participant tree.
            return;
        }

        var stateRoot = Path.Combine(staging, ExtensionStateDirectoryName);
        var active = Path.Combine(stateRoot, TransactionalStateStore.ActiveDirectoryName);
        ImmutableArray<StateParticipantSummary> staged;
        try
        {
            staged = await TransactionalStateStore.SummarizeParticipantSetAsync(active, token);
        }
        catch (Exception exception) when (
            exception is ExtensionStateException or StateQuotaExceededException)
        {
            throw new InvalidDataException(
                "Backup extension state could not be read or validated.",
                exception);
        }

        foreach (var participant in manifest.Participants)
        {
            var match = staged.FirstOrDefault(candidate => string.Equals(
                candidate.ExtensionId,
                participant.ExtensionId,
                StringComparison.Ordinal));
            if (match is null)
            {
                throw new InvalidDataException(
                    $"Backup extension state for '{participant.ExtensionId}' is missing from " +
                    "the archive.");
            }

            if (match.RecordCount != participant.RecordCount ||
                !string.Equals(match.Integrity, participant.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Backup extension state for '{participant.ExtensionId}' failed integrity " +
                    "validation.");
            }
        }

        var declared = manifest.Participants
            .Select(participant => participant.ExtensionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var summary in staged)
        {
            if (!declared.Contains(summary.ExtensionId))
            {
                throw new InvalidDataException(
                    $"Backup carries extension state for '{summary.ExtensionId}' that its " +
                    "integrity manifest does not declare.");
            }
        }

        try
        {
            TransactionalStateStore.ValidateCompatibilityMirror(
                stateRoot,
                declared,
                expected.Select(participant => participant.ExtensionId));
        }
        catch (Exception exception) when (
            exception is ExtensionStateException or StateQuotaExceededException)
        {
            throw new InvalidDataException(
                "Backup version 1 extension state is not a projection of the participant " +
                "state its integrity manifest declares.",
                exception);
        }

        foreach (var extensionId in quarantined)
        {
            var owner = Path.Combine(active, OwnerDirectoryName(extensionId));
            if (Directory.Exists(owner))
            {
                var target = Path.Combine(
                    stateRoot,
                    TransactionalStateStore.QuarantineDirectoryName,
                    OwnerDirectoryName(extensionId));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(owner, target);
            }

            // The version 1 mirror is a rebuildable projection of the authoritative record
            // tree, so quarantining the records also retires the mirror.
            var mirror = Path.Combine(stateRoot, OwnerDirectoryName(extensionId));
            if (Directory.Exists(mirror))
            {
                Directory.Delete(mirror, recursive: true);
            }
        }
    }

    private static void EnsureFreeSpace(StorageBackupManifest manifest, string parent)
    {
        long requiredBytes = 0;
        try
        {
            foreach (var file in manifest.Files)
            {
                requiredBytes = checked(requiredBytes + file.Length);
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Backup integrity manifest contains an invalid total length.",
                exception);
        }

        var rootPath = Path.GetPathRoot(parent);
        if (string.IsNullOrEmpty(rootPath))
        {
            return;
        }

        var availableBytes = new DriveInfo(rootPath).AvailableFreeSpace;
        if (requiredBytes > Math.Max(0, availableBytes - RestoreFreeSpaceReserveBytes))
        {
            throw new IOException(
                "The restore volume does not have enough free space for the backup.");
        }
    }

    private static async Task StageFileAsync(
        string staging,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        StorageBackupFile file,
        CancellationToken token)
    {
        ValidateRelativePath(file.Path);
        if (!entries.TryGetValue(file.Path, out var entry))
        {
            throw new InvalidDataException(
                $"Backup file '{file.Path}' is missing from the archive.");
        }

        if (file.Length < 0 || entry.Length != file.Length)
        {
            throw new InvalidDataException(
                $"Backup file '{file.Path}' failed integrity validation.");
        }

        var target = Path.GetFullPath(
            Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(
                staging + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Backup path '{file.Path}' is unsafe.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = entry.Open();
        await using var output = File.Create(target);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            if (length + read > file.Length)
            {
                throw new InvalidDataException(
                    $"Backup file '{file.Path}' failed integrity validation.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), token);
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        var actualHash = hash.GetHashAndReset();
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(file.Sha256);
        }
        catch (Exception exception) when (
            exception is FormatException or ArgumentNullException)
        {
            throw new InvalidDataException(
                $"Backup integrity hash for '{file.Path}' is invalid.",
                exception);
        }

        if (length != file.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new InvalidDataException(
                $"Backup file '{file.Path}' failed integrity validation.");
        }
    }

    private static string OwnerDirectoryName(string extensionId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(extensionId)));

    private static async Task<StorageBackupManifest> ReadManifestAsync(
        string backupPath,
        CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(backupPath);
        var entry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("Backup has no integrity manifest.");
        await using var stream = entry.Open();
        return await JsonSerializer.DeserializeAsync<StorageBackupManifest>(
            stream,
            cancellationToken: token)
            ?? throw new InvalidDataException("Backup integrity manifest is empty.");
    }

    private static void EnsureRestoreTargetIsClean(string destination)
    {
        foreach (var directoryName in IncludedDirectories)
        {
            if (Directory.Exists(Path.Combine(destination, directoryName)))
            {
                throw new IOException(
                    $"Restore target '{destination}' already contains '{directoryName}'.");
            }
        }

        foreach (var fileName in IncludedFiles)
        {
            if (File.Exists(Path.Combine(destination, fileName)))
            {
                throw new IOException(
                    $"Restore target '{destination}' already contains '{fileName}'.");
            }
        }
    }

    private static bool IsTransient(string relativePath) =>
        relativePath.Split('/').Any(segment => segment.StartsWith('.'));

    /// <summary>
    /// The transactional state store's commit journals name directories the store generated
    /// and are replayed the moment the store is opened. They are never part of a consistent
    /// offline archive, so an archive can neither produce nor deliver one.
    /// </summary>
    private static bool IsReservedExtensionStateFile(string relativePath)
    {
        var segments = relativePath.Split('/');
        return segments.Length > 1 &&
            string.Equals(segments[0], ExtensionStateDirectoryName, StringComparison.Ordinal) &&
            ReservedExtensionStateFileNames.Contains(segments[^1], StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Path.IsPathFullyQualified(path) ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Split('/').Any(segment => segment is "" or "." or "..") ||
            !IncludedDirectories.Any(
                directory => path.StartsWith(directory + "/", StringComparison.Ordinal)) &&
            !IncludedFiles.Contains(path, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"Backup path '{path}' is invalid.");
        }

        if (IsReservedExtensionStateFile(path))
        {
            throw new InvalidDataException(
                $"Backup path '{path}' is a reserved extension-state commit journal and " +
                "cannot be restored.");
        }
    }

    private static async Task AddEntryAsync(
        ZipArchive archive,
        string relativePath,
        string file,
        List<StorageBackupFile> files,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
        await using var input = File.OpenRead(file);
        await using var output = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            hash.AppendData(buffer, 0, read);
            length += read;
        }

        files.Add(new StorageBackupFile(
            relativePath,
            length,
            Convert.ToHexStringLower(hash.GetHashAndReset())));
    }

    private static FileStream AcquireStorageLease(string root)
    {
        var lockPath = Path.Combine(root, ".storage.lock");
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception)
        {
            throw new StorageCheckpointUnavailableException(
                $"Storage directory '{root}' is in use. Stop the server before creating a backup.",
                exception);
        }
    }

    /// <summary>
    /// The single restore commit point. <c>StagingDirectory</c> is the generated name of the
    /// staging directory beside the storage directory, never an arbitrary path.
    /// </summary>
    private sealed record RestoreCommitJournal(
        int Version,
        string StagingDirectory,
        DateTimeOffset CommittedAt);
}

/// <summary>
/// Reports that one consistent offline checkpoint could not be established because a live
/// server is mutating the storage directory.
/// </summary>
public sealed class StorageCheckpointUnavailableException(string message, Exception innerException)
    : IOException(message, innerException);

public sealed record StorageBackupManifest(
    int Version,
    DateTimeOffset CreatedAt,
    IReadOnlyList<StorageBackupFile> Files,
    IReadOnlyList<StorageBackupParticipant>? Participants = null,
    long? CheckpointId = null);

public sealed record StorageBackupFile(string Path, long Length, string Sha256);

public sealed record StorageBackupParticipant(
    string ExtensionId,
    string ExtensionVersion,
    string SchemaName,
    int SchemaVersion,
    bool Required,
    int RecordCount,
    string Sha256);
