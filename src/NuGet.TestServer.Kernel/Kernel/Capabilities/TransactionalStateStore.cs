using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Kernel.Capabilities;

/// <summary>
/// The kernel-owned transactional store for authoritative extension state. It provides
/// monotonic concurrency tokens that survive restart, namespaced schema identity with
/// ordered migrations, bounded quotas, bounded lock cardinality, frozen checkpoints, and a
/// staged restore with a single journalled commit point.
/// </summary>
internal sealed class TransactionalStateStore : IDisposable
{
    internal const int LockStripeCount = 64;
    internal const int LegacySchemaVersion = 1;
    internal const string ActiveDirectoryName = "v2";
    internal const string RecordExtension = ".rec";
    internal const string ParticipantFileName = "participant.json";
    internal const string SequenceFileName = "sequence.json";
    internal const string RestoreJournalFileName = "restore.commit";
    internal const string WriteJournalFileName = "write.commit";
    internal const string StagingPrefix = ".staging-";
    internal const string TrashPrefix = ".trash-";
    internal const string CheckpointPrefix = ".checkpoint-";
    internal const string QuarantineDirectoryName = "quarantine";

    private const string RecordMagic = "nts-state/2";
    private const int MaximumHeaderBytes = 8 * 1024;
    private const long SequenceReservation = 256;
    private static readonly TimeSpan DefaultCheckpointLease = TimeSpan.FromMinutes(10);

    private readonly string? _root;
    private readonly StateStoreQuotas _quotas;
    private readonly TimeProvider _clock;
    private readonly ExtensionStateStore _compatibility;
    private readonly ImmutableDictionary<string, StateParticipantDescriptor> _participants;
    private readonly SemaphoreSlim[] _stripes =
        [.. Enumerable.Range(0, LockStripeCount).Select(_ => new SemaphoreSlim(1, 1))];
    private readonly SemaphoreSlim _restoreGate = new(1, 1);
    private readonly SemaphoreSlim _commitGate = new(1, 1);
    private readonly ConcurrentDictionary<StateCheckpoint, byte> _checkpoints = new();
    private readonly object _sequenceGate = new();
    private long _sequenceNext;
    private long _sequenceCeiling;
    private long _checkpointNext;
    private long _checkpointCeiling;
    private int _publishFailed;
    private ImmutableDictionary<string, OwnerRecords> _owners =
        ImmutableDictionary<string, OwnerRecords>.Empty.WithComparers(StringComparer.Ordinal);

    public TransactionalStateStore(
        string? root,
        IEnumerable<StateParticipantDescriptor>? participants = null,
        StateStoreQuotas? quotas = null,
        ImmutableDictionary<
            string,
            ImmutableDictionary<string, LegacyStateFileSetRegistration>>? legacyFileSets = null,
        TimeProvider? timeProvider = null)
    {
        _root = root is null ? null : Path.GetFullPath(root);
        _quotas = (quotas ?? new StateStoreQuotas()).Validate();
        _clock = timeProvider ?? TimeProvider.System;
        _compatibility = new ExtensionStateStore(_root, legacyFileSets);
        var builder = ImmutableDictionary.CreateBuilder<string, StateParticipantDescriptor>(
            StringComparer.Ordinal);
        foreach (var participant in participants ?? [])
        {
            participant.Validate();
            if (builder.ContainsKey(participant.ExtensionId))
            {
                throw new ArgumentException(
                    $"Extension '{participant.ExtensionId}' declares more than one state schema.",
                    nameof(participants));
            }

            builder.Add(participant.ExtensionId, participant);
        }

        _participants = builder.ToImmutable();
        if (_root is null)
        {
            return;
        }

        Directory.CreateDirectory(_root);
        Recover(_root);
        _owners = LoadIndex();
        LoadSequence();

        // Opening the store is bounded by participant descriptors and record headers. A
        // migration republishes the whole active tree and rebuilds the version 1 mirror
        // from it, so records that only exist in the mirror are adopted first and then
        // travel through the migration with every other record. Record payloads are
        // materialized only when a migration actually has to rewrite them.
        var persisted = ReadPersistedParticipants(_root);
        var migrate = EnsurePersistedSchemasAreSupported(persisted);
        migrate |= ImportCompatibilityRecords(persisted);
        if (migrate)
        {
            MigratePersistedSchemas();
        }

        WriteParticipantDescriptors();
    }

    internal int LockCount => _stripes.Length;

    /// <summary>
    /// A deterministic fault seam used by tests to interrupt a durable batch at a named
    /// point. It is never set by the server.
    /// </summary>
    internal Action<StateWriteFailPoint>? WriteFaultInjector { get; set; }

    internal ImmutableArray<StateParticipantDescriptor> Participants =>
        [.. _participants.Values.OrderBy(
            participant => participant.ExtensionId,
            StringComparer.Ordinal)];

    public ValueTask<ExtensionStateFileSet?> ReadLegacyFileSetAsync(
        string ownerId,
        string logicalName,
        CancellationToken token,
        long maximumBytes = long.MaxValue) =>
        _compatibility.ReadLegacyFileSetAsync(ownerId, logicalName, token, maximumBytes);

    public async ValueTask<StateRecord?> ReadAsync(
        string ownerId,
        string key,
        CancellationToken token,
        long maximumBytes = long.MaxValue)
    {
        var participant = ResolveParticipant(ownerId);
        ValidateKey(key);
        using var _ = await LockOwnerAsync(ownerId, token);
        if (!_owners.TryGetValue(ownerId, out var owner) ||
            !owner.Records.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (entry.Length > maximumBytes)
        {
            throw new CapabilityStreamLimitExceededException(entry.Length, maximumBytes);
        }

        var value = entry.Value ?? await ReadPayloadAsync(ownerId, key, entry, maximumBytes, token);
        return new StateRecord(
            ownerId,
            key,
            value,
            entry.ETag,
            participant.SchemaName,
            participant.SchemaVersion);
    }

    public async ValueTask<StateRecord> WriteAsync(
        string ownerId,
        string key,
        byte[] value,
        long? expectedETag,
        CancellationToken token,
        long maximumBytes = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(value);
        var results = await CompareAndSwapAsync(
            ownerId,
            [new StateEdit(key, expectedETag, value)],
            token,
            maximumBytes);
        return results[0];
    }

    /// <summary>
    /// Applies every edit for one owner atomically. All concurrency tokens and quotas are
    /// validated before the first byte is written.
    /// </summary>
    public async ValueTask<ImmutableArray<StateRecord>> CompareAndSwapAsync(
        string ownerId,
        IReadOnlyList<StateEdit> edits,
        CancellationToken token,
        long maximumBytes = long.MaxValue)
    {
        var participant = ResolveParticipant(ownerId);
        ArgumentNullException.ThrowIfNull(edits);
        EnsureWritable();
        if (edits.Count == 0)
        {
            throw new ArgumentException("At least one edit is required.", nameof(edits));
        }

        foreach (var edit in edits)
        {
            ValidateKey(edit.Key);
            ArgumentNullException.ThrowIfNull(edit.Value);
            ValidateRecordSize(edit.Value.LongLength, maximumBytes);
        }

        if (edits.Select(edit => edit.Key).Distinct(StringComparer.Ordinal).Count() != edits.Count)
        {
            throw new ArgumentException("Edits must target distinct keys.", nameof(edits));
        }

        token.ThrowIfCancellationRequested();
        using var _ = await LockOwnerAsync(ownerId, token);
        token.ThrowIfCancellationRequested();
        var owner = _owners.TryGetValue(ownerId, out var existing) ? existing : OwnerRecords.Empty;
        foreach (var edit in edits)
        {
            owner.Records.TryGetValue(edit.Key, out var current);
            if (edit.ExpectedETag is { } expected)
            {
                if (current is null || current.ETag != expected)
                {
                    throw new StateConcurrencyException(edit.Key, expected, current?.ETag);
                }
            }
        }

        EnsureOwnerAdmitted(ownerId);
        var projected = owner;
        foreach (var edit in edits)
        {
            projected = projected.With(
                edit.Key,
                new StateEntry(0, edit.Value.LongLength, string.Empty, null));
        }

        EnsureOwnerQuota(ownerId, projected);

        var results = await ApplyBatchAsync(ownerId, participant, edits, owner, token);
        return results;
    }

    public async ValueTask DeleteAsync(
        string ownerId,
        string key,
        long expectedETag,
        CancellationToken token)
    {
        ResolveParticipant(ownerId);
        ValidateKey(key);
        EnsureWritable();
        token.ThrowIfCancellationRequested();
        using var _ = await LockOwnerAsync(ownerId, token);
        var owner = _owners.TryGetValue(ownerId, out var existing) ? existing : OwnerRecords.Empty;
        if (!owner.Records.TryGetValue(key, out var entry) || entry.ETag != expectedETag)
        {
            throw new StateConcurrencyException(key, expectedETag, entry?.ETag);
        }

        if (_root is not null)
        {
            TryDelete(GetRecordPath(_root, ownerId, key));
            TryDelete(_compatibility.GetCompatibilityPath(ownerId, key));
        }

        SetOwner(ownerId, owner.Without(key));
    }

    /// <summary>
    /// Freezes every participant's committed state and returns a leased checkpoint. The
    /// exported content cannot change after this call returns.
    /// </summary>
    public async ValueTask<StateCheckpoint> CreateCheckpointAsync(
        CancellationToken token,
        TimeSpan? lease = null)
    {
        var duration = lease ?? DefaultCheckpointLease;
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease), "A checkpoint lease must be positive.");
        }

        ReleaseExpiredCheckpoints();
        using var _ = await LockAllAsync(token);
        var checkpointId = NextCheckpointId();
        var createdAt = _clock.GetUtcNow();
        string? frozenDirectory = null;
        var frozen = ImmutableArray<StateCheckpointParticipant>.Empty;
        if (_root is null)
        {
            frozen = CaptureFrozenParticipants();
        }
        else
        {
            frozenDirectory = Path.Combine(_root, $"{CheckpointPrefix}{checkpointId:x}");
            CopyDirectory(Path.Combine(_root, ActiveDirectoryName), frozenDirectory);
        }

        var checkpoint = new StateCheckpoint(
            checkpointId,
            createdAt,
            createdAt + duration,
            frozen,
            frozenDirectory,
            ReleaseCheckpoint);
        _checkpoints[checkpoint] = 0;
        return checkpoint;
    }

    public async ValueTask<StateCheckpointData> ExportCheckpointAsync(
        StateCheckpoint checkpoint,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.IsDisposed)
        {
            throw new InvalidOperationException("The checkpoint was already released.");
        }

        if (_clock.GetUtcNow() > checkpoint.ExpiresAt)
        {
            throw new InvalidOperationException(
                $"Checkpoint '{checkpoint.CheckpointId}' expired at {checkpoint.ExpiresAt:O}.");
        }

        var participants = checkpoint.FrozenDirectory is null
            ? checkpoint.FrozenParticipants
            : await ReadParticipantSetAsync(checkpoint.FrozenDirectory, token);
        return new StateCheckpointData(
            StateCheckpointData.CurrentManifestVersion,
            checkpoint.CheckpointId,
            checkpoint.CreatedAt,
            participants);
    }

    /// <summary>
    /// Validates the complete participant, schema, migration, and quota set, then
    /// materializes the migrated content without touching authoritative state.
    /// </summary>
    public async ValueTask<StagedStateRestore> StageRestoreAsync(
        StateCheckpointData data,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(data);
        token.ThrowIfCancellationRequested();
        if (data.ManifestVersion is < 1 or > StateCheckpointData.CurrentManifestVersion)
        {
            throw new StateSchemaCompatibilityException(
                $"Checkpoint manifest version '{data.ManifestVersion}' is not supported.");
        }

        var quarantined = ImmutableArray.CreateBuilder<string>();
        var warnings = ImmutableArray.CreateBuilder<string>();
        var plans = new List<(StateParticipantDescriptor Participant,
            StateCheckpointParticipant Source,
            ImmutableArray<StateSchemaMigration> Path)>();
        foreach (var source in data.Participants)
        {
            if (!_participants.TryGetValue(source.ExtensionId, out var participant))
            {
                if (source.Required)
                {
                    throw new StateSchemaCompatibilityException(
                        $"Required extension '{source.ExtensionId}' has no registered state " +
                        "schema in this server.");
                }

                quarantined.Add(source.ExtensionId);
                warnings.Add(
                    $"Extension '{source.ExtensionId}' is not active; its state was quarantined " +
                    "without activation.");
                continue;
            }

            if (!string.Equals(source.SchemaName, participant.SchemaName, StringComparison.Ordinal))
            {
                throw new StateSchemaCompatibilityException(
                    $"Extension '{source.ExtensionId}' expects schema '{participant.SchemaName}' " +
                    $"but the checkpoint contains '{source.SchemaName}'.");
            }

            if (source.SchemaVersion > participant.SchemaVersion)
            {
                throw new StateSchemaCompatibilityException(
                    $"Extension '{source.ExtensionId}' schema version {source.SchemaVersion} is " +
                    $"newer than the supported version {participant.SchemaVersion}.");
            }

            var path = participant.ResolveMigrationPath(source.SchemaVersion)
                ?? throw new StateSchemaCompatibilityException(
                    $"Extension '{source.ExtensionId}' has no complete migration path from " +
                    $"schema version {source.SchemaVersion} to {participant.SchemaVersion}.");
            plans.Add((participant, source, path));
        }

        foreach (var required in _participants.Values.Where(participant => participant.Required))
        {
            if (!data.Participants.Any(source =>
                    string.Equals(source.ExtensionId, required.ExtensionId, StringComparison.Ordinal)))
            {
                throw new StateSchemaCompatibilityException(
                    $"Required extension '{required.ExtensionId}' state is missing from the " +
                    "checkpoint.");
            }
        }

        if (plans.Count > _quotas.MaximumOwners)
        {
            throw new StateQuotaExceededException(
                $"The checkpoint contains {plans.Count} state owners which exceeds the maximum " +
                $"of {_quotas.MaximumOwners}.");
        }

        var restoreId = NextSequence();
        var staging = _root is null
            ? null
            : Path.Combine(_root, $"{StagingPrefix}{restoreId:x}");
        var prepared = ImmutableDictionary.CreateBuilder<string, OwnerRecords>(StringComparer.Ordinal);
        try
        {
            if (staging is not null)
            {
                // The staged active tree is the whole authoritative record set. It is
                // materialized even when the checkpoint activates no participant, so a
                // commit replaces active state with the empty tree instead of silently
                // preserving the previous one.
                Directory.CreateDirectory(Path.Combine(staging, ActiveDirectoryName));
            }

            foreach (var (participant, source, path) in plans)
            {
                var owner = OwnerRecords.Empty;
                foreach (var record in source.Records)
                {
                    token.ThrowIfCancellationRequested();
                    ValidateKey(record.Key);
                    var value = Migrate(participant, path, record);
                    ValidateRecordSize(value.LongLength, long.MaxValue);
                    var entry = new StateEntry(
                        NextSequence(),
                        value.LongLength,
                        Convert.ToHexStringLower(SHA256.HashData(value)),
                        staging is null ? value : null);
                    owner = owner.With(record.Key, entry);
                    if (staging is not null)
                    {
                        await WriteRecordFileAsync(
                            GetRecordPath(
                                Path.Combine(staging, ActiveDirectoryName),
                                participant.ExtensionId,
                                record.Key),
                            new RecordHeader(
                                record.Key,
                                entry.ETag,
                                participant.SchemaName,
                                participant.SchemaVersion,
                                entry.Length,
                                entry.Sha256),
                            value,
                            token);
                    }
                }

                EnsureOwnerQuota(participant.ExtensionId, owner);
                prepared[participant.ExtensionId] = owner;
                if (staging is not null)
                {
                    await WriteParticipantDescriptorAsync(
                        Path.Combine(staging, ActiveDirectoryName),
                        participant,
                        token);
                }
            }

            if (staging is not null)
            {
                foreach (var source in data.Participants.Where(source =>
                             quarantined.Contains(source.ExtensionId)))
                {
                    await WriteQuarantineAsync(staging, source, token);
                }
            }
        }
        catch
        {
            if (staging is not null && Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }

        return new StagedStateRestore(
            restoreId,
            data,
            staging,
            prepared.ToImmutable(),
            new StateRestoreReport(quarantined.ToImmutable(), warnings.ToImmutable()),
            AbortStaged);
    }

    /// <summary>
    /// Makes staged content authoritative. The journal write is the only commit point; a
    /// crash before it aborts and a crash after it replays deterministically.
    /// </summary>
    public async ValueTask<StateRestoreReport> CommitRestoreAsync(
        StagedStateRestore staged,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(staged);
        token.ThrowIfCancellationRequested();
        EnsureWritable();
        await _restoreGate.WaitAsync(token);
        try
        {
            staged.MarkCompleted();
            using var _ = await LockAllAsync(token);
            if (_root is not null && staged.StagingDirectory is not null)
            {
                var journal = new RestoreJournal(
                    1,
                    Path.GetFileName(staged.StagingDirectory),
                    $"{TrashPrefix}{staged.RestoreId:x}",
                    staged.RestoreId);
                await WriteFileAtomicAsync(
                    Path.Combine(_root, RestoreJournalFileName),
                    JsonSerializer.SerializeToUtf8Bytes(journal),
                    token);
                ApplyJournal(_root, journal);
                _owners = LoadIndex();
                LoadSequence();
            }
            else
            {
                _owners = staged.PreparedOwners;
            }

            return staged.Report;
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    public ValueTask AbortRestoreAsync(StagedStateRestore staged, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(staged);
        token.ThrowIfCancellationRequested();
        staged.MarkCompleted();
        AbortStaged(staged);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Completes or discards an interrupted restore and removes transient content. It is
    /// safe to call repeatedly and is executed before a store is opened.
    /// </summary>
    internal static void Recover(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
        {
            return;
        }

        RecoverWriteJournal(full);
        var journalPath = Path.Combine(full, RestoreJournalFileName);
        RestoreJournal? journal = null;
        if (File.Exists(journalPath))
        {
            try
            {
                journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllBytes(journalPath));
            }
            catch (JsonException)
            {
                // A journal that is not complete JSON was torn by a crash before the commit
                // point, so the restore it describes never became authoritative.
                journal = null;
            }
        }

        if (journal is not null)
        {
            ValidateRestoreJournal(journal);
            ApplyJournal(full, journal);
        }
        else if (File.Exists(journalPath))
        {
            File.Delete(journalPath);
        }

        foreach (var directory in Directory.EnumerateDirectories(full))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(StagingPrefix, StringComparison.Ordinal) ||
                name.StartsWith(TrashPrefix, StringComparison.Ordinal) ||
                name.StartsWith(CheckpointPrefix, StringComparison.Ordinal))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Rolls a committed but unpublished durable batch forward. The write journal is the
    /// single commit point for a batch, so replaying it is idempotent and always completes
    /// the whole batch — every record and the version 1 mirror those records project —
    /// rather than a subset of it.
    /// </summary>
    private static void RecoverWriteJournal(string root)
    {
        var journalPath = Path.Combine(root, WriteJournalFileName);
        if (!File.Exists(journalPath))
        {
            return;
        }

        WriteJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<WriteJournal>(File.ReadAllBytes(journalPath));
        }
        catch (JsonException)
        {
            journal = null;
        }

        if (journal is null)
        {
            File.Delete(journalPath);
            return;
        }

        ValidateWriteJournal(journal);
        ApplyWriteJournal(root, journal);
    }

    /// <summary>
    /// A journal only ever names directories this store generated inside its own root, so a
    /// journal that arrived with restored content or was edited by hand can never direct a
    /// move or a recursive delete outside the state root.
    /// </summary>
    private static void ValidateRestoreJournal(RestoreJournal journal)
    {
        if (journal.Version != 1 ||
            journal.RestoreId < 0 ||
            !IsGeneratedDirectoryName(journal.StagingDirectory, StagingPrefix) ||
            !IsGeneratedDirectoryName(journal.TrashDirectory, TrashPrefix))
        {
            throw new ExtensionStateException(
                "The extension state restore journal is not a journal this store wrote. " +
                "Remove the untrusted journal before starting the server.");
        }
    }

    /// <summary>
    /// A write journal names the staging directory the commit generated, the owner
    /// directory it publishes into, and the record file names that batch owns. Every name
    /// is a store-generated hash name, so a journal that arrived with restored content or
    /// was edited by hand can never direct a move, a projection, or a recursive delete
    /// outside the state root.
    /// </summary>
    private static void ValidateWriteJournal(WriteJournal journal)
    {
        if (journal.Version != 1 ||
            journal.BatchId < 0 ||
            !IsGeneratedDirectoryName(journal.StagingDirectory, StagingPrefix) ||
            !IsOwnerDirectoryName(journal.OwnerDirectory) ||
            journal.Records is not { Count: > 0 } records ||
            records.Any(name => !IsHashName(name)) ||
            records.Distinct(StringComparer.Ordinal).Count() != records.Count)
        {
            throw new ExtensionStateException(
                "The extension state write journal is not a journal this store wrote. " +
                "Remove the untrusted journal before starting the server.");
        }
    }

    private static bool IsGeneratedDirectoryName(string? name, string prefix) =>
        name is not null &&
        name.Length > prefix.Length &&
        name.Length <= prefix.Length + 16 &&
        name.StartsWith(prefix, StringComparison.Ordinal) &&
        name.AsSpan(prefix.Length).ToString().All(char.IsAsciiHexDigitLower);

    private static bool IsOwnerDirectoryName(string? name) => IsHashName(name);

    private static bool IsHashName(string? name) =>
        name is { Length: 64 } && name.All(char.IsAsciiHexDigitLower);

    internal static ImmutableArray<StateParticipantDescriptor> ReadPersistedParticipants(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var active = Path.Combine(Path.GetFullPath(root), ActiveDirectoryName);
        if (!Directory.Exists(active))
        {
            return [];
        }

        var participants = ImmutableArray.CreateBuilder<StateParticipantDescriptor>();
        foreach (var directory in Directory.EnumerateDirectories(active).Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(directory, ParticipantFileName);
            if (File.Exists(path))
            {
                participants.Add(ReadPersistedDescriptor(path));
            }
        }

        return participants.ToImmutable();
    }

    /// <summary>
    /// Describes every participant in a committed or staged record tree without holding
    /// its records. Record identity comes from bounded headers and every payload is
    /// streamed through one fixed buffer, so this costs one buffer rather than the size
    /// of the tree, and the integrity value it produces is the value a materialized
    /// checkpoint participant computes for the same content. Per-record, per-owner, and
    /// owner-count quotas bound what an untrusted tree can ask this to process.
    /// </summary>
    internal static async ValueTask<ImmutableArray<StateParticipantSummary>>
        SummarizeParticipantSetAsync(
            string activeDirectory,
            CancellationToken token,
            StateStoreQuotas? quotas = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeDirectory);
        var limits = (quotas ?? new StateStoreQuotas()).Validate();
        var summaries = ImmutableArray.CreateBuilder<StateParticipantSummary>();
        if (!Directory.Exists(activeDirectory))
        {
            return summaries.ToImmutable();
        }

        var buffer = new byte[64 * 1024];
        foreach (var directory in Directory.EnumerateDirectories(activeDirectory)
                     .Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var descriptorPath = Path.Combine(directory, ParticipantFileName);
            var files = Directory.EnumerateFiles(directory, $"*{RecordExtension}")
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!File.Exists(descriptorPath))
            {
                if (files.Length > 0)
                {
                    throw new ExtensionStateException(
                        "An extension state owner holds records without a participant " +
                        "descriptor.");
                }

                continue;
            }

            if (summaries.Count >= limits.MaximumOwners)
            {
                throw new StateQuotaExceededException(
                    $"Extension state holds more than {limits.MaximumOwners} owners.");
            }

            var descriptor = ReadPersistedDescriptor(descriptorPath);
            if (!string.Equals(
                    Path.GetFileName(directory),
                    OwnerDirectoryName(descriptor.ExtensionId),
                    StringComparison.Ordinal))
            {
                throw new ExtensionStateException(
                    $"Extension state for '{descriptor.ExtensionId}' is not stored under the " +
                    "directory this store writes.");
            }

            if (files.Length > limits.MaximumRecordsPerOwner)
            {
                throw new StateQuotaExceededException(
                    $"Extension '{descriptor.ExtensionId}' holds {files.Length} records which " +
                    $"exceeds the maximum of {limits.MaximumRecordsPerOwner}.");
            }

            var headers = new List<(string Path, RecordHeader Header)>(files.Length);
            long totalBytes = 0;
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var header = ReadHeader(file)
                    ?? throw new ExtensionStateException(
                        $"Extension state record '{Path.GetFileName(file)}' has no readable " +
                        "header.");
                try
                {
                    ValidateKey(header.Key, limits);
                }
                catch (ArgumentException exception)
                {
                    throw new ExtensionStateException(
                        "An extension state record declares a key this store never writes.",
                        exception);
                }

                if (!string.Equals(
                        Path.GetFileNameWithoutExtension(file),
                        KeyFileName(header.Key),
                        StringComparison.Ordinal))
                {
                    throw new ExtensionStateException(
                        $"Extension state record '{header.Key}' is not stored under the file " +
                        "name this store writes.");
                }

                if (header.Length < 0 || header.Length > limits.MaximumRecordBytes)
                {
                    throw new StateQuotaExceededException(
                        $"Extension state record '{header.Key}' declares {header.Length} bytes " +
                        $"which exceeds the maximum of {limits.MaximumRecordBytes} bytes.");
                }

                totalBytes += header.Length;
                if (totalBytes > limits.MaximumOwnerBytes)
                {
                    throw new StateQuotaExceededException(
                        $"Extension '{descriptor.ExtensionId}' holds more than " +
                        $"{limits.MaximumOwnerBytes} bytes.");
                }

                headers.Add((file, header));
            }

            headers.Sort((left, right) =>
                StringComparer.Ordinal.Compare(left.Header.Key, right.Header.Key));
            using var integrity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            integrity.AppendData(Encoding.UTF8.GetBytes(
                $"{descriptor.ExtensionId}\n{descriptor.ExtensionVersion}\n" +
                $"{descriptor.SchemaName}\n{descriptor.SchemaVersion}\n{descriptor.Required}\n"));
            long highWater = 0;
            foreach (var (path, header) in headers)
            {
                integrity.AppendData(Encoding.UTF8.GetBytes($"{header.Key}\n{header.Length}\n"));
                await AppendRecordPayloadAsync(path, header, integrity, buffer, token);
                highWater = Math.Max(highWater, header.ETag);
            }

            summaries.Add(new StateParticipantSummary(
                descriptor.ExtensionId,
                descriptor.ExtensionVersion,
                descriptor.SchemaName,
                descriptor.SchemaVersion,
                descriptor.Required,
                headers.Count,
                totalBytes,
                highWater,
                Convert.ToHexStringLower(integrity.GetHashAndReset())));
        }

        return [.. summaries.OrderBy(summary => summary.ExtensionId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Validates the version 1 downgrade mirror of a state root against the authoritative
    /// record tree it projects. The mirror is the one part of a state root that is neither
    /// covered by a participant descriptor nor by a typed participant integrity value, and
    /// the next open adopts mirror-only keys of a registered owner into the authoritative
    /// tree, so untrusted content that arrives there has to be proven to be exactly what
    /// the validated tree already declares.
    /// <para>
    /// A mirror of a <paramref name="declaredOwnerIds"/> owner may only hold records that
    /// project a committed record of that owner, with the same safe hashed owner and key
    /// path and the same envelope key and payload identity. A mirror of any other owner
    /// this build could adopt — an owner in <paramref name="registeredOwnerIds"/> the
    /// declared set never named — is refused outright. A mirror of an owner this build
    /// never registers cannot be adopted at all and is left untouched.
    /// </para>
    /// <para>
    /// A state root with no authoritative tree at all predates the transactional layout,
    /// so its mirror is the state itself rather than a projection and is left to the
    /// version 1 adoption path.
    /// </para>
    /// Identity comes from record headers and envelope metadata, so this costs one bounded
    /// read per mirror file rather than the size of the tree.
    /// </summary>
    internal static void ValidateCompatibilityMirror(
        string stateRoot,
        IEnumerable<string> declaredOwnerIds,
        IEnumerable<string> registeredOwnerIds,
        StateStoreQuotas? quotas = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        ArgumentNullException.ThrowIfNull(declaredOwnerIds);
        ArgumentNullException.ThrowIfNull(registeredOwnerIds);
        var limits = (quotas ?? new StateStoreQuotas()).Validate();
        var root = Path.GetFullPath(stateRoot);
        var active = Path.Combine(root, ActiveDirectoryName);
        if (!Directory.Exists(root) || !Directory.Exists(active))
        {
            return;
        }

        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ownerId in declaredOwnerIds)
        {
            declared[OwnerDirectoryName(ownerId)] = ownerId;
        }

        var registered = registeredOwnerIds
            .Select(OwnerDirectoryName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (!IsOwnerDirectoryName(name))
            {
                continue;
            }

            if (!declared.TryGetValue(name, out var ownerId))
            {
                if (registered.Contains(name))
                {
                    throw new ExtensionStateException(
                        "Extension state holds version 1 records for an owner the validated " +
                        "participant set never declares.");
                }

                continue;
            }

            ValidateCompatibilityMirrorOwner(
                directory,
                Path.Combine(active, name),
                ownerId,
                limits);
        }
    }

    /// <summary>
    /// Proves one owner's mirror is a projection of that owner's committed records. A
    /// mirror write is best effort, so a record without a mirror file is expected; a
    /// mirror file without a matching record, or one whose envelope names a different key
    /// or a different payload, is not a projection this store ever wrote.
    /// </summary>
    private static void ValidateCompatibilityMirrorOwner(
        string mirrorDirectory,
        string ownerDirectory,
        string ownerId,
        StateStoreQuotas limits)
    {
        if (Directory.EnumerateDirectories(mirrorDirectory).Any())
        {
            throw new ExtensionStateException(
                $"The version 1 mirror of '{ownerId}' holds a directory this store never " +
                "writes.");
        }

        var files = Directory.EnumerateFiles(mirrorDirectory).Order(StringComparer.Ordinal).ToArray();
        if (files.Length > limits.MaximumRecordsPerOwner)
        {
            throw new StateQuotaExceededException(
                $"The version 1 mirror of '{ownerId}' holds {files.Length} records which " +
                $"exceeds the maximum of {limits.MaximumRecordsPerOwner}.");
        }

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!string.Equals(Path.GetExtension(file), ".json", StringComparison.Ordinal) ||
                !IsHashName(name))
            {
                throw new ExtensionStateException(
                    $"The version 1 mirror of '{ownerId}' holds a file name this store never " +
                    "writes.");
            }

            EnsureMirrorFileIsReadable(ownerId, file, limits);
            var envelope = ExtensionStateStore.TryReadCompatibilityIdentity(file)
                ?? throw new ExtensionStateException(
                    $"The version 1 mirror of '{ownerId}' holds a record this store never wrote.");
            try
            {
                ValidateKey(envelope.Key, limits);
            }
            catch (ArgumentException exception)
            {
                throw new ExtensionStateException(
                    $"The version 1 mirror of '{ownerId}' declares a key this store never writes.",
                    exception);
            }

            if (!string.Equals(KeyFileName(envelope.Key), name, StringComparison.Ordinal))
            {
                throw new ExtensionStateException(
                    $"The version 1 mirror of '{ownerId}' is not stored under the file name this " +
                    "store writes.");
            }

            var record = Path.Combine(ownerDirectory, $"{name}{RecordExtension}");
            var header = File.Exists(record) ? ReadHeader(record) : null;
            if (header is null ||
                !string.Equals(header.Key, envelope.Key, StringComparison.Ordinal) ||
                !string.Equals(header.Sha256, envelope.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExtensionStateException(
                    $"The version 1 mirror of '{ownerId}' is not a projection of the committed " +
                    $"record '{envelope.Key}'.");
            }
        }
    }

    private static void EnsureMirrorFileIsReadable(
        string ownerId,
        string path,
        StateStoreQuotas limits)
    {
        var length = new FileInfo(path).Length;
        var limit = MaximumLegacyRecordBytes(limits);
        if (length > limit)
        {
            throw new StateQuotaExceededException(
                $"The version 1 mirror record '{Path.GetFileName(path)}' of '{ownerId}' is " +
                $"{length} bytes and cannot hold a record within the " +
                $"{limits.MaximumRecordBytes} byte maximum.");
        }
    }

    /// <summary>
    /// Streams one record's payload into the participant integrity value while
    /// validating the payload against the identity its header committed. The payload is
    /// never held.
    /// </summary>
    private static async ValueTask AppendRecordPayloadAsync(
        string path,
        RecordHeader header,
        IncrementalHash integrity,
        byte[] buffer,
        CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            ParseHeader(stream, path, out var prefix, out var payloadOffset);
            if (header.Length != stream.Length - payloadOffset)
            {
                throw new ExtensionStateException(
                    $"Extension state record '{header.Key}' has an inconsistent length.");
            }

            using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var carried = (int)Math.Min(prefix.Length - payloadOffset, header.Length);
            if (carried > 0)
            {
                integrity.AppendData(prefix, payloadOffset, carried);
                content.AppendData(prefix, payloadOffset, carried);
            }

            var remaining = header.Length - carried;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    token);
                if (read == 0)
                {
                    throw new ExtensionStateException(
                        $"Extension state record '{header.Key}' ended before its declared " +
                        "length.");
                }

                integrity.AppendData(buffer, 0, read);
                content.AppendData(buffer, 0, read);
                remaining -= read;
            }

            if (!string.Equals(
                    Convert.ToHexStringLower(content.GetHashAndReset()),
                    header.Sha256,
                    StringComparison.Ordinal))
            {
                throw new ExtensionStateException(
                    $"Extension state record '{header.Key}' failed integrity validation.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
            FormatException)
        {
            throw new ExtensionStateException(
                "Extension state could not be read or validated.",
                exception);
        }
    }

    private static StateParticipantDescriptor ReadPersistedDescriptor(string path)
    {
        PersistedParticipant? document;
        try
        {
            document = JsonSerializer.Deserialize<PersistedParticipant>(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new ExtensionStateException(
                "An extension state participant descriptor could not be read.",
                exception);
        }

        if (document is null)
        {
            throw new ExtensionStateException(
                "An extension state participant descriptor is empty.");
        }

        try
        {
            return new StateParticipantDescriptor(
                document.ExtensionId,
                document.ExtensionVersion,
                document.SchemaName,
                document.SchemaVersion,
                document.Required).Validate();
        }
        catch (ArgumentException exception)
        {
            throw new ExtensionStateException(
                "An extension state participant descriptor is invalid.",
                exception);
        }
    }

    public void Dispose()
    {
        foreach (var checkpoint in _checkpoints.Keys)
        {
            checkpoint.Dispose();
        }

        foreach (var stripe in _stripes)
        {
            stripe.Dispose();
        }

        _restoreGate.Dispose();
        _commitGate.Dispose();
    }

    /// <summary>
    /// Refuses further durable mutation while a committed batch is still waiting to be
    /// published, because a later journal would replace the pending one.
    /// </summary>
    private void EnsureWritable()
    {
        if (Volatile.Read(ref _publishFailed) != 0)
        {
            throw new ExtensionStateException(
                "Extension state has a pending write journal that could not be published. " +
                "Restart the server so the committed batch is replayed.");
        }
    }

    private StateParticipantDescriptor ResolveParticipant(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        return _participants.TryGetValue(ownerId, out var participant)
            ? participant
            : throw new ExtensionStateException(
                $"Extension '{ownerId}' has no registered state schema.");
    }

    private void ValidateKey(string key) => ValidateKey(key, _quotas);

    private static void ValidateKey(string key, StateStoreQuotas quotas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > quotas.MaximumKeyLength)
        {
            throw new StateQuotaExceededException(
                $"Extension state key length {key.Length} exceeds the maximum of " +
                $"{quotas.MaximumKeyLength}.");
        }

        foreach (var character in key)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                throw new ArgumentException(
                    "Extension state keys may contain only letters, numbers, '.', '_' and '-'.",
                    nameof(key));
            }
        }
    }

    private void ValidateRecordSize(long length, long maximumBytes)
    {
        if (length > maximumBytes)
        {
            throw new CapabilityStreamLimitExceededException(length, maximumBytes);
        }

        if (length > _quotas.MaximumRecordBytes)
        {
            throw new StateQuotaExceededException(
                $"Extension state record of {length} bytes exceeds the maximum of " +
                $"{_quotas.MaximumRecordBytes} bytes.");
        }
    }

    private void EnsureOwnerAdmitted(string ownerId)
    {
        if (!_owners.ContainsKey(ownerId) && _owners.Count >= _quotas.MaximumOwners)
        {
            throw new StateQuotaExceededException(
                $"Extension state already holds {_owners.Count} owners which is the configured " +
                "maximum.");
        }
    }

    private void EnsureOwnerQuota(string ownerId, OwnerRecords owner)
    {
        if (owner.Records.Count > _quotas.MaximumRecordsPerOwner)
        {
            throw new StateQuotaExceededException(
                $"Extension '{ownerId}' would hold {owner.Records.Count} records which exceeds " +
                $"the maximum of {_quotas.MaximumRecordsPerOwner}.");
        }

        if (owner.TotalBytes > _quotas.MaximumOwnerBytes)
        {
            throw new StateQuotaExceededException(
                $"Extension '{ownerId}' would hold {owner.TotalBytes} bytes which exceeds the " +
                $"maximum of {_quotas.MaximumOwnerBytes} bytes.");
        }
    }

    /// <summary>
    /// Applies one owner's batch as a single durable transaction. Every record and the owner
    /// descriptor are staged outside the authoritative tree first; the write journal is the
    /// only commit point, so a failure or cancellation before it leaves the previous state
    /// complete, and a failure after it is rolled forward to the complete new batch.
    /// </summary>
    private async ValueTask<ImmutableArray<StateRecord>> ApplyBatchAsync(
        string ownerId,
        StateParticipantDescriptor participant,
        IReadOnlyList<StateEdit> edits,
        OwnerRecords owner,
        CancellationToken token)
    {
        var results = ImmutableArray.CreateBuilder<StateRecord>(edits.Count);
        var updated = owner;
        if (_root is null)
        {
            foreach (var edit in edits)
            {
                var entry = new StateEntry(
                    NextSequence(),
                    edit.Value.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(edit.Value)),
                    edit.Value);
                updated = updated.With(edit.Key, entry);
                results.Add(new StateRecord(
                    ownerId,
                    edit.Key,
                    edit.Value,
                    entry.ETag,
                    participant.SchemaName,
                    participant.SchemaVersion));
            }

            SetOwner(ownerId, updated);
            return results.MoveToImmutable();
        }

        var batchId = NextSequence();
        var staging = Path.Combine(_root, $"{StagingPrefix}{batchId:x}");
        var ownerDirectory = OwnerDirectoryName(ownerId);
        var stagedActive = Path.Combine(staging, ActiveDirectoryName);
        try
        {
            Directory.CreateDirectory(Path.Combine(stagedActive, ownerDirectory));
            foreach (var edit in edits)
            {
                token.ThrowIfCancellationRequested();
                var eTag = NextSequence();
                var sha256 = Convert.ToHexStringLower(SHA256.HashData(edit.Value));
                await WriteRecordFileAsync(
                    GetRecordPath(stagedActive, ownerId, edit.Key),
                    new RecordHeader(
                        edit.Key,
                        eTag,
                        participant.SchemaName,
                        participant.SchemaVersion,
                        edit.Value.LongLength,
                        sha256),
                    edit.Value,
                    token);
                updated = updated.With(
                    edit.Key,
                    new StateEntry(eTag, edit.Value.LongLength, sha256, null));
                results.Add(new StateRecord(
                    ownerId,
                    edit.Key,
                    edit.Value,
                    eTag,
                    participant.SchemaName,
                    participant.SchemaVersion));
            }

            await WriteParticipantDescriptorAsync(stagedActive, participant, token);
            WriteFaultInjector?.Invoke(StateWriteFailPoint.AfterStage);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteDirectory(staging);
            throw new ExtensionStateException(
                "Extension state could not be persisted.",
                exception);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }

        var journal = new WriteJournal(
            1,
            Path.GetFileName(staging),
            ownerDirectory,
            batchId,
            [.. edits.Select(edit => KeyFileName(edit.Key))]);

        // One batch at a time owns the commit journal, so a concurrent owner can never
        // replace or delete a journal that has not been published yet.
        await _commitGate.WaitAsync(CancellationToken.None);
        try
        {
            try
            {
                await WriteFileAtomicAsync(
                    Path.Combine(_root, WriteJournalFileName),
                    JsonSerializer.SerializeToUtf8Bytes(journal),
                    CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                TryDeleteDirectory(staging);
                throw new ExtensionStateException(
                    "Extension state could not be committed.",
                    exception);
            }

            // The batch is authoritative from here: publishing only materializes it, and an
            // interruption is replayed from the journal when the store is next opened. The
            // version 1 mirror is projected while the journal is still pending and the
            // journal is retired last, so a crash between publishing a record and mirroring
            // it can never leave a downgrade reading a pre-crash value.
            SetOwner(ownerId, updated);
            try
            {
                WriteFaultInjector?.Invoke(StateWriteFailPoint.AfterCommitJournal);
                PublishWriteJournal(_root, journal, WriteFaultInjector);
                WriteFaultInjector?.Invoke(StateWriteFailPoint.BeforeMirrorRefresh);
                foreach (var edit in edits)
                {
                    await MirrorAsync(ownerId, edit.Key, edit.Value, CancellationToken.None);
                }

                WriteFaultInjector?.Invoke(StateWriteFailPoint.AfterMirrorRefresh);
                CompleteWriteJournal(_root, journal);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The journal must survive to be replayed, so this store stops mutating
                // durable state rather than overwriting it with a later batch.
                Volatile.Write(ref _publishFailed, 1);
                throw new ExtensionStateException(
                    "Extension state was committed but could not be published; the pending write " +
                    "journal completes it when the store is next opened.",
                    exception);
            }
            catch
            {
                Volatile.Write(ref _publishFailed, 1);
                throw;
            }
        }
        finally
        {
            _commitGate.Release();
        }

        return results.MoveToImmutable();
    }

    /// <summary>
    /// Rolls a committed but unpublished durable batch forward: it publishes every record
    /// the batch owns, refreshes the version 1 mirror of exactly those records, and only
    /// then retires the journal. Every step is idempotent, so a crash at any point inside
    /// a batch replays the whole batch rather than a subset of it.
    /// </summary>
    private static void ApplyWriteJournal(string root, WriteJournal journal)
    {
        PublishWriteJournal(root, journal);
        RefreshCompatibilityMirror(root, journal);
        CompleteWriteJournal(root, journal);
    }

    /// <summary>
    /// Moves a committed batch into the authoritative tree. Every move is idempotent, so a
    /// replay publishes exactly the records that a crash left behind.
    /// </summary>
    private static void PublishWriteJournal(
        string root,
        WriteJournal journal,
        Action<StateWriteFailPoint>? faultInjector = null)
    {
        ValidateWriteJournal(journal);
        var stagedOwner = Path.Combine(
            root,
            journal.StagingDirectory,
            ActiveDirectoryName,
            journal.OwnerDirectory);
        if (!Directory.Exists(stagedOwner))
        {
            return;
        }

        var target = Path.Combine(root, ActiveDirectoryName, journal.OwnerDirectory);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(stagedOwner).Order(StringComparer.Ordinal))
        {
            faultInjector?.Invoke(StateWriteFailPoint.BeforePublishRecord);
            File.Move(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    /// <summary>
    /// Retires a journal whose batch is completely published and mirrored. It runs last, so
    /// a journal only disappears once nothing in the batch is left to replay.
    /// </summary>
    private static void CompleteWriteJournal(string root, WriteJournal journal)
    {
        var journalPath = Path.Combine(root, WriteJournalFileName);
        if (File.Exists(journalPath))
        {
            File.Delete(journalPath);
        }

        TryDeleteDirectory(Path.Combine(root, journal.StagingDirectory));
    }

    /// <summary>
    /// Republishes the version 1 mirror of exactly the records a rolled-forward batch made
    /// authoritative. The journal names its own records, so recovery projects only the
    /// owner and keys the interrupted batch owned and never rebuilds an unrelated mirror,
    /// and it reads one published record at a time so the projection stays bounded by the
    /// record quota instead of by the size of the tree. Rewriting a mirror that is already
    /// current is harmless, so a replay after an interrupted refresh is idempotent. A
    /// mirror is a rebuildable projection, so a record that cannot be projected retires its
    /// mirror file rather than leaving a pre-crash value where a downgrade would read it.
    /// Each mirror is published atomically, so a downgrade never reads a half-written one.
    /// </summary>
    private static void RefreshCompatibilityMirror(string root, WriteJournal journal)
    {
        var owner = Path.Combine(root, ActiveDirectoryName, journal.OwnerDirectory);
        var mirrorDirectory = Path.Combine(root, journal.OwnerDirectory);
        foreach (var name in journal.Records ?? [])
        {
            var record = Path.Combine(owner, $"{name}{RecordExtension}");
            var mirror = Path.Combine(mirrorDirectory, $"{name}.json");
            if (!File.Exists(record))
            {
                continue;
            }

            try
            {
                var published = ReadRecordFileAsync(record, long.MaxValue, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

                // A record file is named by the same key hash the version 1 reader
                // computes, so a record that does not hash to its own file name cannot be
                // projected into a mirror that reader would ever find.
                if (!string.Equals(
                        KeyFileName(published.Header.Key),
                        name,
                        StringComparison.Ordinal))
                {
                    TryDelete(mirror);
                    continue;
                }

                WriteFileAtomicAsync(
                        mirror,
                        ExtensionStateStore.CreateCompatibilityEnvelope(
                            published.Header.Key,
                            published.Payload),
                        CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception) when (
                exception is ExtensionStateException or IOException or UnauthorizedAccessException)
            {
                TryDelete(mirror);
            }
        }
    }

    private async ValueTask<StateEntry> PersistAsync(
        string ownerId,
        StateParticipantDescriptor participant,
        string key,
        byte[] value,
        long eTag,
        CancellationToken token)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(value));
        if (_root is null)
        {
            return new StateEntry(eTag, value.LongLength, sha256, value);
        }

        var header = new RecordHeader(
            key,
            eTag,
            participant.SchemaName,
            participant.SchemaVersion,
            value.LongLength,
            sha256);
        try
        {
            await WriteRecordFileAsync(
                GetRecordPath(Path.Combine(_root, ActiveDirectoryName), ownerId, key),
                header,
                value,
                token);
            await WriteParticipantDescriptorAsync(
                Path.Combine(_root, ActiveDirectoryName),
                participant,
                token);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new ExtensionStateException(
                "Extension state could not be persisted.",
                exception);
        }

        await MirrorAsync(ownerId, key, value, token);
        return new StateEntry(eTag, value.LongLength, sha256, null);
    }

    /// <summary>
    /// Maintains the version 1 record mirror so an older server build keeps reading the same
    /// state. The mirror is a rebuildable projection; a failed mirror write removes the stale
    /// file instead of failing the authoritative write.
    /// </summary>
    private async ValueTask MirrorAsync(
        string ownerId,
        string key,
        byte[] value,
        CancellationToken token)
    {
        try
        {
            await _compatibility.WriteRawAsync(ownerId, key, value, token);
        }
        catch (Exception exception) when (
            exception is ExtensionStateException or IOException or UnauthorizedAccessException)
        {
            TryDelete(_compatibility.GetCompatibilityPath(ownerId, key));
        }
    }

    private async ValueTask<byte[]> ReadPayloadAsync(
        string ownerId,
        string key,
        StateEntry entry,
        long maximumBytes,
        CancellationToken token)
    {
        var path = GetRecordPath(Path.Combine(_root!, ActiveDirectoryName), ownerId, key);
        var (header, payload) = await ReadRecordFileAsync(path, maximumBytes, token);
        if (header.ETag != entry.ETag ||
            !string.Equals(header.Sha256, entry.Sha256, StringComparison.Ordinal))
        {
            throw new ExtensionStateException(
                $"Extension state record '{key}' does not match its committed identity.");
        }

        return payload;
    }

    private ImmutableArray<StateCheckpointParticipant> CaptureFrozenParticipants()
    {
        var participants = ImmutableArray.CreateBuilder<StateCheckpointParticipant>();
        foreach (var participant in Participants)
        {
            var records = ImmutableArray.CreateBuilder<StateCheckpointRecord>();
            if (_owners.TryGetValue(participant.ExtensionId, out var owner))
            {
                foreach (var (key, entry) in owner.Records.OrderBy(
                             pair => pair.Key,
                             StringComparer.Ordinal))
                {
                    records.Add(new StateCheckpointRecord(key, entry.Value!, entry.ETag));
                }
            }

            participants.Add(new StateCheckpointParticipant(
                participant.ExtensionId,
                participant.ExtensionVersion,
                participant.SchemaName,
                participant.SchemaVersion,
                participant.Required,
                records.ToImmutable()));
        }

        return participants.ToImmutable();
    }

    /// <summary>
    /// Reads the typed participant set from a committed or frozen record tree without
    /// mutating it.
    /// </summary>
    internal static async ValueTask<ImmutableArray<StateCheckpointParticipant>>
        ReadParticipantSetAsync(string activeDirectory, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activeDirectory);
        var participants = ImmutableArray.CreateBuilder<StateCheckpointParticipant>();
        if (!Directory.Exists(activeDirectory))
        {
            return participants.ToImmutable();
        }

        foreach (var directory in Directory.EnumerateDirectories(activeDirectory)
                     .Order(StringComparer.Ordinal))
        {
            var descriptorPath = Path.Combine(directory, ParticipantFileName);
            if (!File.Exists(descriptorPath))
            {
                continue;
            }

            var descriptor = JsonSerializer.Deserialize<PersistedParticipant>(
                    await File.ReadAllBytesAsync(descriptorPath, token))
                ?? throw new ExtensionStateException(
                    "An extension state participant descriptor is empty.");
            var records = ImmutableArray.CreateBuilder<StateCheckpointRecord>();
            foreach (var file in Directory
                         .EnumerateFiles(directory, $"*{RecordExtension}")
                         .Order(StringComparer.Ordinal))
            {
                var (header, payload) = await ReadRecordFileAsync(file, long.MaxValue, token);
                records.Add(new StateCheckpointRecord(header.Key, payload, header.ETag));
            }

            participants.Add(new StateCheckpointParticipant(
                descriptor.ExtensionId,
                descriptor.ExtensionVersion,
                descriptor.SchemaName,
                descriptor.SchemaVersion,
                descriptor.Required,
                [.. records.OrderBy(record => record.Key, StringComparer.Ordinal)]));
        }

        return [.. participants.OrderBy(
            participant => participant.ExtensionId,
            StringComparer.Ordinal)];
    }

    private static byte[] Migrate(
        StateParticipantDescriptor participant,
        ImmutableArray<StateSchemaMigration> path,
        StateCheckpointRecord record)
    {
        var value = record.Value;
        foreach (var migration in path)
        {
            try
            {
                value = migration.Transform(value)
                    ?? throw new StateSchemaCompatibilityException(
                        $"Migration {migration.FromVersion}->{migration.ToVersion} for " +
                        $"'{participant.ExtensionId}' produced no value.");
            }
            catch (Exception exception) when (exception is not StateSchemaCompatibilityException)
            {
                throw new StateSchemaCompatibilityException(
                    $"Migration {migration.FromVersion}->{migration.ToVersion} for " +
                    $"'{participant.ExtensionId}' record '{record.Key}' failed: " +
                    exception.Message);
            }
        }

        return value;
    }

    private async ValueTask WriteQuarantineAsync(
        string staging,
        StateCheckpointParticipant source,
        CancellationToken token)
    {
        var directory = Path.Combine(
            staging,
            QuarantineDirectoryName,
            OwnerDirectoryName(source.ExtensionId));
        Directory.CreateDirectory(directory);
        await WriteFileAtomicAsync(
            Path.Combine(directory, ParticipantFileName),
            JsonSerializer.SerializeToUtf8Bytes(new PersistedParticipant(
                source.ExtensionId,
                source.ExtensionVersion,
                source.SchemaName,
                source.SchemaVersion,
                source.Required)),
            token);
        foreach (var record in source.Records)
        {
            await WriteRecordFileAsync(
                Path.Combine(directory, $"{KeyFileName(record.Key)}{RecordExtension}"),
                new RecordHeader(
                    record.Key,
                    record.ETag,
                    source.SchemaName,
                    source.SchemaVersion,
                    record.Value.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(record.Value))),
                record.Value,
                token);
        }
    }

    private void AbortStaged(StagedStateRestore staged)
    {
        if (staged.StagingDirectory is not null && Directory.Exists(staged.StagingDirectory))
        {
            Directory.Delete(staged.StagingDirectory, recursive: true);
        }
    }

    private void ReleaseCheckpoint(StateCheckpoint checkpoint)
    {
        _checkpoints.TryRemove(checkpoint, out _);
        if (checkpoint.FrozenDirectory is not null &&
            Directory.Exists(checkpoint.FrozenDirectory))
        {
            Directory.Delete(checkpoint.FrozenDirectory, recursive: true);
        }
    }

    private void ReleaseExpiredCheckpoints()
    {
        var now = _clock.GetUtcNow();
        foreach (var checkpoint in _checkpoints.Keys)
        {
            if (now > checkpoint.ExpiresAt)
            {
                checkpoint.Dispose();
            }
        }
    }

    /// <summary>
    /// Rebuilds the authoritative index from committed record headers and validates the
    /// tree it describes against the quotas a write obeys. The tree on disk is untrusted —
    /// a restore, an operator, or an interrupted earlier build can have left it over quota
    /// or unreadable — so an open that cannot prove the committed tree is one this store
    /// would have written fails closed instead of adopting it as the baseline the next
    /// write extends. It reads headers only, so a payload that no longer matches its header
    /// is still left for the read that asks for it.
    /// </summary>
    private ImmutableDictionary<string, OwnerRecords> LoadIndex()
    {
        var owners = ImmutableDictionary.CreateBuilder<string, OwnerRecords>(StringComparer.Ordinal);
        var active = Path.Combine(_root!, ActiveDirectoryName);
        foreach (var participant in _participants.Values)
        {
            var directory = Path.Combine(active, OwnerDirectoryName(participant.ExtensionId));
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var owner = OwnerRecords.Empty;
            foreach (var file in Directory.EnumerateFiles(directory, $"*{RecordExtension}"))
            {
                var header = ReadHeader(file)
                    ?? throw new ExtensionStateException(
                        $"Extension state record '{Path.GetFileName(file)}' of " +
                        $"'{participant.ExtensionId}' has no readable header.");
                if (header.Length < 0 || header.Length > _quotas.MaximumRecordBytes)
                {
                    throw new StateQuotaExceededException(
                        $"Extension '{participant.ExtensionId}' holds a record of " +
                        $"{header.Length} bytes which exceeds the maximum of " +
                        $"{_quotas.MaximumRecordBytes} bytes.");
                }

                owner = owner.With(
                    header.Key,
                    new StateEntry(header.ETag, header.Length, header.Sha256, null));
            }

            if (owner.Records.Count > 0)
            {
                EnsureOwnerQuota(participant.ExtensionId, owner);
                owners[participant.ExtensionId] = owner;
            }
        }

        if (owners.Count > _quotas.MaximumOwners)
        {
            throw new StateQuotaExceededException(
                $"Extension state holds {owners.Count} owners which exceeds the configured " +
                $"maximum of {_quotas.MaximumOwners}.");
        }

        return owners.ToImmutable();
    }

    private void LoadSequence()
    {
        var highWater = 0L;
        foreach (var owner in _owners.Values)
        {
            foreach (var entry in owner.Records.Values)
            {
                highWater = Math.Max(highWater, entry.ETag);
            }
        }

        var path = Path.Combine(_root!, SequenceFileName);
        PersistedSequence? persisted = null;
        if (File.Exists(path))
        {
            try
            {
                persisted = JsonSerializer.Deserialize<PersistedSequence>(File.ReadAllBytes(path));
            }
            catch (JsonException)
            {
                persisted = null;
            }
        }

        lock (_sequenceGate)
        {
            _sequenceNext = Math.Max(highWater + 1, persisted?.RecordCeiling + 1 ?? 1);
            _sequenceCeiling = _sequenceNext - 1;
            _checkpointNext = Math.Max(persisted?.CheckpointCeiling + 1 ?? 1, 1);
            _checkpointCeiling = _checkpointNext - 1;
        }
    }

    private long NextSequence()
    {
        lock (_sequenceGate)
        {
            if (_root is not null && _sequenceNext > _sequenceCeiling)
            {
                _sequenceCeiling = _sequenceNext + SequenceReservation;
                PersistSequence();
            }

            return _sequenceNext++;
        }
    }

    private long NextCheckpointId()
    {
        lock (_sequenceGate)
        {
            if (_root is not null && _checkpointNext > _checkpointCeiling)
            {
                _checkpointCeiling = _checkpointNext + SequenceReservation;
                PersistSequence();
            }

            return _checkpointNext++;
        }
    }

    private void PersistSequence() =>
        WriteFileAtomicAsync(
                Path.Combine(_root!, SequenceFileName),
                JsonSerializer.SerializeToUtf8Bytes(
                    new PersistedSequence(1, _sequenceCeiling, _checkpointCeiling)),
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    private void WriteParticipantDescriptors()
    {
        foreach (var participant in _participants.Values)
        {
            WriteParticipantDescriptorAsync(
                    Path.Combine(_root!, ActiveDirectoryName),
                    participant,
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    /// <summary>
    /// Upgrades persisted records through the declared migration path. It runs only when
    /// the descriptor scan proved a migration is required, so it is the one place that
    /// materializes the persisted record set and it reads that set exactly once.
    /// </summary>
    private void MigratePersistedSchemas()
    {
        var persisted = ReadPersistedSchemas();

        // Committing a migration replaces the whole active tree, so persisted state for an
        // extension this build does not activate has to travel with the checkpoint. Staging
        // quarantines it instead of activating it, and it is never deleted.
        var participants = _participants.Values
            .Select(participant => persisted.FirstOrDefault(source => string.Equals(
                    source.ExtensionId,
                    participant.ExtensionId,
                    StringComparison.Ordinal))
                ?? new StateCheckpointParticipant(
                    participant.ExtensionId,
                    participant.ExtensionVersion,
                    participant.SchemaName,
                    participant.SchemaVersion,
                    participant.Required,
                    []))
            .Concat(persisted.Where(source => !_participants.ContainsKey(source.ExtensionId)))
            .ToImmutableArray();
        var data = new StateCheckpointData(
            StateCheckpointData.CurrentManifestVersion,
            0,
            _clock.GetUtcNow(),
            participants);
        var staged = StageRestoreAsync(data, CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        CommitRestoreAsync(staged, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    private ImmutableArray<StateCheckpointParticipant> ReadPersistedSchemas() =>
        ReadParticipantSetAsync(Path.Combine(_root!, ActiveDirectoryName), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Fails the open when persisted state cannot be served by this build, and reports
    /// whether any persisted participant is older than the schema this build owns. It
    /// compares persisted participant descriptors, so it never reads a record payload.
    /// </summary>
    private bool EnsurePersistedSchemasAreSupported(
        ImmutableArray<StateParticipantDescriptor> persisted)
    {
        var migrate = false;
        foreach (var source in persisted)
        {
            if (!_participants.TryGetValue(source.ExtensionId, out var participant))
            {
                if (source.Required)
                {
                    throw new StateSchemaCompatibilityException(
                        $"Persisted state requires extension '{source.ExtensionId}' which is " +
                        "not active in this server.");
                }

                continue;
            }

            if (!string.Equals(source.SchemaName, participant.SchemaName, StringComparison.Ordinal))
            {
                throw new StateSchemaCompatibilityException(
                    $"Extension '{source.ExtensionId}' owns schema '{participant.SchemaName}' " +
                    $"but the persisted state declares '{source.SchemaName}'.");
            }

            if (source.SchemaVersion > participant.SchemaVersion)
            {
                throw new StateSchemaCompatibilityException(
                    $"Extension '{source.ExtensionId}' persisted schema version " +
                    $"{source.SchemaVersion} is newer than the supported version " +
                    $"{participant.SchemaVersion}.");
            }

            migrate |= source.SchemaVersion < participant.SchemaVersion;
        }

        return migrate;
    }

    /// <summary>
    /// Adopts version 1 records that predate the transactional layout so no state is
    /// stranded by an upgrade. A record that is already committed is skipped by its file
    /// name, so a reopen never reads the mirror it wrote. Adoption is one all-or-nothing
    /// admission per owner: every adoptable record is projected first and the aggregate
    /// record, byte, and owner-count quotas the resulting owner would hold are validated
    /// before the first record is persisted, so a refusal can never leave part of itself in
    /// the authoritative tree. It reports whether an adopted record was stamped below the
    /// schema this build owns and therefore still has to be migrated.
    /// </summary>
    private bool ImportCompatibilityRecords(ImmutableArray<StateParticipantDescriptor> persisted)
    {
        var migrate = false;
        foreach (var participant in _participants.Values)
        {
            var directory = Path.Combine(_root!, OwnerDirectoryName(participant.ExtensionId));
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var owner = _owners.TryGetValue(participant.ExtensionId, out var existing)
                ? existing
                : OwnerRecords.Empty;
            var adoptable = ScanAdoptableLegacyRecords(participant.ExtensionId, directory, owner);
            if (adoptable.Count == 0)
            {
                continue;
            }

            // The whole adoption is admitted or refused before anything is written, so a
            // refusal never leaves an over-quota tree for a later open to inherit.
            var projected = owner;
            foreach (var candidate in adoptable)
            {
                projected = projected.With(
                    candidate.Key,
                    new StateEntry(0, candidate.Length, string.Empty, null));
            }

            EnsureOwnerAdmitted(participant.ExtensionId);
            EnsureOwnerQuota(participant.ExtensionId, projected);

            var descriptor = ResolveImportDescriptor(persisted, participant);
            foreach (var candidate in adoptable)
            {
                var record = ExtensionStateStore.TryReadCompatibilityRecord(candidate.Path);
                if (record is null ||
                    !string.Equals(record.Value.Key, candidate.Key, StringComparison.Ordinal) ||
                    record.Value.Payload.LongLength != candidate.Length)
                {
                    throw new ExtensionStateException(
                        $"Version 1 extension state for '{participant.ExtensionId}' changed " +
                        "while it was being adopted.");
                }

                var entry = PersistAsync(
                        participant.ExtensionId,
                        descriptor,
                        record.Value.Key,
                        record.Value.Payload,
                        NextSequence(),
                        CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                owner = owner.With(record.Value.Key, entry);
                migrate |= descriptor.SchemaVersion < participant.SchemaVersion;
            }

            SetOwner(participant.ExtensionId, owner);
        }

        return migrate;
    }

    /// <summary>
    /// Projects every version 1 record one owner could adopt without persisting any of
    /// them. Each candidate is refused by file length before it is materialized, and one
    /// payload at a time is materialized to establish the key and length the adoption
    /// would add, so the scan is bounded by the record quota rather than by the size of the
    /// version 1 directory.
    /// </summary>
    private List<LegacyRecordCandidate> ScanAdoptableLegacyRecords(
        string ownerId,
        string directory,
        OwnerRecords owner)
    {
        var committed = owner.Records.Keys
            .Select(KeyFileName)
            .ToHashSet(StringComparer.Ordinal);
        var candidates = new List<LegacyRecordCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            if (committed.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                continue;
            }

            EnsureLegacyRecordIsAdoptable(ownerId, file);
            var record = ExtensionStateStore.TryReadCompatibilityRecord(file);
            if (record is null ||
                owner.Records.ContainsKey(record.Value.Key) ||
                !seen.Add(record.Value.Key))
            {
                continue;
            }

            try
            {
                ValidateKey(record.Value.Key);
            }
            catch (ArgumentException exception)
            {
                throw new ExtensionStateException(
                    $"Version 1 extension state for '{ownerId}' declares a " +
                    "key this store never writes.",
                    exception);
            }

            ValidateRecordSize(record.Value.Payload.LongLength, long.MaxValue);
            candidates.Add(new LegacyRecordCandidate(
                file,
                record.Value.Key,
                record.Value.Payload.LongLength));
        }

        return candidates;
    }

    /// <summary>
    /// A version 1 envelope holds its payload as base64 inside JSON, so a file larger than
    /// twice the record quota cannot hold a record this store may keep. Refusing it by
    /// length keeps adoption bounded by the quotas a write obeys instead of materializing
    /// whatever a storage directory happens to contain.
    /// </summary>
    private void EnsureLegacyRecordIsAdoptable(string ownerId, string path)
    {
        var limit = MaximumLegacyRecordBytes(_quotas);
        var length = new FileInfo(path).Length;
        if (length > limit)
        {
            throw new StateQuotaExceededException(
                $"Version 1 extension state record '{Path.GetFileName(path)}' for '{ownerId}' " +
                $"is {length} bytes and cannot hold a record within the " +
                $"{_quotas.MaximumRecordBytes} byte maximum.");
        }
    }

    private static long MaximumLegacyRecordBytes(StateStoreQuotas quotas) =>
        quotas.MaximumRecordBytes > (long.MaxValue - 1024) / 2
            ? long.MaxValue
            : (quotas.MaximumRecordBytes * 2) + 1024;

    private sealed record LegacyRecordCandidate(string Path, string Key, long Length);

    /// <summary>
    /// An adopted version 1 record was written by the build that owned the persisted schema
    /// version, so it is imported at that version and migrated with the rest of the tree
    /// rather than being declared current without ever running a migration. State that
    /// predates the transactional layout has no persisted descriptor at all, so it is
    /// adopted at the first schema version and travels through the complete migration path.
    /// </summary>
    private static StateParticipantDescriptor ResolveImportDescriptor(
        ImmutableArray<StateParticipantDescriptor> persisted,
        StateParticipantDescriptor participant)
    {
        var source = persisted.FirstOrDefault(candidate => string.Equals(
            candidate.ExtensionId,
            participant.ExtensionId,
            StringComparison.Ordinal));
        var version = source is null ? LegacySchemaVersion : source.SchemaVersion;
        if (version >= participant.SchemaVersion)
        {
            return participant;
        }

        return new StateParticipantDescriptor(
            participant.ExtensionId,
            source?.ExtensionVersion ?? participant.ExtensionVersion,
            participant.SchemaName,
            version,
            participant.Required);
    }

    private void SetOwner(string ownerId, OwnerRecords records) =>
        ImmutableInterlocked.Update(
            ref _owners,
            (owners, change) => owners.SetItem(change.OwnerId, change.Records),
            (OwnerId: ownerId, Records: records));

    private static void ApplyJournal(string root, RestoreJournal journal)
    {
        ValidateRestoreJournal(journal);
        var active = Path.Combine(root, ActiveDirectoryName);
        var staging = Path.Combine(root, journal.StagingDirectory);
        var stagedActive = Path.Combine(staging, ActiveDirectoryName);
        var trash = Path.Combine(root, journal.TrashDirectory);
        var quarantine = Path.Combine(
            root,
            QuarantineDirectoryName,
            journal.RestoreId.ToString("x"));

        // Only owners this restore replaced, published, or quarantined own a version 1
        // mirror that has to be rebuilt. Collecting them before the moves keeps a replay
        // after an interrupted commit exact, and leaves every other legacy directory
        // alone. The set is collected from both sides of every move so a replay that
        // finds the moves already applied still retires the mirrors they invalidated.
        var owned = new HashSet<string>(StringComparer.Ordinal);
        CollectOwnerDirectories(active, owned);
        CollectOwnerDirectories(stagedActive, owned);
        CollectOwnerDirectories(trash, owned);
        CollectOwnerDirectories(Path.Combine(staging, QuarantineDirectoryName), owned);
        CollectOwnerDirectories(quarantine, owned);
        if (Directory.Exists(stagedActive))
        {
            if (Directory.Exists(active))
            {
                if (Directory.Exists(trash))
                {
                    Directory.Delete(trash, recursive: true);
                }

                Directory.Move(active, trash);
            }

            Directory.Move(stagedActive, active);
        }

        var stagedQuarantine = Path.Combine(staging, QuarantineDirectoryName);
        if (Directory.Exists(stagedQuarantine))
        {
            var destination = quarantine;
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(stagedQuarantine, destination);
        }

        RebuildCompatibilityMirror(root, owned);
        var journalPath = Path.Combine(root, RestoreJournalFileName);
        if (File.Exists(journalPath))
        {
            File.Delete(journalPath);
        }

        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        if (Directory.Exists(trash))
        {
            Directory.Delete(trash, recursive: true);
        }
    }

    /// <summary>
    /// Republishes the version 1 mirror of every owner a restore touched. The mirror is a
    /// projection of the transactional tree, so a mirror this store did not project is
    /// version 1 state of an extension that never joined the tree and is preserved
    /// untouched rather than deleted.
    /// </summary>
    private static void RebuildCompatibilityMirror(string root, IReadOnlySet<string> owned)
    {
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (IsOwnerDirectoryName(name) && owned.Contains(name))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        var active = Path.Combine(root, ActiveDirectoryName);
        if (!Directory.Exists(active))
        {
            return;
        }

        foreach (var ownerDirectory in Directory.EnumerateDirectories(active))
        {
            var mirror = Path.Combine(root, Path.GetFileName(ownerDirectory));
            foreach (var file in Directory.EnumerateFiles(ownerDirectory, $"*{RecordExtension}"))
            {
                var record = ReadRecordFileAsync(file, long.MaxValue, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                Directory.CreateDirectory(mirror);
                File.WriteAllBytes(
                    Path.Combine(
                        mirror,
                        $"{KeyFileName(record.Header.Key)}.json"),
                    ExtensionStateStore.CreateCompatibilityEnvelope(
                        record.Header.Key,
                        record.Payload));
            }
        }
    }

    private static void CollectOwnerDirectories(string directory, HashSet<string> owned)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var owner in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(owner);
            if (IsOwnerDirectoryName(name))
            {
                owned.Add(name);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(
                Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private async ValueTask<Releaser> LockOwnerAsync(string ownerId, CancellationToken token)
    {
        var stripe = OwnerStripe(ownerId);
        await _stripes[stripe].WaitAsync(token);
        return new Releaser(_stripes, [stripe]);
    }

    private async ValueTask<Releaser> LockAllAsync(CancellationToken token)
    {
        var acquired = new List<int>(LockStripeCount);
        try
        {
            for (var index = 0; index < LockStripeCount; index++)
            {
                await _stripes[index].WaitAsync(token);
                acquired.Add(index);
            }
        }
        catch
        {
            new Releaser(_stripes, [.. acquired]).Dispose();
            throw;
        }

        return new Releaser(_stripes, [.. acquired]);
    }

    private static int OwnerStripe(string ownerId) =>
        (int)(BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId)), 0) %
              LockStripeCount);

    private static string OwnerDirectoryName(string ownerId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId)));

    private static string KeyFileName(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string GetRecordPath(string activeDirectory, string ownerId, string key) =>
        Path.Combine(
            activeDirectory,
            OwnerDirectoryName(ownerId),
            $"{KeyFileName(key)}{RecordExtension}");

    private static async ValueTask WriteParticipantDescriptorAsync(
        string activeDirectory,
        StateParticipantDescriptor participant,
        CancellationToken token) =>
        await WriteFileAtomicAsync(
            Path.Combine(
                activeDirectory,
                OwnerDirectoryName(participant.ExtensionId),
                ParticipantFileName),
            JsonSerializer.SerializeToUtf8Bytes(new PersistedParticipant(
                participant.ExtensionId,
                participant.ExtensionVersion,
                participant.SchemaName,
                participant.SchemaVersion,
                participant.Required)),
            token);

    private static RecordHeader? ReadHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ParseHeader(stream, path, out _, out _);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static RecordHeader ParseHeader(
        Stream stream,
        string path,
        out byte[] prefix,
        out int payloadOffset)
    {
        prefix = new byte[MaximumHeaderBytes];
        var length = 0;
        var headerEnd = -1;
        var newlines = 0;
        while (length < prefix.Length)
        {
            var read = stream.Read(prefix, length, prefix.Length - length);
            if (read == 0)
            {
                break;
            }

            var scan = length;
            length += read;
            for (var index = scan; index < length && headerEnd < 0; index++)
            {
                if (prefix[index] == (byte)'\n' && ++newlines == 2)
                {
                    headerEnd = index;
                }
            }

            if (headerEnd >= 0)
            {
                break;
            }
        }

        if (headerEnd < 0)
        {
            throw new ExtensionStateException(
                $"Extension state record '{Path.GetFileName(path)}' has no readable header.");
        }

        var firstNewline = Array.IndexOf(prefix, (byte)'\n', 0, headerEnd);
        if (!Encoding.UTF8.GetString(prefix, 0, firstNewline).Equals(RecordMagic, StringComparison.Ordinal))
        {
            throw new ExtensionStateException(
                $"Extension state record '{Path.GetFileName(path)}' is not a supported format.");
        }

        var header = JsonSerializer.Deserialize<RecordHeader>(
                prefix.AsSpan(firstNewline + 1, headerEnd - firstNewline - 1))
            ?? throw new ExtensionStateException(
                $"Extension state record '{Path.GetFileName(path)}' has an empty header.");
        payloadOffset = headerEnd + 1;
        prefix = prefix[..length];
        return header;
    }

    private static async ValueTask<(RecordHeader Header, byte[] Payload)> ReadRecordFileAsync(
        string path,
        long maximumBytes,
        CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = ParseHeader(stream, path, out var prefix, out var payloadOffset);
            if (header.Length > maximumBytes)
            {
                throw new CapabilityStreamLimitExceededException(header.Length, maximumBytes);
            }

            if (header.Length < 0 || header.Length != stream.Length - payloadOffset)
            {
                throw new ExtensionStateException(
                    $"Extension state record '{header.Key}' has an inconsistent length.");
            }

            var payload = new byte[header.Length];
            StatePayloadInstrumentation.Materialized(payload.LongLength);
            var carried = (int)Math.Min(prefix.Length - payloadOffset, payload.LongLength);
            prefix.AsSpan(payloadOffset, carried).CopyTo(payload);
            var offset = carried;
            while (offset < payload.Length)
            {
                var read = await stream.ReadAsync(payload.AsMemory(offset), token);
                if (read == 0)
                {
                    throw new ExtensionStateException(
                        $"Extension state record '{header.Key}' ended before its declared length.");
                }

                offset += read;
            }

            if (!string.Equals(
                    Convert.ToHexStringLower(SHA256.HashData(payload)),
                    header.Sha256,
                    StringComparison.Ordinal))
            {
                throw new ExtensionStateException(
                    $"Extension state record '{header.Key}' failed integrity validation.");
            }

            return (header, payload);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
            FormatException)
        {
            throw new ExtensionStateException(
                "Extension state could not be read or validated.",
                exception);
        }
    }

    private static async ValueTask WriteRecordFileAsync(
        string path,
        RecordHeader header,
        byte[] payload,
        CancellationToken token)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes($"{RecordMagic}\n"), token);
                await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(header), token);
                await stream.WriteAsync("\n"u8.ToArray(), token);
                await stream.WriteAsync(payload, token);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async ValueTask WriteFileAtomicAsync(
        string path,
        byte[] content,
        CancellationToken token)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, token);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private readonly struct Releaser(SemaphoreSlim[] stripes, int[] acquired) : IDisposable
    {
        public void Dispose()
        {
            for (var index = acquired.Length - 1; index >= 0; index--)
            {
                stripes[acquired[index]].Release();
            }
        }
    }

    private sealed record RecordHeader(
        string Key,
        long ETag,
        string SchemaName,
        int SchemaVersion,
        long Length,
        string Sha256);

    private sealed record PersistedParticipant(
        string ExtensionId,
        string ExtensionVersion,
        string SchemaName,
        int SchemaVersion,
        bool Required);

    private sealed record PersistedSequence(int Version, long RecordCeiling, long CheckpointCeiling);

    private sealed record RestoreJournal(
        int Version,
        string StagingDirectory,
        string TrashDirectory,
        long RestoreId);

    private sealed record WriteJournal(
        int Version,
        string StagingDirectory,
        string OwnerDirectory,
        long BatchId,
        IReadOnlyList<string>? Records);
}
