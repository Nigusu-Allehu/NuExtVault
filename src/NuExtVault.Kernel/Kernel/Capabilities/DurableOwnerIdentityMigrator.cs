using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NuExtVault.Kernel.Capabilities;

internal sealed record OwnerIdentityMigration(
    string PredecessorId,
    string SuccessorId,
    string AuthorizationDigest)
{
    public OwnerIdentityMigration Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PredecessorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SuccessorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(AuthorizationDigest);
        if (string.Equals(PredecessorId, SuccessorId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Owner identity migration cannot link an identity to itself.");
        }
        if (AuthorizationDigest.Length != 64 ||
            AuthorizationDigest.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "Owner identity migration authorization digest must be SHA-256 hex.");
        }

        return this;
    }
}

internal enum OwnerIdentityMigrationFailPoint
{
    AfterTransactionalState,
    AfterStagedContent,
    AfterPublicationJournal
}

/// <summary>
/// Migrates extension identity across the kernel's durable owner-keyed stores before
/// any of them open. The root journal authorizes mixed identity only while an exact
/// migration is being rolled forward after interruption.
/// </summary>
internal sealed class DurableOwnerIdentityMigrator
{
    internal const string JournalFileName = ".owner-identity-migration.commit";
    private const string StagedContentIndexFileName = "index.json";
    private readonly string _root;
    private readonly ImmutableArray<OwnerIdentityMigration> _migrations;

    public DurableOwnerIdentityMigrator(
        string storageDirectory,
        IEnumerable<OwnerIdentityMigration> migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentNullException.ThrowIfNull(migrations);
        _root = Path.GetFullPath(storageDirectory);
        _migrations =
        [
            .. migrations.Select(migration => migration.Validate())
                .OrderBy(migration => migration.PredecessorId, StringComparer.Ordinal)
        ];
        ValidateDeclarations(_migrations);
    }

    internal Action<OwnerIdentityMigrationFailPoint>? FaultInjector { get; init; }

    public static void Migrate(
        string storageDirectory,
        IEnumerable<OwnerIdentityMigration> migrations) =>
        new DurableOwnerIdentityMigrator(storageDirectory, migrations).Migrate();

    public void Migrate()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        var journalPath = Path.Combine(_root, JournalFileName);
        if (_migrations.IsEmpty)
        {
            if (File.Exists(journalPath) ||
                File.Exists(journalPath + ".tmp") ||
                Directory.Exists(Path.Combine(_root, "extension-state")) &&
                Directory.EnumerateDirectories(
                        Path.Combine(_root, "extension-state"),
                        ".owner-migration-*",
                        SearchOption.AllDirectories)
                    .Any())
            {
                throw new InvalidDataException(
                    "An interrupted owner identity migration cannot resume without the " +
                    "same verified administrator authorization.");
            }
            return;
        }

        var stateRoot = Path.Combine(_root, "extension-state");
        if (Directory.Exists(stateRoot))
        {
            TransactionalStateStore.Recover(stateRoot);
        }

        var journal = ReadJournal(journalPath);
        if (journal is not null)
        {
            EnsureJournalMatches(journal);
        }
        else
        {
            var identities = Inspect();
            foreach (var migration in _migrations)
            {
                var state = identities[migration];
                if (state.Legacy && state.Current)
                {
                    throw Collision(migration);
                }
            }

            var pending = _migrations.Where(migration => identities[migration].Legacy).ToArray();
            if (pending.Length == 0)
            {
                return;
            }
            var convergence = pending
                .GroupBy(migration => migration.SuccessorId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (convergence is not null)
            {
                throw new InvalidDataException(
                    $"Multiple populated durable predecessors converge on successor " +
                    $"'{convergence.Key}'; migration cannot merge them.");
            }

            journal = new MigrationJournal(1, Phase: 0, pending, _migrations);
            WriteAtomic(journalPath, JsonSerializer.SerializeToUtf8Bytes(journal));
        }

        foreach (var migration in journal.Migrations)
        {
            MigrateTransactionalState(migration);
        }
        journal = Checkpoint(journalPath, journal, phase: 1);
        FaultInjector?.Invoke(OwnerIdentityMigrationFailPoint.AfterTransactionalState);

        MigrateOwnerJson(
            Path.Combine(_root, StagedContentStore.DirectoryName, StagedContentIndexFileName),
            journal.Migrations,
            "OwnerId");
        journal = Checkpoint(journalPath, journal, phase: 2);
        FaultInjector?.Invoke(OwnerIdentityMigrationFailPoint.AfterStagedContent);

        MigrateOwnerJson(
            Path.Combine(_root, StagedContentStore.DirectoryName, PublicationJournal.FileName),
            journal.Migrations,
            "OwnerId");
        journal = Checkpoint(journalPath, journal, phase: 3);
        FaultInjector?.Invoke(OwnerIdentityMigrationFailPoint.AfterPublicationJournal);

        var remaining = Inspect();
        foreach (var migration in journal.Migrations)
        {
            if (remaining[migration].Legacy)
            {
                throw new InvalidDataException(
                    $"Durable owner migration from '{migration.PredecessorId}' to " +
                    $"'{migration.SuccessorId}' did not transform every owner-keyed surface.");
            }
        }

        DeleteDurable(journalPath);
    }

    private Dictionary<OwnerIdentityMigration, IdentityPresence> Inspect()
    {
        var result = _migrations.ToDictionary(
            migration => migration,
            _ => new IdentityPresence(),
            EqualityComparer<OwnerIdentityMigration>.Default);
        var stateRoot = Path.Combine(_root, "extension-state");
        if (Directory.Exists(stateRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(
                         stateRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(directory);
                var participantPath = Path.Combine(
                    directory,
                    TransactionalStateStore.ParticipantFileName);
                var persistedOwner = File.Exists(participantPath)
                    ? JsonNode.Parse(File.ReadAllBytes(participantPath))?["ExtensionId"]
                        ?.GetValue<string>()
                    : null;
                foreach (var migration in _migrations)
                {
                    EnsureExactOwnerCasing(persistedOwner, migration);
                    result[migration].Legacy |= string.Equals(
                        name,
                        OwnerDirectoryName(migration.PredecessorId),
                        StringComparison.Ordinal);
                    result[migration].Current |= string.Equals(
                        name,
                        OwnerDirectoryName(migration.SuccessorId),
                        StringComparison.Ordinal);
                }
            }
        }

        InspectOwnerJson(
            Path.Combine(_root, StagedContentStore.DirectoryName, StagedContentIndexFileName),
            result);
        InspectOwnerJson(
            Path.Combine(_root, StagedContentStore.DirectoryName, PublicationJournal.FileName),
            result);
        return result;
    }

    private void InspectOwnerJson(
        string path,
        Dictionary<OwnerIdentityMigration, IdentityPresence> result)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var array = ReadArray(path);
        foreach (var node in array)
        {
            var owner = node?["OwnerId"]?.GetValue<string>()
                ?? throw new InvalidDataException(
                    $"Owner-keyed durable file '{Path.GetFileName(path)}' has no owner identity.");
            foreach (var migration in _migrations)
            {
                EnsureExactOwnerCasing(owner, migration);
                result[migration].Legacy |= string.Equals(
                    owner,
                    migration.PredecessorId,
                    StringComparison.Ordinal);
                result[migration].Current |= string.Equals(
                    owner,
                    migration.SuccessorId,
                    StringComparison.Ordinal);
            }
        }
    }

    private static void EnsureExactOwnerCasing(
        string? persistedOwner,
        OwnerIdentityMigration migration)
    {
        if (persistedOwner is null)
        {
            return;
        }

        var matchesPredecessor = string.Equals(
            persistedOwner,
            migration.PredecessorId,
            StringComparison.OrdinalIgnoreCase);
        var matchesSuccessor = string.Equals(
            persistedOwner,
            migration.SuccessorId,
            StringComparison.OrdinalIgnoreCase);
        if ((matchesPredecessor &&
             !string.Equals(
                 persistedOwner,
                 migration.PredecessorId,
                 StringComparison.Ordinal)) ||
            (matchesSuccessor &&
             !string.Equals(
                 persistedOwner,
                 migration.SuccessorId,
                 StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Persisted durable owner '{persistedOwner}' has different casing from the " +
                "authorized owner identity; migration cannot choose an identity spelling.");
        }
    }

    private void MigrateTransactionalState(OwnerIdentityMigration migration)
    {
        var stateRoot = Path.Combine(_root, "extension-state");
        if (!Directory.Exists(stateRoot))
        {
            return;
        }

        var oldName = OwnerDirectoryName(migration.PredecessorId);
        var newName = OwnerDirectoryName(migration.SuccessorId);
        var directories = Directory.EnumerateDirectories(
                stateRoot,
                "*",
                SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), oldName, StringComparison.Ordinal))
            .OrderByDescending(path => path.Length)
            .ToArray();
        foreach (var oldPath in directories)
        {
            var parent = Path.GetDirectoryName(oldPath)!;
            var newPath = Path.Combine(parent, newName);
            var staging = Path.Combine(parent, $".owner-migration-{newName}");
            if (Directory.Exists(newPath))
            {
                throw Collision(migration);
            }

            if (Directory.Exists(staging))
            {
                throw new InvalidDataException(
                    $"Durable owner migration staging path '{staging}' already exists.");
            }

            MoveDirectoryDurable(oldPath, staging);
            RewriteParticipant(staging, migration);
            MoveDirectoryDurable(staging, newPath);
        }

        // Resume a crash after the predecessor directory moved but before publication.
        foreach (var staging in Directory.EnumerateDirectories(
                     stateRoot,
                     $".owner-migration-{newName}",
                     SearchOption.AllDirectories)
                 .OrderByDescending(path => path.Length)
                 .ToArray())
        {
            var newPath = Path.Combine(Path.GetDirectoryName(staging)!, newName);
            if (Directory.Exists(newPath))
            {
                throw Collision(migration);
            }

            RewriteParticipant(staging, migration);
            MoveDirectoryDurable(staging, newPath);
        }
    }

    private static void RewriteParticipant(string directory, OwnerIdentityMigration migration)
    {
        var path = Path.Combine(directory, TransactionalStateStore.ParticipantFileName);
        if (!File.Exists(path))
        {
            return;
        }

        var node = JsonNode.Parse(File.ReadAllBytes(path))?.AsObject()
            ?? throw new InvalidDataException("Extension state participant descriptor is empty.");
        var owner = node["ExtensionId"]?.GetValue<string>()
            ?? throw new InvalidDataException(
                "Extension state participant descriptor has no extension identity.");
        if (!string.Equals(owner, migration.PredecessorId, StringComparison.Ordinal) &&
            !string.Equals(owner, migration.SuccessorId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Extension state participant '{owner}' is stored under another owner's directory.");
        }

        node["ExtensionId"] = migration.SuccessorId;
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(node));
    }

    private static void MigrateOwnerJson(
        string path,
        IReadOnlyList<OwnerIdentityMigration> migrations,
        string ownerProperty)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var array = ReadArray(path);
        var changed = false;
        foreach (var migration in migrations)
        {
            var legacy = array.Where(node => string.Equals(
                    node?[ownerProperty]?.GetValue<string>(),
                    migration.PredecessorId,
                    StringComparison.Ordinal))
                .ToArray();
            var current = array.Any(node => string.Equals(
                node?[ownerProperty]?.GetValue<string>(),
                migration.SuccessorId,
                StringComparison.Ordinal));
            if (legacy.Length > 0 && current)
            {
                throw Collision(migration);
            }

            foreach (var node in legacy)
            {
                node![ownerProperty] = migration.SuccessorId;
                changed = true;
            }
        }

        if (changed)
        {
            WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(array));
        }
    }

    private static JsonArray ReadArray(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllBytes(path))?.AsArray()
                ?? throw new InvalidDataException(
                    $"Owner-keyed durable file '{Path.GetFileName(path)}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Owner-keyed durable file '{Path.GetFileName(path)}' is unreadable.",
                exception);
        }
    }

    private MigrationJournal? ReadJournal(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<MigrationJournal>(File.ReadAllBytes(path))
                ?? throw new InvalidDataException("The owner identity migration journal is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The owner identity migration journal is unreadable; migration cannot continue.",
                exception);
        }
    }

    private void EnsureJournalMatches(MigrationJournal journal)
    {
        if (journal.Version != 1 ||
            journal.Phase is < 0 or > 3 ||
        journal.Migrations is null ||
        journal.AuthorizedMigrations is null ||
        journal.Migrations.Count == 0 ||
        journal.Migrations.Any(migration => !_migrations.Contains(migration)) ||
        journal.Migrations.Distinct().Count() != journal.Migrations.Count ||
        !journal.AuthorizedMigrations.SequenceEqual(_migrations))
        {
            throw new InvalidDataException(
                "The owner identity migration journal does not match the verified active " +
                "extension manifests.");
        }
    }

    private static MigrationJournal Checkpoint(
        string path,
        MigrationJournal journal,
        int phase)
    {
        if (journal.Phase >= phase)
        {
            return journal;
        }

        var checkpoint = journal with { Phase = phase };
        WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(checkpoint));
        return checkpoint;
    }

    internal static void ValidateDeclarations(
        IEnumerable<OwnerIdentityMigration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        var migrations = declarations.Select(migration => migration.Validate()).ToImmutableArray();
        var predecessors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var successors = migrations
            .Select(migration => migration.SuccessorId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var migration in migrations)
        {
            if (!predecessors.Add(migration.PredecessorId))
            {
                throw new ArgumentException(
                    $"Owner identity predecessor '{migration.PredecessorId}' has multiple successors.");
            }

            if (successors.Contains(migration.PredecessorId))
            {
                throw new ArgumentException(
                    $"Owner identity migration chain through '{migration.PredecessorId}' is ambiguous.");
            }
        }
    }

    private static InvalidDataException Collision(OwnerIdentityMigration migration) =>
        new(
            $"Durable storage contains both predecessor '{migration.PredecessorId}' and " +
            $"successor '{migration.SuccessorId}' owner data. Refusing to merge or choose; " +
            "restore one coherent backup or remove the unintended storage root.");

    private static string OwnerDirectoryName(string ownerId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId)));

    private static void WriteAtomic(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(
                   temporary,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None,
                   4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        MoveFileDurable(temporary, path);
    }

    private static void MoveDirectoryDurable(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            MoveWindows(source, destination, replaceExisting: false);
            return;
        }

        Directory.Move(source, destination);
        FlushDirectory(Path.GetDirectoryName(destination)!);
    }

    private static void MoveFileDurable(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            MoveWindows(source, destination, replaceExisting: true);
            return;
        }

        File.Move(source, destination, overwrite: true);
        FlushDirectory(Path.GetDirectoryName(destination)!);
    }

    private static void DeleteDurable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var completed = path + ".completed";
            MoveWindows(path, completed, replaceExisting: true);
            File.Delete(completed);
            return;
        }

        File.Delete(path);
        FlushDirectory(Path.GetDirectoryName(path)!);
    }

    private static void MoveWindows(
        string source,
        string destination,
        bool replaceExisting)
    {
        const uint moveFileReplaceExisting = 0x1;
        const uint moveFileWriteThrough = 0x8;
        var flags = moveFileWriteThrough |
                    (replaceExisting ? moveFileReplaceExisting : 0);
        if (!MoveFileEx(source, destination, flags))
        {
            throw new IOException(
                $"Could not durably move '{source}' to '{destination}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    private static void FlushDirectory(string path)
    {
        var descriptor = Open(path, 0);
        if (descriptor < 0)
        {
            throw new IOException(
                $"Could not open directory '{path}' for a durability checkpoint.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new IOException(
                    $"Could not durably checkpoint directory '{path}'.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    private sealed record MigrationJournal(
        int Version,
        int Phase,
        IReadOnlyList<OwnerIdentityMigration> Migrations,
        IReadOnlyList<OwnerIdentityMigration> AuthorizedMigrations);

    private sealed class IdentityPresence
    {
        public bool Legacy { get; set; }

        public bool Current { get; set; }
    }
}
