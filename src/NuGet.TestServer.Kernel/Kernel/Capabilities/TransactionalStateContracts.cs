using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NuGet.TestServer.Kernel.Capabilities;

internal sealed record StateSchemaMigration(int FromVersion, int ToVersion, Func<byte[], byte[]> Transform);

/// <summary>
/// A deterministic instrumentation seam used by tests to observe every whole record
/// payload the state layer materializes, so a path that must stay bounded by a record
/// header or a streaming buffer can be proven bounded. It is never set by the server,
/// and it is scoped to one asynchronous flow so parallel hosts never observe each
/// other.
/// </summary>
internal static class StatePayloadInstrumentation
{
    private static readonly AsyncLocal<Action<long>?> Observer = new();

    internal static Action<long>? Current
    {
        get => Observer.Value;
        set => Observer.Value = value;
    }

    internal static void Materialized(long length) => Observer.Value?.Invoke(length);
}

/// <summary>
/// The named points where a durable batch can be interrupted. Tests inject a deterministic
/// fault at one of these points to prove the batch is all-or-nothing, and that a batch the
/// commit journal made authoritative is completed by recovery no matter where it stopped.
/// </summary>
internal enum StateWriteFailPoint
{
    AfterStage,
    AfterCommitJournal,
    BeforePublishRecord,
    BeforeMirrorRefresh,
    AfterMirrorRefresh,
    BeforeDeleteCommitJournal,
    AfterDeleteCommitJournal,
    BeforeDeleteAuthoritativeRemoval,
    AfterDeleteAuthoritativeRemoval,
    BeforeDeleteMirrorRemoval,
    AfterDeleteMirrorRemoval
}

internal sealed record StateParticipantDescriptor(
    string ExtensionId,
    string ExtensionVersion,
    string SchemaName,
    int SchemaVersion,
    bool Required = false,
    ImmutableArray<StateSchemaMigration> Migrations = default)
{
    public ImmutableArray<StateSchemaMigration> OrderedMigrations =>
        Migrations.IsDefault ? [] : [.. Migrations.OrderBy(migration => migration.FromVersion)];

    public StateParticipantDescriptor Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ExtensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ExtensionVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(SchemaName);
        if (SchemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SchemaVersion),
                "Schema versions start at 1.");
        }

        var previous = 0;
        foreach (var migration in OrderedMigrations)
        {
            if (migration.ToVersion != migration.FromVersion + 1 ||
                migration.FromVersion < 1 ||
                migration.ToVersion > SchemaVersion ||
                migration.FromVersion <= previous && previous != 0)
            {
                throw new ArgumentException(
                    $"Migration {migration.FromVersion}->{migration.ToVersion} for schema " +
                    $"'{SchemaName}' is not a contiguous ascending step.",
                    nameof(Migrations));
            }

            previous = migration.FromVersion;
        }

        return this;
    }

    /// <summary>
    /// Returns the complete ordered migration chain from <paramref name="fromVersion"/> to
    /// the registered version, or <c>null</c> when the chain is incomplete.
    /// </summary>
    public ImmutableArray<StateSchemaMigration>? ResolveMigrationPath(int fromVersion)
    {
        if (fromVersion == SchemaVersion)
        {
            return [];
        }

        if (fromVersion < 1 || fromVersion > SchemaVersion)
        {
            return null;
        }

        var path = ImmutableArray.CreateBuilder<StateSchemaMigration>();
        var current = fromVersion;
        foreach (var migration in OrderedMigrations)
        {
            if (migration.FromVersion != current)
            {
                continue;
            }

            path.Add(migration);
            current = migration.ToVersion;
        }

        return current == SchemaVersion ? path.ToImmutable() : null;
    }
}

internal sealed record StateStoreQuotas(
    int MaximumKeyLength = 128,
    long MaximumRecordBytes = 64L * 1024 * 1024,
    int MaximumRecordsPerOwner = 256,
    long MaximumOwnerBytes = 256L * 1024 * 1024,
    int MaximumOwners = 64)
{
    public StateStoreQuotas Validate()
    {
        if (MaximumKeyLength is < 1 or > 128 ||
            MaximumRecordBytes <= 0 ||
            MaximumRecordsPerOwner <= 0 ||
            MaximumOwnerBytes < MaximumRecordBytes ||
            MaximumOwners <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumOwnerBytes),
                "Extension state quotas are invalid.");
        }

        return this;
    }
}

internal sealed record StateRecord(
    string OwnerId,
    string Key,
    byte[] Value,
    long ETag,
    string SchemaName,
    int SchemaVersion);

internal sealed record StateEdit(
    string Key,
    long? ExpectedETag,
    byte[] Value,
    bool RequireAbsent = false);

internal sealed record StateCheckpointRecord(string Key, byte[] Value, long ETag);

internal sealed record StateCheckpointParticipant(
    string ExtensionId,
    string ExtensionVersion,
    string SchemaName,
    int SchemaVersion,
    bool Required,
    ImmutableArray<StateCheckpointRecord> Records)
{
    public string ComputeIntegrity()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(
            $"{ExtensionId}\n{ExtensionVersion}\n{SchemaName}\n{SchemaVersion}\n{Required}\n"));
        foreach (var record in Records.OrderBy(record => record.Key, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes($"{record.Key}\n{record.Value.LongLength}\n"));
            hash.AppendData(record.Value);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

internal sealed record StateCheckpointData(
    int ManifestVersion,
    long CheckpointId,
    DateTimeOffset CreatedAt,
    ImmutableArray<StateCheckpointParticipant> Participants)
{
    internal const int CurrentManifestVersion = 2;
}

/// <summary>
/// One participant's identity, record count, and integrity value computed by streaming
/// its committed record files. <see cref="Integrity"/> is byte-identical to
/// <see cref="StateCheckpointParticipant.ComputeIntegrity"/> for the same content, so a
/// bounded capture and a materialized checkpoint always describe the same state.
/// </summary>
internal sealed record StateParticipantSummary(
    string ExtensionId,
    string ExtensionVersion,
    string SchemaName,
    int SchemaVersion,
    bool Required,
    int RecordCount,
    long TotalBytes,
    long HighWaterETag,
    string Integrity);

internal sealed record StateRestoreReport(
    ImmutableArray<string> QuarantinedExtensions,
    ImmutableArray<string> Warnings);


/// <summary>
/// A frozen point-in-time view of every participant's state. Durable checkpoints keep an
/// isolated copy of the committed record tree until the lease expires or the checkpoint is
/// disposed, so later mutation can never change what a checkpoint exports.
/// </summary>
internal sealed class StateCheckpoint : IDisposable
{
    private readonly Action<StateCheckpoint> _release;
    private int _disposed;

    internal StateCheckpoint(
        long checkpointId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        ImmutableArray<StateCheckpointParticipant> frozenParticipants,
        string? frozenDirectory,
        Action<StateCheckpoint> release)
    {
        CheckpointId = checkpointId;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        FrozenParticipants = frozenParticipants;
        FrozenDirectory = frozenDirectory;
        _release = release;
    }

    internal long CheckpointId { get; }

    internal DateTimeOffset CreatedAt { get; }

    internal DateTimeOffset ExpiresAt { get; }

    internal ImmutableArray<StateCheckpointParticipant> FrozenParticipants { get; }

    internal string? FrozenDirectory { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _release(this);
        }
    }
}

/// <summary>
/// Restore content that has been validated and fully materialized but is not authoritative
/// yet. Committing it is a single journalled step.
/// </summary>
internal sealed class StagedStateRestore : IDisposable
{
    private int _completed;

    internal StagedStateRestore(
        long restoreId,
        StateCheckpointData data,
        string? stagingDirectory,
        ImmutableDictionary<string, OwnerRecords> preparedOwners,
        StateRestoreReport report,
        Action<StagedStateRestore> release)
    {
        RestoreId = restoreId;
        Data = data;
        StagingDirectory = stagingDirectory;
        PreparedOwners = preparedOwners;
        Report = report;
        Release = release;
    }

    internal long RestoreId { get; }

    internal StateCheckpointData Data { get; }

    internal string? StagingDirectory { get; }

    internal ImmutableDictionary<string, OwnerRecords> PreparedOwners { get; }

    internal StateRestoreReport Report { get; }

    internal Action<StagedStateRestore> Release { get; }

    internal bool IsCompleted => Volatile.Read(ref _completed) != 0;

    internal void MarkCompleted()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            throw new InvalidOperationException("The staged restore was already completed.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            Release(this);
        }
    }
}

internal sealed record OwnerRecords(
    ImmutableDictionary<string, StateEntry> Records,
    long TotalBytes)
{
    internal static OwnerRecords Empty { get; } =
        new(ImmutableDictionary<string, StateEntry>.Empty.WithComparers(StringComparer.Ordinal), 0);

    internal OwnerRecords With(string key, StateEntry entry)
    {
        var previous = Records.TryGetValue(key, out var existing) ? existing.Length : 0;
        return new OwnerRecords(
            Records.SetItem(key, entry),
            TotalBytes - previous + entry.Length);
    }

    internal OwnerRecords Without(string key) =>
        Records.TryGetValue(key, out var existing)
            ? new OwnerRecords(Records.Remove(key), TotalBytes - existing.Length)
            : this;
}

internal sealed record StateEntry(long ETag, long Length, string Sha256, byte[]? Value);
