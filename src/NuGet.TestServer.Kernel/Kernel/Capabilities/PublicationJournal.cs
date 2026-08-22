using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace NuGet.TestServer.Kernel.Capabilities;

/// <summary>The phase of one journalled publication.</summary>
internal enum PublicationJournalPhase
{
    /// <summary>The kernel recorded the intent but has not yet learned the outcome.</summary>
    Pending,

    /// <summary>The publication pipeline returned a terminal outcome.</summary>
    Resolved,

    /// <summary>Staged transition, extension state, and audit were all applied.</summary>
    Committed
}

/// <summary>
/// One journalled publication. It is the kernel's single source of truth for what a
/// retry must observe, and for what recovery must finish after an interrupted publish.
/// </summary>
internal sealed record PublicationJournalEntry(
    string EntryId,
    string OwnerId,
    string IdempotencyKey,
    string StagedContentId,
    string? StagedSymbolContentId,
    string StateKey,
    long? ExpectedStateToken,
    string PackageId,
    string PackageVersion,
    string ContentSha256,
    PublicationJournalPhase Phase,
    string Outcome,
    long? StateToken,
    string? FailureDetail,
    string StatePayload,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The kernel-owned recovery journal that makes staged publication atomic in the only
/// sense the kernel can guarantee across two independent transactional systems: the
/// journal is written before the package transaction begins, the outcome is recorded
/// before any dependent state moves, and recovery deterministically finishes or fails
/// closed on every entry it finds. A retry with the same idempotency key replays the
/// recorded result instead of publishing twice, and no crash can leave a published
/// package with a still-promotable staging record.
///
/// It is host-scoped: durable under the configured storage directory and in memory for
/// an embedded host, exactly like the staged content it coordinates.
/// </summary>
internal sealed class PublicationJournal : IDisposable
{
    internal const string FileName = "publication-journal.json";

    private readonly string? _path;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableDictionary<string, PublicationJournalEntry> _entries =
        ImmutableDictionary<string, PublicationJournalEntry>.Empty
            .WithComparers(StringComparer.Ordinal);
    private int _disposed;

    public PublicationJournal(string? storageDirectory, TimeProvider? timeProvider = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
        if (storageDirectory is null)
        {
            _path = null;
            return;
        }

        var root = Path.Combine(
            Path.GetFullPath(storageDirectory),
            StagedContentStore.DirectoryName);
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, FileName);
        _entries = Load();
    }

    internal ImmutableArray<PublicationJournalEntry> Entries =>
        [.. _entries.Values.OrderBy(entry => entry.EntryId, StringComparer.Ordinal)];

    /// <summary>The key a retry is deduplicated by: owner plus idempotency key.</summary>
    internal static string CreateKey(string ownerId, string idempotencyKey) =>
        ownerId + "\n" + idempotencyKey;

    public PublicationJournalEntry? Find(string ownerId, string idempotencyKey) =>
        _entries.GetValueOrDefault(CreateKey(ownerId, idempotencyKey));

    /// <summary>
    /// Records publication intent. Returns the existing entry when the same owner and
    /// idempotency key were already journalled, so callers can replay instead of
    /// republishing.
    /// </summary>
    public async ValueTask<(PublicationJournalEntry Entry, bool Created)> BeginAsync(
        PublicationJournalEntry entry,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(token);
        try
        {
            var key = CreateKey(entry.OwnerId, entry.IdempotencyKey);
            if (_entries.TryGetValue(key, out var existing))
            {
                return (existing, false);
            }

            var now = _clock.GetUtcNow();
            var created = entry with
            {
                Phase = PublicationJournalPhase.Pending,
                CreatedAt = now,
                UpdatedAt = now
            };
            _entries = _entries.SetItem(key, created);
            Persist();
            return (created, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<PublicationJournalEntry> ResolveAsync(
        PublicationJournalEntry entry,
        string outcome,
        string? failureDetail,
        CancellationToken token) =>
        UpdateAsync(
            entry,
            current => current with
            {
                Phase = PublicationJournalPhase.Resolved,
                Outcome = outcome,
                FailureDetail = failureDetail
            },
            token);

    public ValueTask<PublicationJournalEntry> CommitAsync(
        PublicationJournalEntry entry,
        long? stateToken,
        CancellationToken token) =>
        UpdateAsync(
            entry,
            current => current with
            {
                Phase = PublicationJournalPhase.Committed,
                StateToken = stateToken
            },
            token);

    /// <summary>
    /// Drops an entry that never reached the publication pipeline, so a rejected
    /// pre-condition never consumes an idempotency key.
    /// </summary>
    public async ValueTask AbortAsync(PublicationJournalEntry entry, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(token);
        try
        {
            _entries = _entries.Remove(CreateKey(entry.OwnerId, entry.IdempotencyKey));
            Persist();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Entries a restarted host must finish or fail closed.</summary>
    public ImmutableArray<PublicationJournalEntry> ReadUnfinished() =>
        [.. _entries.Values
            .Where(entry => entry.Phase != PublicationJournalPhase.Committed)
            .OrderBy(entry => entry.EntryId, StringComparer.Ordinal)];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _gate.Dispose();
        }
    }

    private async ValueTask<PublicationJournalEntry> UpdateAsync(
        PublicationJournalEntry entry,
        Func<PublicationJournalEntry, PublicationJournalEntry> update,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(token);
        try
        {
            var key = CreateKey(entry.OwnerId, entry.IdempotencyKey);
            var current = _entries.GetValueOrDefault(key) ?? entry;
            var updated = update(current) with { UpdatedAt = _clock.GetUtcNow() };
            _entries = _entries.SetItem(key, updated);
            Persist();
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ImmutableDictionary<string, PublicationJournalEntry> Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return _entries;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedEntry[]>(
                File.ReadAllBytes(_path)) ?? [];
            var builder = ImmutableDictionary.CreateBuilder<string, PublicationJournalEntry>(
                StringComparer.Ordinal);
            foreach (var entry in persisted)
            {
                var value = new PublicationJournalEntry(
                    entry.EntryId,
                    entry.OwnerId,
                    entry.IdempotencyKey,
                    entry.StagedContentId,
                    entry.StagedSymbolContentId,
                    entry.StateKey,
                    entry.ExpectedStateToken,
                    entry.PackageId,
                    entry.PackageVersion,
                    entry.ContentSha256,
                    Enum.Parse<PublicationJournalPhase>(entry.Phase),
                    entry.Outcome,
                    entry.StateToken,
                    entry.FailureDetail,
                    entry.StatePayload,
                    DateTimeOffset.Parse(entry.CreatedAt, CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(entry.UpdatedAt, CultureInfo.InvariantCulture));
                builder[CreateKey(value.OwnerId, value.IdempotencyKey)] = value;
            }

            return builder.ToImmutable();
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or ArgumentException or IOException)
        {
            throw new InvalidDataException(
                "The staged publication journal is unreadable; recovery cannot continue safely.",
                exception);
        }
    }

    private void Persist()
    {
        if (_path is null)
        {
            return;
        }

        var persisted = _entries.Values
            .OrderBy(entry => entry.EntryId, StringComparer.Ordinal)
            .Select(entry => new PersistedEntry(
                entry.EntryId,
                entry.OwnerId,
                entry.IdempotencyKey,
                entry.StagedContentId,
                entry.StagedSymbolContentId,
                entry.StateKey,
                entry.ExpectedStateToken,
                entry.PackageId,
                entry.PackageVersion,
                entry.ContentSha256,
                entry.Phase.ToString(),
                entry.Outcome,
                entry.StateToken,
                entry.FailureDetail,
                entry.StatePayload,
                entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                entry.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)))
            .ToArray();
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(persisted));
        File.Move(temporary, _path, overwrite: true);
    }

    private sealed record PersistedEntry(
        string EntryId,
        string OwnerId,
        string IdempotencyKey,
        string StagedContentId,
        string? StagedSymbolContentId,
        string StateKey,
        long? ExpectedStateToken,
        string PackageId,
        string PackageVersion,
        string ContentSha256,
        string Phase,
        string Outcome,
        long? StateToken,
        string? FailureDetail,
        string StatePayload,
        string CreatedAt,
        string UpdatedAt);
}
