using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Kernel.Capabilities;

/// <summary>The lifecycle state of one staged content record.</summary>
internal enum StagedContentState
{
    Staged,
    Promoted,
    Released
}

/// <summary>
/// One kernel-owned staged content record. It binds the content to a host instance and
/// an owning extension, carries the lease expiry, and records the integrity the kernel
/// computed while streaming the content in.
/// </summary>
internal sealed record StagedContentRecord(
    string ContentId,
    string HostInstanceId,
    string OwnerId,
    string ContentType,
    long ContentLength,
    string ContentSha256,
    string? PackageId,
    string? PackageVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    StagedContentState State);

internal sealed record StagedContentQuotas(
    long MaximumContentBytes = 256L * 1024 * 1024,
    int MaximumRecordsPerOwner = 256,
    long MaximumOwnerBytes = 1024L * 1024 * 1024,
    TimeSpan? DefaultLease = null)
{
    public TimeSpan Lease => DefaultLease ?? TimeSpan.FromHours(24);

    public StagedContentQuotas Validate()
    {
        if (MaximumContentBytes <= 0 ||
            MaximumRecordsPerOwner <= 0 ||
            MaximumOwnerBytes < MaximumContentBytes ||
            Lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumOwnerBytes),
                "Staged content quotas are invalid.");
        }

        return this;
    }
}

internal enum StagedContentWriteStatus
{
    Succeeded,
    QuotaExceeded,
    ContentTooLarge
}

internal sealed record StagedContentWriteOutput(
    StagedContentWriteStatus Status,
    StagedContentRecord? Record,
    string? Detail);

/// <summary>
/// The kernel's host-scoped staged content store. Content is written under the host's
/// storage directory when one is configured and kept in memory otherwise, so an
/// embedded host stays deterministic and network-independent while a configured host
/// survives restart. Every record is bound to the host instance and the owning
/// extension, leased, quota-checked, and removable; nothing is process-static.
/// </summary>
internal sealed class StagedContentStore : IDisposable
{
    internal const string DirectoryName = "staged-content";
    private const string IndexFileName = "index.json";
    private const string ContentExtension = ".bin";
    private const string StagingExtension = ".staging";

    private readonly string? _root;
    private readonly string _hostInstanceId;
    private readonly StagedContentQuotas _quotas;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte[]> _memory = new(StringComparer.Ordinal);
    private ImmutableDictionary<string, StagedContentRecord> _records =
        ImmutableDictionary<string, StagedContentRecord>.Empty.WithComparers(StringComparer.Ordinal);
    private int _disposed;

    public StagedContentStore(
        string? storageDirectory,
        string hostInstanceId,
        StagedContentQuotas? quotas = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostInstanceId);
        _hostInstanceId = hostInstanceId;
        _quotas = (quotas ?? new StagedContentQuotas()).Validate();
        _clock = timeProvider ?? TimeProvider.System;
        _root = storageDirectory is null
            ? null
            : Path.Combine(Path.GetFullPath(storageDirectory), DirectoryName);
        if (_root is null)
        {
            return;
        }

        Directory.CreateDirectory(_root);
        _records = LoadIndex();
        RemoveOrphanedContentFiles();
    }

    internal bool IsDurable => _root is not null;

    internal ImmutableArray<StagedContentRecord> Records =>
        [.. _records.Values.OrderBy(record => record.ContentId, StringComparer.Ordinal)];

    /// <summary>
    /// Streams content in under the declared limit, computes its integrity, and stores
    /// it. Nothing is retained when a quota or limit rejects the write.
    /// </summary>
    public async ValueTask<StagedContentWriteOutput> WriteAsync(
        string ownerId,
        Stream content,
        string contentType,
        long maximumBytes,
        string? packageId,
        string? packageVersion,
        CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        var limit = Math.Min(
            maximumBytes <= 0 ? _quotas.MaximumContentBytes : maximumBytes,
            _quotas.MaximumContentBytes);
        var contentId = Guid.NewGuid().ToString("N");
        var now = _clock.GetUtcNow();
        var temporaryPath = _root is null ? null : Path.Combine(_root, contentId + StagingExtension);

        long length;
        string sha256;
        byte[]? buffered = null;
        Stream sink = temporaryPath is null
            ? new MemoryStream()
            : new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            length = 0;
            while (true)
            {
                var read = await content.ReadAsync(buffer, token);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (length > limit)
                {
                    await sink.DisposeAsync();
                    sink = Stream.Null;
                    Discard(temporaryPath);
                    return new StagedContentWriteOutput(
                        StagedContentWriteStatus.ContentTooLarge,
                        null,
                        "Staged content exceeds the declared limit.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await sink.WriteAsync(buffer.AsMemory(0, read), token);
            }

            sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (sink is MemoryStream memory)
            {
                buffered = memory.ToArray();
            }
        }
        catch
        {
            await sink.DisposeAsync();
            Discard(temporaryPath);
            throw;
        }
        finally
        {
            if (sink != Stream.Null)
            {
                await sink.DisposeAsync();
            }
        }

        await _gate.WaitAsync(token);
        try
        {
            var reclaimed = ReclaimExpiredCore(_clock.GetUtcNow());
            if (reclaimed > 0)
            {
                PersistIndex();
            }
            var owned = _records.Values
                .Where(record =>
                    string.Equals(record.OwnerId, ownerId, StringComparison.Ordinal) &&
                    record.State == StagedContentState.Staged)
                .ToArray();
            if (owned.Length >= _quotas.MaximumRecordsPerOwner ||
                owned.Sum(record => record.ContentLength) + length > _quotas.MaximumOwnerBytes)
            {
                Discard(temporaryPath);
                return new StagedContentWriteOutput(
                    StagedContentWriteStatus.QuotaExceeded,
                    null,
                    "The extension exceeded its staged content quota.");
            }

            var record = new StagedContentRecord(
                contentId,
                _hostInstanceId,
                ownerId,
                contentType,
                length,
                sha256,
                packageId,
                packageVersion,
                now,
                now + _quotas.Lease,
                StagedContentState.Staged);
            if (temporaryPath is not null)
            {
                File.Move(temporaryPath, ContentPath(contentId), overwrite: true);
            }
            else if (buffered is not null)
            {
                _memory[contentId] = buffered;
            }

            _records = _records.SetItem(contentId, record);
            PersistIndex();
            return new StagedContentWriteOutput(StagedContentWriteStatus.Succeeded, record, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Resolves a staged record for the extension that staged it. Durable stores adopt
    /// records written by an earlier host instance of the same storage directory;
    /// in-memory stores are private to one host instance by construction, so ownership
    /// is always the extension identity.
    /// </summary>
    public StagedContentRecord? Find(string ownerId, string contentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (string.IsNullOrWhiteSpace(contentId) ||
            !_records.TryGetValue(contentId, out var record) ||
            !string.Equals(record.OwnerId, ownerId, StringComparison.Ordinal))
        {
            return null;
        }

        return record;
    }

    public bool IsExpired(StagedContentRecord record) => _clock.GetUtcNow() >= record.ExpiresAt;

    /// <summary>Opens staged content as a bounded, forward-only stream.</summary>
    public Stream Open(StagedContentRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_root is null)
        {
            return _memory.TryGetValue(record.ContentId, out var bytes)
                ? new MemoryStream(bytes, writable: false)
                : throw new InvalidOperationException(
                    $"Staged content '{record.ContentId}' has no readable payload.");
        }

        return new FileStream(
            ContentPath(record.ContentId),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
    }

    /// <summary>
    /// Records the identity the kernel extracted from staged content. Only kernel
    /// parsing supplies it; an extension can never rewrite staged identity.
    /// </summary>
    public async ValueTask AnnotateAsync(StagedContentRecord record, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(token);
        try
        {
            if (_records.ContainsKey(record.ContentId))
            {
                _records = _records.SetItem(record.ContentId, record);
                PersistIndex();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Moves a staged record to a terminal state and frees its bytes. Returns
    /// <c>false</c> when the record is absent or already terminal.
    /// </summary>
    public async ValueTask<bool> TransitionAsync(
        string ownerId,
        string contentId,
        StagedContentState target,
        CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (Find(ownerId, contentId) is not { State: StagedContentState.Staged } record)
            {
                return false;
            }

            _records = _records.Remove(contentId);
            FreeContent(contentId);
            PersistIndex();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases every expired staged record. Returns the number of leases reclaimed.
    /// </summary>
    public async ValueTask<int> ReclaimExpiredAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var reclaimed = ReclaimExpiredCore(_clock.GetUtcNow());
            if (reclaimed > 0)
            {
                PersistIndex();
            }

            return reclaimed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _memory.Clear();
        _gate.Dispose();
    }

    private void FreeContent(string contentId)
    {
        _memory.TryRemove(contentId, out _);
        if (_root is not null)
        {
            Discard(ContentPath(contentId));
        }
    }

    private int ReclaimExpiredCore(DateTimeOffset now)
    {
        var expired = _records.Values
            .Where(record => record.State == StagedContentState.Staged && now >= record.ExpiresAt)
            .ToArray();
        foreach (var record in expired)
        {
            _records = _records.Remove(record.ContentId);
            FreeContent(record.ContentId);
        }

        return expired.Length;
    }

    private string ContentPath(string contentId) =>
        Path.Combine(_root!, contentId + ContentExtension);

    private static void Discard(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A staged blob that cannot be removed now is reclaimed by the next sweep.
        }
    }

    private ImmutableDictionary<string, StagedContentRecord> LoadIndex()
    {
        var path = Path.Combine(_root!, IndexFileName);
        if (!File.Exists(path))
        {
            return _records;
        }

        try
        {
            var entries = JsonSerializer.Deserialize<PersistedStagedContent[]>(
                File.ReadAllBytes(path)) ?? [];
            var builder = ImmutableDictionary.CreateBuilder<string, StagedContentRecord>(
                StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                builder[entry.ContentId] = new StagedContentRecord(
                    entry.ContentId,
                    entry.HostInstanceId,
                    entry.OwnerId,
                    entry.ContentType,
                    entry.ContentLength,
                    entry.ContentSha256,
                    entry.PackageId,
                    entry.PackageVersion,
                    DateTimeOffset.Parse(entry.CreatedAt, CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(entry.ExpiresAt, CultureInfo.InvariantCulture),
                    Enum.Parse<StagedContentState>(entry.State));
            }

            return builder.ToImmutable();
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or ArgumentException or IOException)
        {
            throw new InvalidDataException(
                "The staged-content index is unreadable; staged content cannot be reconciled safely.",
                exception);
        }
    }

    private void PersistIndex()
    {
        if (_root is null)
        {
            return;
        }

        var entries = _records.Values
            .OrderBy(record => record.ContentId, StringComparer.Ordinal)
            .Select(record => new PersistedStagedContent(
                record.ContentId,
                record.HostInstanceId,
                record.OwnerId,
                record.ContentType,
                record.ContentLength,
                record.ContentSha256,
                record.PackageId,
                record.PackageVersion,
                record.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                record.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
                record.State.ToString()))
            .ToArray();
        var path = Path.Combine(_root, IndexFileName);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(entries));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Removes content blobs the index does not reference and unfinished staging files,
    /// so an interrupted write can never leave an orphan behind. Only files this store
    /// owns are considered; anything else in the directory is left untouched.
    /// </summary>
    private void RemoveOrphanedContentFiles()
    {
        var missing = _records.Values
            .Where(record =>
                record.State != StagedContentState.Staged ||
                !File.Exists(ContentPath(record.ContentId)))
            .Select(record => record.ContentId)
            .ToArray();
        foreach (var contentId in missing)
        {
            _records = _records.Remove(contentId);
        }

        foreach (var file in Directory.EnumerateFiles(_root!))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(StagingExtension, StringComparison.Ordinal))
            {
                Discard(file);
                continue;
            }

            if (!name.EndsWith(ContentExtension, StringComparison.Ordinal))
            {
                continue;
            }

            var contentId = Path.GetFileNameWithoutExtension(name);
            if (!_records.TryGetValue(contentId, out var record) ||
                record.State != StagedContentState.Staged)
            {
                Discard(file);
            }
        }

        if (missing.Length > 0)
        {
            PersistIndex();
        }
    }

    private sealed record PersistedStagedContent(
        string ContentId,
        string HostInstanceId,
        string OwnerId,
        string ContentType,
        long ContentLength,
        string ContentSha256,
        string? PackageId,
        string? PackageVersion,
        string CreatedAt,
        string ExpiresAt,
        string State);
}

internal sealed class StagedContentReclaimer(StagedContentStore store) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await store.ReclaimExpiredAsync(stoppingToken);
        }
    }
}
