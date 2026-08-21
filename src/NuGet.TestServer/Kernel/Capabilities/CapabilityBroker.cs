using System.Collections.Concurrent;
using System.Collections.Immutable;
using NuGet.Packaging;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Faults;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Requests;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.Kernel.Capabilities;

internal interface ICapabilityHandleIdentity
{
    string HostInstanceId { get; }

    string OwnerId { get; }
}

internal enum CapabilityCallOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    QuotaExceeded
}

internal sealed record CapabilityAuditEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    string HostInstanceId,
    string OwnerId,
    string? OperationId,
    string CapabilityName,
    string Action,
    CapabilityCallOutcome Outcome);

internal sealed class CapabilityAuditLog
{
    private const int Capacity = 4096;
    private readonly ConcurrentQueue<CapabilityAuditEntry> _entries = new();
    private int _count;
    private long _droppedCount;
    private long _sequence;

    public IReadOnlyList<CapabilityAuditEntry> Entries => _entries.ToArray();

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    internal void Record(
        string hostInstanceId,
        string ownerId,
        string capabilityName,
        string action,
        CapabilityCallOutcome outcome)
    {
        _entries.Enqueue(new CapabilityAuditEntry(
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow,
            hostInstanceId,
            ownerId,
            CapabilityOperationAttribution.Current,
            capabilityName,
            action,
            outcome));
        if (Interlocked.Increment(ref _count) <= Capacity)
        {
            return;
        }

        if (_entries.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
            Interlocked.Increment(ref _droppedCount);
        }
    }
}

internal static class CapabilityOperationAttribution
{
    private static readonly AsyncLocal<string?> CurrentOperation = new();

    public static string? Current => CurrentOperation.Value;

    public static IDisposable Enter(string operationId)
    {
        var previous = CurrentOperation.Value;
        CurrentOperation.Value = operationId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CurrentOperation.Value = previous;
            }
        }
    }
}

internal sealed record CapabilityLimits(
    int MaximumConcurrentCalls = 64,
    long MaximumStreamBytes = 256L * 1024 * 1024,
    int MaximumQueuedCalls = 64,
    TimeSpan? QueueTimeout = null)
{
    public TimeSpan EffectiveQueueTimeout => QueueTimeout ?? TimeSpan.FromMilliseconds(250);

    public CapabilityLimits Validate()
    {
        if (MaximumConcurrentCalls <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumConcurrentCalls),
                "Capability concurrency must be positive.");
        }

        if (MaximumStreamBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumStreamBytes),
                "Capability stream limits must be positive.");
        }

        if (MaximumQueuedCalls < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumQueuedCalls),
                "Capability queue capacity cannot be negative.");
        }

        if (EffectiveQueueTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(QueueTimeout),
                "Capability queue timeout must be positive.");
        }

        return this;
    }
}

internal sealed class CapabilityDeniedException(string ownerId, string capabilityName)
    : InvalidOperationException(
        $"Owner '{ownerId}' was denied undeclared or ungranted capability '{capabilityName}'.")
{
    public string OwnerId { get; } = ownerId;

    public string CapabilityName { get; } = capabilityName;
}

internal sealed class CapabilityQuotaExceededException(
    string capabilityName,
    int retryAfterSeconds = 1)
    : InvalidOperationException($"Capability '{capabilityName}' exceeded its concurrency quota.")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

internal sealed class CapabilityStreamLimitExceededException(
    long declaredLength,
    long maximumLength)
    : InvalidOperationException(
        $"Capability stream length '{declaredLength}' exceeds limit '{maximumLength}'.")
{
    public long DeclaredLength { get; } = declaredLength;

    public long MaximumLength { get; } = maximumLength;
}

internal static class CapabilityStreams
{
    public static Stream Bound(Stream source, long declaredLength, long maximumLength)
        => Bound(source, declaredLength, maximumLength, CancellationToken.None, null);

    internal static Stream Bound(
        Stream source,
        long declaredLength,
        long maximumLength,
        CancellationToken cancellationToken,
        Action<CapabilityCallOutcome>? completed)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (declaredLength < 0 || declaredLength > maximumLength)
        {
            throw new CapabilityStreamLimitExceededException(declaredLength, maximumLength);
        }

        return new BoundedCapabilityReadStream(
            source,
            declaredLength,
            cancellationToken,
            completed);
    }
}

internal sealed class BoundedCapabilityReadStream(
    Stream inner,
    long maximumLength,
    CancellationToken cancellationToken,
    Action<CapabilityCallOutcome>? completed) : Stream
{
    private long _read;
    private int _completed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => Math.Min(inner.Length, maximumLength);
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Track(inner.Read(buffer, offset, count));
        }
        catch (OperationCanceledException)
        {
            Complete(CapabilityCallOutcome.Cancelled);
            throw;
        }
        catch
        {
            Complete(CapabilityCallOutcome.Failed);
            throw;
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            token);
        try
        {
            return Track(await inner.ReadAsync(buffer, linked.Token));
        }
        catch (OperationCanceledException)
        {
            Complete(CapabilityCallOutcome.Cancelled);
            throw;
        }
        catch
        {
            Complete(CapabilityCallOutcome.Failed);
            throw;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                inner.Dispose();
                Complete(CapabilityCallOutcome.Succeeded);
            }
            catch
            {
                Complete(CapabilityCallOutcome.Failed);
                throw;
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await inner.DisposeAsync();
            Complete(CapabilityCallOutcome.Succeeded);
        }
        catch
        {
            Complete(CapabilityCallOutcome.Failed);
            throw;
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }

    private int Track(int count)
    {
        _read += count;
        if (_read > maximumLength)
        {
            Complete(CapabilityCallOutcome.Failed);
            throw new CapabilityStreamLimitExceededException(_read, maximumLength);
        }

        if (count == 0)
        {
            Complete(CapabilityCallOutcome.Succeeded);
        }

        return count;
    }

    private void Complete(CapabilityCallOutcome outcome)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            completed?.Invoke(outcome);
        }
    }
}

internal sealed class CapabilityCallGate
{
    private readonly string _hostInstanceId;
    private readonly string _ownerId;
    private readonly string _capabilityName;
    private readonly CapabilityAuditLog _audit;
    private readonly CapabilityLimits _limits;
    private readonly SemaphoreSlim _concurrency;
    private int _queuedCalls;

    public CapabilityCallGate(
        string hostInstanceId,
        string ownerId,
        string capabilityName,
        CapabilityAuditLog audit,
        CapabilityLimits limits)
    {
        _hostInstanceId = hostInstanceId;
        _ownerId = ownerId;
        _capabilityName = capabilityName;
        _audit = audit;
        _limits = limits.Validate();
        _concurrency = new SemaphoreSlim(_limits.MaximumConcurrentCalls);
    }

    public async ValueTask<T> InvokeAsync<T>(
        string action,
        Func<CancellationToken, ValueTask<T>> callback,
        CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            _audit.Record(
                _hostInstanceId,
                _ownerId,
                _capabilityName,
                action,
                CapabilityCallOutcome.Cancelled);
            token.ThrowIfCancellationRequested();
        }
        if (!await EnterAsync(action, token))
        {
            throw new CapabilityQuotaExceededException(_capabilityName);
        }

        try
        {
            var result = await callback(token);
            _audit.Record(
                _hostInstanceId,
                _ownerId,
                _capabilityName,
                action,
                CapabilityCallOutcome.Succeeded);
            return result;
        }
        catch (OperationCanceledException)
        {
            _audit.Record(
                _hostInstanceId,
                _ownerId,
                _capabilityName,
                action,
                CapabilityCallOutcome.Cancelled);
            throw;
        }
        catch
        {
            _audit.Record(
                _hostInstanceId,
                _ownerId,
                _capabilityName,
                action,
                CapabilityCallOutcome.Failed);
            throw;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public Stream LeaseStream(
        string action,
        Stream source,
        long declaredLength,
        CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            source.Dispose();
            _audit.Record(
                _hostInstanceId,
                _ownerId,
                _capabilityName,
                action,
                CapabilityCallOutcome.Cancelled);
            token.ThrowIfCancellationRequested();
        }

        if (!_concurrency.Wait(0))
        {
            source.Dispose();
            RecordQuotaExceeded(action);
            throw new CapabilityQuotaExceededException(_capabilityName);
        }

        try
        {
            return CapabilityStreams.Bound(
                source,
                declaredLength,
                _limits.MaximumStreamBytes,
                token,
                outcome =>
                {
                    _audit.Record(
                        _hostInstanceId,
                        _ownerId,
                        _capabilityName,
                        action,
                        outcome);
                    _concurrency.Release();
                });
        }
        catch
        {
            _concurrency.Release();
            source.Dispose();
            throw;
        }
    }

    private async ValueTask<bool> EnterAsync(string action, CancellationToken token)
    {
        if (_concurrency.Wait(0))
        {
            return true;
        }

        if (Interlocked.Increment(ref _queuedCalls) > _limits.MaximumQueuedCalls)
        {
            Interlocked.Decrement(ref _queuedCalls);
            RecordQuotaExceeded(action);
            return false;
        }

        try
        {
            if (await _concurrency.WaitAsync(_limits.EffectiveQueueTimeout, token))
            {
                return true;
            }

            RecordQuotaExceeded(action);
            return false;
        }
        catch (OperationCanceledException)
        {
            _audit.Record(
                _hostInstanceId,
                _ownerId,
                _capabilityName,
                action,
                CapabilityCallOutcome.Cancelled);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _queuedCalls);
        }
    }

    private void RecordQuotaExceeded(string action) =>
        _audit.Record(
            _hostInstanceId,
            _ownerId,
            _capabilityName,
            action,
            CapabilityCallOutcome.QuotaExceeded);
}

internal interface IPackageReadCapability
{
    ValueTask<IReadOnlyList<CapabilityPackageMetadata>> GetAllAsync(CancellationToken token);

    ValueTask<IReadOnlyList<CapabilityPackageMetadata>> FindByIdAsync(
        string id,
        CancellationToken token);

    ValueTask<IReadOnlyList<CapabilityPackageMetadata>> FindReadableStoredByIdAsync(
        string id,
        PackageResourceClass resourceClass,
        CancellationToken token);

    ValueTask<CapabilityPackageMetadata?> FindReadableAsync(
        string id,
        string version,
        PackageResourceClass resourceClass,
        CancellationToken token);

    ValueTask<CapabilityPackageMetadata?> FindStoredAsync(
        string id,
        string version,
        CancellationToken token);

    ValueTask<CapabilityPackageSearchPage> SearchAsync(
        string query,
        bool includePrerelease,
        int skip,
        int take,
        string? packageType,
        CancellationToken token);

    ValueTask<byte[]?> FindSymbolAsync(string id, string version, CancellationToken token);

    ValueTask<CapabilityPackageContent?> OpenContentAsync(
        string id,
        string version,
        PackageResourceClass resourceClass,
        CancellationToken token);

}

internal sealed record CapabilityPackageMetadata(
    string Id,
    NuGet.Versioning.NuGetVersion Version,
    string NormalizedVersion,
    ReadOnlyMemory<byte> NuspecContent,
    string Description,
    string Summary,
    string Title,
    string Authors,
    string Tags,
    Uri? ProjectUrl,
    string Readme,
    string Icon,
    string LicenseExpression,
    string LicenseFile,
    Uri? LicenseUrl,
    IReadOnlyList<PackageTypeMetadata> EffectivePackageTypes,
    RepositoryMetadata? Repository,
    string PackageHash,
    PackageRepositoryMetadata RepositoryMetadata,
    IReadOnlyList<PackageDependencyGroup> DependencyGroups,
    DateTimeOffset Published,
    bool IsListed);

internal sealed record CapabilityPackageSearchPage(
    int TotalHits,
    IReadOnlyList<CapabilityPackageSearchItem> Items);

internal sealed record CapabilityPackageSearchItem(
    CapabilityPackageMetadata Package,
    IReadOnlyList<CapabilityPackageMetadata> Versions);

internal sealed record CapabilityPackageContent(
    Stream Stream,
    string Sha512,
    long Length);

internal interface IPackageMutationCapability
{
    ValueTask AddSymbolAsync(byte[] content, CancellationToken token);

    ValueTask<bool> DeleteAsync(string id, string version, CancellationToken token);

    ValueTask<bool> SetListedAsync(string id, string version, bool listed, CancellationToken token);

    ValueTask<bool> SetRepositoryMetadataAsync(
        string id,
        string version,
        PackageRepositoryMetadata metadata,
        CancellationToken token);
}

internal interface IPublicationCapability
{
    ValueTask<PackagePublicationResult> PublishAsync(
        PackagePublicationRequest request,
        CancellationToken token);

    ValueTask AddAsync(TestPackage package, CancellationToken token);

    ValueTask ResetAsync(CancellationToken token);

    ValueTask<bool> DeleteControlledAsync(
        string id,
        string version,
        string actor,
        string reason,
        CancellationToken token);

    ValueTask<string?> GetOwnerAsync(string packageId, CancellationToken token);
}

internal interface IModerationCapability
{
    ValueTask<bool> ModerateAsync(
        string id,
        string version,
        PackageModerationState state,
        string actor,
        string reason,
        CancellationToken token);

    ValueTask<bool> DeleteControlledAsync(
        string id,
        string version,
        string actor,
        string reason,
        CancellationToken token);

    ValueTask<IReadOnlyList<PackageSupplyChainAudit>> GetAuditHistoryAsync(
        CancellationToken token);

    ValueTask<IReadOnlyList<PackageValidationRecord>> GetValidationResultsAsync(
        string id,
        string version,
        CancellationToken token);
}

internal interface IFaultInjectionCapability
{
    int FaultCapacity { get; }
    ValueTask<IReadOnlyList<FaultRule>> GetFaultsAsync(CancellationToken token);
    ValueTask<string?> TryAddFaultAsync(FaultRule rule, CancellationToken token);
    ValueTask ClearFaultsAsync(CancellationToken token);
}

internal interface IRequestRecordingCapability
{
    int RequestCapacity { get; }
    long EvictedRequestCount { get; }
    ValueTask<IReadOnlyList<RequestRecord>> GetRequestsAsync(CancellationToken token);
    ValueTask ClearRequestsAsync(CancellationToken token);
}

internal sealed record ControlPackageMetadata(
    string Id,
    string NormalizedVersion,
    bool IsListed,
    DateTimeOffset Published);

/// <summary>
/// The extension-facing package control capability. Every member is action-scoped and
/// serializable: content moves through kernel-issued handles and metadata moves through
/// abstraction documents, so no kernel implementation type crosses the boundary.
/// </summary>
internal interface IPackageControlCapability
{
    ValueTask<IReadOnlyList<PackageSummaryDocument>> GetAllAsync(CancellationToken token);

    ValueTask<PackageSummaryDocument> AddContentAsync(
        StreamHandle content,
        CancellationToken token);

    ValueTask ResetAsync(CancellationToken token);

    ValueTask<bool> DeleteAsync(string id, string version, CancellationToken token);

    ValueTask<bool> SetListedAsync(
        string id,
        string version,
        bool listed,
        CancellationToken token);

    ValueTask<bool> SetRepositoryMetadataAsync(
        string id,
        string version,
        PackageRepositoryMetadataDocument metadata,
        CancellationToken token);
}

/// <summary>
/// Kernel-internal package fixture surface for the programmatic test host. It is never
/// handed to an extension, so it may use kernel package types.
/// </summary>
internal interface IPackageFixtureCapability
{
    ValueTask AddAsync(TestPackage package, CancellationToken token);
    ValueTask<TestPackage?> FindAsync(string id, string version, CancellationToken token);
    ValueTask<byte[]?> FindSymbolAsync(string id, string version, CancellationToken token);
    ValueTask ResetAsync(CancellationToken token);
}

/// <summary>
/// The extension-facing kernel instrumentation control capability. Fault rules and
/// request records cross the boundary as abstraction documents.
/// </summary>
internal interface IKernelInstrumentationControlCapability
{
    int FaultCapacity { get; }
    int RequestCapacity { get; }
    long EvictedRequestCount { get; }
    ValueTask<IReadOnlyList<FaultRuleDocument>> GetFaultsAsync(CancellationToken token);
    ValueTask<string?> TryAddFaultAsync(FaultRuleDocument rule, CancellationToken token);
    ValueTask ClearFaultsAsync(CancellationToken token);
    ValueTask<IReadOnlyList<RequestRecordDocument>> GetRequestsAsync(CancellationToken token);
    ValueTask ClearRequestsAsync(CancellationToken token);
}

/// <summary>
/// Kernel-internal instrumentation fixture surface for the programmatic test host.
/// </summary>
internal interface IKernelInstrumentationFixtureCapability
{
    ValueTask<IReadOnlyList<FaultRule>> GetFaultRulesAsync(CancellationToken token);
    ValueTask<string?> TryAddFaultRuleAsync(FaultRule rule, CancellationToken token);
    ValueTask ClearFaultsAsync(CancellationToken token);
    ValueTask<IReadOnlyList<RequestRecord>> GetRequestRecordsAsync(CancellationToken token);
    ValueTask ClearRequestsAsync(CancellationToken token);
}

internal sealed record CapabilityStorageReport(
    bool Ready,
    string Status,
    string Dependency,
    string? Reason,
    int PackageCount,
    long StorageBytes,
    int VulnerabilitySnapshotCount,
    int VulnerabilitySnapshotRetentionLimit);

internal sealed record CapabilityDiagnosticsSnapshot(
    long RequestCount,
    long FailedRequestCount,
    long PublishedPackageCount,
    long StorageFailureCount);

internal sealed record ExtensionHealthSnapshot(bool Ready, string Status, string? Reason);

internal interface IExtensionHealthSource
{
    ExtensionHealthSnapshot GetHealth();
}

internal interface IServerOperationsCapability
{
    string Mode { get; }

    ValueTask<CapabilityStorageReport> GetReadinessAsync(CancellationToken token);

    ValueTask<CapabilityStorageReport> GetStorageReportAsync(CancellationToken token);

    ValueTask<CapabilityDiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken token);

    ValueTask<StorageBackupManifest?> CreateBackupAsync(
        OperationExecutionContext context,
        StreamHandle destination,
        CancellationToken token);

    ValueTask<StorageBackupManifest?> RestoreAsync(
        OperationExecutionContext context,
        StreamHandle source,
        CancellationToken token);
}

internal interface IVulnerabilityReadCapability
{
    VulnerabilitySnapshot Active { get; }

    bool TryGet(string snapshotId, out VulnerabilitySnapshot? snapshot);
}

/// <summary>
/// The extension-facing vulnerability catalog capability. It returns documents and
/// kernel-issued content descriptors, never a kernel snapshot type.
/// </summary>
internal interface IVulnerabilityCatalogCapability
{
    ValueTask<VulnerabilityCatalogDocument> GetActiveAsync(CancellationToken token);

    ValueTask<ContentDescriptor?> OpenPageAsync(
        string snapshotId,
        string pageName,
        CancellationToken token);
}

internal sealed record VulnerabilityCatalogDocument(
    string SnapshotId,
    DateTimeOffset UpdatedAt,
    ImmutableArray<VulnerabilityCatalogPageDocument> Pages);

internal sealed record VulnerabilityCatalogPageDocument(
    string Name,
    string Sha256,
    DateTimeOffset UpdatedAt,
    string? Comment);

internal enum KernelEventKind
{
    PackagePublished
}

internal interface ITypedEventPublisher
{
    ValueTask PublishAsync(KernelEventKind kind, CancellationToken token);
}

internal interface IExtensionStateCapability
{
    ValueTask<T?> ReadAsync<T>(string key, CancellationToken token);

    ValueTask WriteAsync<T>(string key, T value, CancellationToken token);

    ValueTask<ExtensionStateFileSet?> ReadLegacyFileSetAsync(
        string logicalName,
        CancellationToken token);
}

internal sealed record ExtensionStateFile(string LogicalName, byte[] Content);

internal sealed record ExtensionStateFileSet(ImmutableArray<ExtensionStateFile> Files);

internal interface IBackupParticipationCapability
{
    ValueTask ContributeAsync(string logicalName, Stream content, CancellationToken token);
}

internal interface IOutboundHttpCapability
{
    ValueTask<OutboundHttpResponse> SendAsync(
        OutboundHttpRequest request,
        CancellationToken token);
}

internal interface ISecretReferenceCapability
{
    ValueTask<SecretReferenceHandle> ResolveReferenceAsync(
        string reference,
        CancellationToken token);
}

internal sealed record OutboundHttpRequest(
    Uri Uri,
    string Method,
    ImmutableDictionary<string, string> Headers,
    long MaximumResponseBytes);

internal sealed record OutboundHttpResponse(
    int statusCode,
    ImmutableDictionary<string, string> headers,
    ImmutableArray<string> contentEncodings,
    long? contentLength,
    byte[] content)
{
    public int StatusCode { get; } = statusCode;

    public ImmutableDictionary<string, string> Headers { get; } = headers;

    public ImmutableArray<string> ContentEncodings { get; } = contentEncodings;

    public long? ContentLength { get; } = contentLength;

    public byte[] Content { get; } = content;
}

internal sealed record SecretReferenceHandle(string Id);

internal static class BuiltInOwnerCapabilityRequirements
{
    public static IReadOnlyDictionary<string, ImmutableArray<string>> All { get; } =
        new Dictionary<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [BuiltInExtensionIds.Protocol] =
            [
                BuiltInCapabilityNames.PackagesIdentityRead,
                BuiltInCapabilityNames.PackagesMetadataRead,
                BuiltInCapabilityNames.PackagesContentRead,
                BuiltInCapabilityNames.VulnerabilityStateRead
            ],
            [BuiltInExtensionIds.Publication] =
            [
                BuiltInCapabilityNames.PackagesMetadataRead,
                BuiltInCapabilityNames.PackagesContentWrite,
                BuiltInCapabilityNames.PackagesPublish,
                BuiltInCapabilityNames.PackagesUnlist,
                BuiltInCapabilityNames.PackagesRelist,
                BuiltInCapabilityNames.PackagesDelete,
                BuiltInCapabilityNames.EventsPublish
            ],
            [BuiltInExtensionIds.Vulnerabilities] =
            [
                BuiltInCapabilityNames.VulnerabilityStateRead
            ],
            [BuiltInExtensionIds.SupplyChain] =
            [
                BuiltInCapabilityNames.ModerationRead,
                BuiltInCapabilityNames.ModerationDecide
            ],
            [BuiltInExtensionIds.SupplyChainPolicy] =
            [
                BuiltInCapabilityNames.SupplyChainSignatureInspect,
                BuiltInCapabilityNames.SupplyChainPackageScan
            ],
            [BuiltInExtensionIds.TestControl] =
            [
                BuiltInCapabilityNames.ControlPackagesManage,
                BuiltInCapabilityNames.ControlInstrumentationManage
            ],
            [BuiltInExtensionIds.Operations] =
            [
                BuiltInCapabilityNames.OperationsQuery,
                BuiltInCapabilityNames.BackupInvoke,
                BuiltInCapabilityNames.RestoreInvoke
            ]
        };
}

internal sealed class CapabilityBroker
{
    private readonly string _hostInstanceId;
    private readonly ResolvedExtensionGraph _graph;
    private readonly CapabilityAuditLog _audit;
    private readonly CapabilityLimits _limits;
    private readonly CapabilityServices _services;
    private readonly ConcurrentDictionary<string, CapabilityOwnerContext> _contexts =
        new(StringComparer.OrdinalIgnoreCase);

    public CapabilityBroker(
        string hostInstanceId,
        ResolvedExtensionGraph graph,
        CapabilityAuditLog audit,
        CapabilityLimits limits,
        CapabilityServices services)
    {
        _hostInstanceId = hostInstanceId;
        _graph = graph;
        _audit = audit;
        _limits = limits.Validate();
        _services = services;
    }

    public CapabilityOwnerContext ForOwner(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (!_graph.Extensions.Any(extension =>
                string.Equals(extension.Id, ownerId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CapabilityDeniedException(ownerId, "<owner-not-active>");
        }

        return _contexts.GetOrAdd(
            ownerId,
            static (id, state) => new CapabilityOwnerContext(
                state.HostInstanceId,
                id,
                state.Graph.Capabilities
                    .Where(capability =>
                        capability.IsGranted &&
                        string.Equals(capability.ExtensionId, id, StringComparison.OrdinalIgnoreCase))
                    .Select(capability => capability.Name)
                    .ToImmutableHashSet(StringComparer.Ordinal),
                state.Audit,
                state.Limits,
                state.Services),
            (HostInstanceId: _hostInstanceId, Graph: _graph, Audit: _audit, Limits: _limits, Services: _services));
    }
}

internal sealed class CapabilityOwnerContext : IExtensionCapabilities
{
    private readonly string _hostInstanceId;
    private readonly string _ownerId;
    private readonly ImmutableHashSet<string> _grants;
    private readonly CapabilityAuditLog _audit;
    private readonly CapabilityLimits _limits;
    private readonly CapabilityServices _services;
    private readonly ConcurrentDictionary<Type, object> _handles = new();

    internal CapabilityOwnerContext(
        string hostInstanceId,
        string ownerId,
        ImmutableHashSet<string> grants,
        CapabilityAuditLog audit,
        CapabilityLimits limits,
        CapabilityServices services)
    {
        _hostInstanceId = hostInstanceId;
        _ownerId = ownerId;
        _grants = grants;
        _audit = audit;
        _limits = limits;
        _services = services;
    }

    public ImmutableHashSet<string> GrantedCapabilities => _grants;

    public T GetRequired<T>(string capabilityName) where T : class
    {
        if (!_grants.Contains(capabilityName) ||
            !CapabilityContracts.Supports(typeof(T), capabilityName))
        {
            _audit.Record(
                _hostInstanceId,
                _ownerId,
                capabilityName,
                "acquire",
                CapabilityCallOutcome.Failed);
            throw new CapabilityDeniedException(_ownerId, capabilityName);
        }

        var handle = _handles.GetOrAdd(typeof(T), _ => CreateHandle<T>());
        return (T)handle;
    }

    /// <summary>
    /// Attempts to acquire an optional capability. Ungranted optional capabilities are
    /// denied without failing the owner.
    /// </summary>
    public bool TryGet<T>(string capabilityName, out T? capability) where T : class
    {
        capability = null;
        if (!_grants.Contains(capabilityName) ||
            !CapabilityContracts.Supports(typeof(T), capabilityName))
        {
            _audit.Record(
                _hostInstanceId,
                _ownerId,
                capabilityName,
                "acquire",
                CapabilityCallOutcome.Failed);
            return false;
        }

        capability = (T)_handles.GetOrAdd(typeof(T), _ => CreateHandle<T>());
        return true;
    }

    private T CreateHandle<T>() where T : class
    {
        object handle = typeof(T) switch
        {
            var type when type == typeof(IPackageReadCapability) =>
                new PackageReadCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.PackageStore,
                    _services.PackageCandidates,
                    _services.Visibility),
            var type when type == typeof(IPackageMutationCapability) =>
                new PackageMutationCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.PackageStore),
            var type when type == typeof(IPublicationCapability) =>
                new PublicationCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.SupplyChain()),
            var type when type == typeof(IModerationCapability) =>
                new ModerationCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.SupplyChain()),
            var type when type == typeof(IPackageSignatureInspectionCapability) =>
                new PackageSignatureInspectionCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.PackagePolicyInspection),
            var type when type == typeof(IPackageScannerCapability) =>
                new PackageScannerCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.PackagePolicyInspection),
            var type when type == typeof(IFaultInjectionCapability) =>
                new FaultInjectionCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.Instrumentation),
            var type when type == typeof(IRequestRecordingCapability) =>
                new RequestRecordingCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.Instrumentation),
            var type when type == typeof(IPackageControlCapability) =>
                new PackageControlCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.PackageStore,
                    _services.SupplyChain(),
                    _services.Diagnostics,
                    _services.PackageLimits),
            var type when type == typeof(IKernelInstrumentationControlCapability) =>
                new KernelInstrumentationControlCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.Instrumentation),
            var type when type == typeof(IServerOperationsCapability) =>
                new ServerOperationsCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.Storage,
                    _services.Diagnostics,
                    _services.Hosting,
                    _services.StorageDirectory,
                    _services.ExtensionHealth),
            var type when type == typeof(IVulnerabilityReadCapability) =>
                new VulnerabilityReadCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.Vulnerabilities),
            var type when type == typeof(IVulnerabilityCatalogCapability) =>
                new VulnerabilityCatalogCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.Vulnerabilities),
            var type when type == typeof(IPackageFixtureCapability) =>
                new PackageControlCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.PackageStore,
                    _services.SupplyChain(),
                    _services.Diagnostics,
                    _services.PackageLimits),
            var type when type == typeof(IKernelInstrumentationFixtureCapability) =>
                new KernelInstrumentationControlCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.Instrumentation),
            var type when type == typeof(ITypedEventPublisher) =>
                new TypedEventPublisher(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.Diagnostics),
            var type when type == typeof(IExtensionStateCapability) =>
                new ExtensionStateCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.ExtensionState),
            var type when type == typeof(IOutboundHttpCapability) =>
                new OutboundHttpCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.OutboundHttp),
            var type when type == typeof(IHostClockCapability) =>
                new HostClockCapability(
                    _hostInstanceId,
                    _ownerId,
                    _grants,
                    _audit,
                    _limits,
                    _services.Clock),
            _ => throw new InvalidOperationException(
                $"Capability handle type '{typeof(T).FullName}' is not available.")
        };
        return (T)handle;
    }
}

internal static class CapabilityContracts
{
    private static readonly IReadOnlyDictionary<Type, ImmutableHashSet<string>> Names =
        new Dictionary<Type, ImmutableHashSet<string>>
        {
            [typeof(IPackageReadCapability)] = Set(
                BuiltInCapabilityNames.PackagesIdentityRead,
                BuiltInCapabilityNames.PackagesMetadataRead,
                BuiltInCapabilityNames.PackagesContentRead),
            [typeof(IPackageMutationCapability)] = Set(
                BuiltInCapabilityNames.PackagesMetadataWrite,
                BuiltInCapabilityNames.PackagesContentWrite,
                BuiltInCapabilityNames.PackagesUnlist,
                BuiltInCapabilityNames.PackagesRelist,
                BuiltInCapabilityNames.PackagesDelete),
            [typeof(IPublicationCapability)] = Set(
                BuiltInCapabilityNames.PackagesMetadataRead,
                BuiltInCapabilityNames.PackagesPublish,
                BuiltInCapabilityNames.PackagesDelete),
            [typeof(IModerationCapability)] = Set(
                BuiltInCapabilityNames.ModerationRead,
                BuiltInCapabilityNames.ModerationDecide),
            [typeof(IFaultInjectionCapability)] = Set(
                BuiltInCapabilityNames.ControlFaultsInject),
            [typeof(IRequestRecordingCapability)] = Set(
                BuiltInCapabilityNames.ControlRequestsRead),
            [typeof(IPackageControlCapability)] = Set(
                BuiltInCapabilityNames.ControlPackagesManage),
            [typeof(IPackageFixtureCapability)] = Set(
                BuiltInCapabilityNames.ControlPackagesManage),
            [typeof(IKernelInstrumentationControlCapability)] = Set(
                BuiltInCapabilityNames.ControlInstrumentationManage),
            [typeof(IKernelInstrumentationFixtureCapability)] = Set(
                BuiltInCapabilityNames.ControlInstrumentationManage),
            [typeof(IServerOperationsCapability)] = Set(
                BuiltInCapabilityNames.OperationsQuery,
                BuiltInCapabilityNames.BackupInvoke,
                BuiltInCapabilityNames.RestoreInvoke),
            [typeof(IVulnerabilityReadCapability)] = Set(
                BuiltInCapabilityNames.VulnerabilityStateRead),
            [typeof(IVulnerabilityCatalogCapability)] = Set(
                BuiltInCapabilityNames.VulnerabilityStateRead),
            [typeof(ITypedEventPublisher)] = Set(BuiltInCapabilityNames.EventsPublish),
            [typeof(IExtensionStateCapability)] = Set(
                BuiltInCapabilityNames.ExtensionStateRead,
                BuiltInCapabilityNames.ExtensionStateWrite),
            [typeof(IBackupParticipationCapability)] = Set(
                BuiltInCapabilityNames.BackupContribute),
            [typeof(IOutboundHttpCapability)] = Set(BuiltInCapabilityNames.OutboundHttp),
            [typeof(ISecretReferenceCapability)] = Set(
                BuiltInCapabilityNames.SecretsResolveReference),
            [typeof(IHostClockCapability)] = Set(BuiltInCapabilityNames.HostClockRead)
            ,
            [typeof(IPackageSignatureInspectionCapability)] = Set(
                BuiltInCapabilityNames.SupplyChainSignatureInspect),
            [typeof(IPackageScannerCapability)] = Set(
                BuiltInCapabilityNames.SupplyChainPackageScan)
        };

    public static bool Supports(Type type, string capabilityName) =>
        Names.TryGetValue(type, out var names) && names.Contains(capabilityName);

    private static ImmutableHashSet<string> Set(params string[] names) =>
        names.ToImmutableHashSet(StringComparer.Ordinal);
}

internal sealed record CapabilityServices(
    IPackageStore PackageStore,
    IPackageCandidateStore PackageCandidates,
    PackageVisibilityPolicy Visibility,
    Func<PackageSupplyChainService> SupplyChain,
    PackagePolicyInspectionService PackagePolicyInspection,
    KernelRequestInstrumentation Instrumentation,
    StorageHealth Storage,
    ServerDiagnostics Diagnostics,
    ServerHostingOptions Hosting,
    string? StorageDirectory,
    VulnerabilitySnapshotProvider Vulnerabilities,
    ExtensionStateStore ExtensionState,
    IExtensionHealthSource ExtensionHealth,
    KernelOutboundHttpClient OutboundHttp,
    PackageTransferLimits PackageLimits,
    TimeProvider Clock);

internal sealed class PackageSignatureInspectionCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    PackagePolicyInspectionService inspection)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits),
        IPackageSignatureInspectionCapability
{
    public ValueTask<PackageSignatureInspection> InspectSignatureAsync(
        PolicyPackageHandle package,
        CancellationToken cancellationToken) =>
        Gate(BuiltInCapabilityNames.SupplyChainSignatureInspect)
            .InvokeAsync(
                "inspect-signature",
                token => inspection.InspectSignatureAsync(package, token),
                cancellationToken);

}

internal sealed class PackageScannerCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    PackagePolicyInspectionService inspection)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits),
        IPackageScannerCapability
{
    public ValueTask<PackageScannerInspection> ScanAsync(
        PolicyPackageHandle package,
        CancellationToken cancellationToken) =>
        Gate(BuiltInCapabilityNames.SupplyChainPackageScan)
            .InvokeAsync(
                "scan",
                token => inspection.ScanAsync(package, token),
                cancellationToken);
}

internal abstract class CapabilityHandle : ICapabilityHandleIdentity
{
    private readonly ConcurrentDictionary<string, CapabilityCallGate> _gates =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CapabilityCallGate> _streamGates =
        new(StringComparer.Ordinal);
    private readonly ImmutableHashSet<string> _grants;
    private readonly CapabilityAuditLog _audit;
    private readonly CapabilityLimits _limits;

    protected CapabilityHandle(
        string hostInstanceId,
        string ownerId,
        ImmutableHashSet<string> grants,
        CapabilityAuditLog audit,
        CapabilityLimits limits)
    {
        HostInstanceId = hostInstanceId;
        OwnerId = ownerId;
        _grants = grants;
        _audit = audit;
        _limits = limits;
    }

    public string HostInstanceId { get; }

    public string OwnerId { get; }

    protected long MaximumStreamBytes => _limits.MaximumStreamBytes;

    protected CapabilityCallGate Gate(string capabilityName)
    {
        if (!_grants.Contains(capabilityName))
        {
            _audit.Record(
                HostInstanceId,
                OwnerId,
                capabilityName,
                "invoke",
                CapabilityCallOutcome.Failed);
            throw new CapabilityDeniedException(OwnerId, capabilityName);
        }

        return _gates.GetOrAdd(
            capabilityName,
            name => new CapabilityCallGate(HostInstanceId, OwnerId, name, _audit, _limits));
    }

    protected CapabilityCallGate StreamGate(string capabilityName)
    {
        _ = Gate(capabilityName);
        return _streamGates.GetOrAdd(
            capabilityName,
            name => new CapabilityCallGate(HostInstanceId, OwnerId, name, _audit, _limits));
    }
}

internal sealed class PackageReadCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    IPackageStore store,
    IPackageCandidateStore candidates,
    PackageVisibilityPolicy visibility)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits), IPackageReadCapability
{
    public ValueTask<IReadOnlyList<CapabilityPackageMetadata>> GetAllAsync(
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesMetadataRead)
            .InvokeAsync(
                "get-all",
                async ct => Map(await store.GetAllAsync(ct)),
                token);

    public ValueTask<IReadOnlyList<CapabilityPackageMetadata>> FindByIdAsync(
        string id,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesMetadataRead)
            .InvokeAsync(
                "find-by-id",
                async ct => Map(await store.FindByIdAsync(id, ct)),
                token);

    public ValueTask<IReadOnlyList<CapabilityPackageMetadata>> FindReadableStoredByIdAsync(
        string id,
        PackageResourceClass resourceClass,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesMetadataRead)
            .InvokeAsync(
                "find-readable-stored-by-id",
                async ct => Map(
                    (await candidates.FindStoredByIdAsync(id, ct))
                    .Where(package => visibility.CanRead(package, resourceClass))),
                token);

    public ValueTask<CapabilityPackageMetadata?> FindReadableAsync(
        string id,
        string version,
        PackageResourceClass resourceClass,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesIdentityRead)
            .InvokeAsync(
                "find-readable",
                async ct =>
                {
                    var package = await store.FindAsync(id, version, ct);
                    return package is not null && visibility.CanRead(package, resourceClass)
                        ? Map(package)
                        : null;
                },
                token);

    public ValueTask<CapabilityPackageMetadata?> FindStoredAsync(
        string id,
        string version,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesMetadataRead)
            .InvokeAsync(
                "find-stored",
                async ct =>
                {
                    var package = await store.FindStoredAsync(id, version, ct);
                    return package is null ? null : Map(package);
                },
                token);

    public ValueTask<CapabilityPackageSearchPage> SearchAsync(
        string query,
        bool includePrerelease,
        int skip,
        int take,
        string? packageType,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesMetadataRead)
            .InvokeAsync(
                "search",
                async ct =>
                {
                    var page = await store.SearchAsync(
                        query,
                        includePrerelease,
                        skip,
                        take,
                        ct,
                        packageType);
                    return new CapabilityPackageSearchPage(
                        page.TotalHits,
                        [
                            .. page.Items.Select(item => new CapabilityPackageSearchItem(
                                Map(item.Package),
                                Map(item.Versions)))
                        ]);
                },
                token);

    public ValueTask<byte[]?> FindSymbolAsync(string id, string version, CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesContentRead)
            .InvokeAsync(
                "find-symbol",
                async ct =>
                {
                    var content = await store.FindSymbolAsync(id, version, ct);
                    if (content is not null && content.LongLength > MaximumStreamBytes)
                    {
                        throw new CapabilityStreamLimitExceededException(
                            content.LongLength,
                            MaximumStreamBytes);
                    }

                    return content;
                },
                token);

    public ValueTask<CapabilityPackageContent?> OpenContentAsync(
        string id,
        string version,
        PackageResourceClass resourceClass,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesContentRead)
            .InvokeAsync(
                "open-content",
                async ct =>
                {
                    var package = await store.FindAsync(id, version, ct);
                    if (package is null || !visibility.CanRead(package, resourceClass))
                    {
                        return null;
                    }

                    return new CapabilityPackageContent(
                        StreamGate(BuiltInCapabilityNames.PackagesContentRead).LeaseStream(
                            "consume-content",
                            package.OpenReadStream(),
                            package.ContentLength,
                            ct),
                        package.PackageHash,
                        package.ContentLength);
                },
                token);

    private static IReadOnlyList<CapabilityPackageMetadata> Map(
        IEnumerable<TestPackage> packages) =>
        [.. packages.Select(Map)];

    private static CapabilityPackageMetadata Map(TestPackage package) =>
        new(
            package.Identity.Id,
            package.Identity.Version,
            package.NormalizedVersion,
            package.NuspecContent,
            package.Description,
            package.Summary,
            package.Title,
            package.Authors,
            package.Tags,
            package.ProjectUrl,
            package.Readme,
            package.Icon,
            package.LicenseExpression,
            package.LicenseFile,
            package.LicenseUrl,
            package.EffectivePackageTypes,
            package.Repository,
            package.PackageHash,
            package.RepositoryMetadata,
            package.DependencyGroups,
            package.Published,
            package.IsListed);
}

internal sealed class PackageMutationCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    IPackageStore store)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits), IPackageMutationCapability
{
    public async ValueTask AddSymbolAsync(byte[] content, CancellationToken token) =>
        await Gate(BuiltInCapabilityNames.PackagesContentWrite)
            .InvokeAsync(
                "add-symbol",
                async ct =>
                {
                    await store.AddSymbolAsync(content, ct);
                    return true;
                },
                token);

    public ValueTask<bool> DeleteAsync(string id, string version, CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesDelete)
            .InvokeAsync("delete", ct => store.DeleteAsync(id, version, ct), token);

    public ValueTask<bool> SetListedAsync(
        string id,
        string version,
        bool listed,
        CancellationToken token) =>
        Gate(listed
                ? BuiltInCapabilityNames.PackagesRelist
                : BuiltInCapabilityNames.PackagesUnlist)
            .InvokeAsync(
                listed ? "relist" : "unlist",
                ct => store.SetListedAsync(id, version, listed, ct),
                token);

    public ValueTask<bool> SetRepositoryMetadataAsync(
        string id,
        string version,
        PackageRepositoryMetadata metadata,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesMetadataWrite)
            .InvokeAsync(
                "set-repository-metadata",
                ct => store.SetRepositoryMetadataAsync(id, version, metadata, ct),
                token);
}

internal sealed class PublicationCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    PackageSupplyChainService supplyChain)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits), IPublicationCapability
{
    public ValueTask<PackagePublicationResult> PublishAsync(
        PackagePublicationRequest request,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesPublish)
            .InvokeAsync("publish", ct => supplyChain.PublishAsync(request, ct), token);

    public async ValueTask AddAsync(TestPackage package, CancellationToken token) =>
        await Gate(BuiltInCapabilityNames.PackagesPublish)
            .InvokeAsync(
                "add",
                async ct =>
                {
                    await supplyChain.AddAsync(package, ct);
                    return true;
                },
                token);

    public async ValueTask ResetAsync(CancellationToken token) =>
        await Gate(BuiltInCapabilityNames.PackagesDelete)
            .InvokeAsync(
                "reset",
                async ct =>
                {
                    await supplyChain.ResetAsync(ct);
                    return true;
                },
                token);

    public ValueTask<bool> DeleteControlledAsync(
        string id,
        string version,
        string actor,
        string reason,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesDelete)
            .InvokeAsync(
                "delete-controlled",
                ct => supplyChain.DeleteControlledAsync(id, version, actor, reason, ct),
                token);

    public ValueTask<string?> GetOwnerAsync(string packageId, CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesMetadataRead)
            .InvokeAsync("get-owner", ct => supplyChain.GetOwnerAsync(packageId, ct), token);
}

internal sealed class ModerationCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    PackageSupplyChainService supplyChain)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits), IModerationCapability
{
    public ValueTask<bool> ModerateAsync(
        string id,
        string version,
        PackageModerationState state,
        string actor,
        string reason,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ModerationDecide)
            .InvokeAsync(
                "decide",
                ct => supplyChain.ModerateAsync(id, version, state, actor, reason, ct),
                token);

    public ValueTask<bool> DeleteControlledAsync(
        string id,
        string version,
        string actor,
        string reason,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ModerationDecide)
            .InvokeAsync(
                "delete",
                ct => supplyChain.DeleteControlledAsync(id, version, actor, reason, ct),
                token);

    public ValueTask<IReadOnlyList<PackageSupplyChainAudit>> GetAuditHistoryAsync(
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ModerationRead)
            .InvokeAsync("get-audit", supplyChain.GetAuditHistoryAsync, token);

    public ValueTask<IReadOnlyList<PackageValidationRecord>> GetValidationResultsAsync(
        string id,
        string version,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ModerationRead)
            .InvokeAsync(
                "get-validations",
                ct => supplyChain.GetValidationResultsAsync(id, version, ct),
                token);
}

internal sealed class FaultInjectionCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    KernelRequestInstrumentation instrumentation)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits),
        IFaultInjectionCapability
{
    public int FaultCapacity => instrumentation.FaultCapacity;

    public ValueTask<IReadOnlyList<FaultRule>> GetFaultsAsync(CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlFaultsInject)
            .InvokeAsync(
                "get-faults",
                _ => ValueTask.FromResult(instrumentation.GetFaults()),
                token);

    public ValueTask<string?> TryAddFaultAsync(FaultRule rule, CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlFaultsInject)
            .InvokeAsync(
                "add-fault",
                _ =>
                {
                    try
                    {
                        instrumentation.AddFault(rule);
                        return ValueTask.FromResult<string?>(null);
                    }
                    catch (InvalidOperationException exception)
                    {
                        return ValueTask.FromResult<string?>(exception.Message);
                    }
                },
                token);

    public async ValueTask ClearFaultsAsync(CancellationToken token) =>
        await Gate(BuiltInCapabilityNames.ControlFaultsInject)
            .InvokeAsync(
                "clear-faults",
                _ =>
                {
                    instrumentation.ClearFaults();
                    return ValueTask.FromResult(true);
                },
                token);
}

internal sealed class RequestRecordingCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    KernelRequestInstrumentation instrumentation)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits),
        IRequestRecordingCapability
{
    public int RequestCapacity => instrumentation.RequestCapacity;
    public long EvictedRequestCount => instrumentation.EvictedRequestCount;

    public ValueTask<IReadOnlyList<RequestRecord>> GetRequestsAsync(CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlRequestsRead)
            .InvokeAsync(
                "get-requests",
                async ct =>
                {
                    await instrumentation.WaitForCompletedRequestsAsync(ct);
                    return instrumentation.GetRequests();
                },
                token);

    public async ValueTask ClearRequestsAsync(CancellationToken token) =>
        await Gate(BuiltInCapabilityNames.ControlRequestsRead)
            .InvokeAsync(
                "clear-requests",
                _ =>
                {
                    instrumentation.ClearRequests();
                    return ValueTask.FromResult(true);
                },
                token);
}

internal sealed class PackageControlCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    IPackageStore store,
    PackageSupplyChainService supplyChain,
    ServerDiagnostics diagnostics,
    PackageTransferLimits packageLimits)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits),
        IPackageControlCapability,
        IPackageFixtureCapability
{
    public ValueTask<IReadOnlyList<PackageSummaryDocument>> GetAllAsync(
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlPackagesManage)
            .InvokeAsync(
                "get-packages",
                async ct => Map(await store.GetAllAsync(ct)),
                token);

    public ValueTask<PackageSummaryDocument> AddContentAsync(
        StreamHandle content,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlPackagesManage)
            .InvokeAsync(
                "add-package-content",
                async ct =>
                {
                    var resolved = OperationExecutionScope.Required.Content.Resolve(content);
                    var stream = resolved.Stream ??
                        new MemoryStream(resolved.Bytes!.Value.ToArray(), writable: false);
                    TestPackage? package = null;
                    try
                    {
                        package = await TestPackage.FromStreamAsync(
                            stream,
                            packageLimits,
                            cancellationToken: ct);
                        await supplyChain.AddAsync(package, ct);
                        diagnostics.RecordPackagePublished();
                        var result = Map(package);
                        package = null;
                        return result;
                    }
                    finally
                    {
                        package?.Dispose();
                    }
                },
                token);

    public async ValueTask AddAsync(TestPackage package, CancellationToken token) =>
        await Gate(BuiltInCapabilityNames.ControlPackagesManage)
            .InvokeAsync(
                "add-package",
                async ct =>
                {
                    await supplyChain.AddAsync(package, ct);
                    return true;
                },
                token);

    public ValueTask<TestPackage?> FindAsync(
        string id,
        string version,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlPackagesManage)
            .InvokeAsync("find-package", ct => store.FindAsync(id, version, ct), token);

    public ValueTask<byte[]?> FindSymbolAsync(
        string id,
        string version,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlPackagesManage)
            .InvokeAsync("find-symbol", ct => store.FindSymbolAsync(id, version, ct), token);

    public async ValueTask ResetAsync(CancellationToken token) =>
        await Gate(BuiltInCapabilityNames.ControlPackagesManage)
            .InvokeAsync(
                "reset-packages",
                async ct =>
                {
                    await supplyChain.ResetAsync(ct);
                    return true;
                },
                token);

    public ValueTask<bool> DeleteAsync(
        string id,
        string version,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlPackagesManage)
            .InvokeAsync("delete-package", ct => store.DeleteAsync(id, version, ct), token);

    public ValueTask<bool> SetListedAsync(
        string id,
        string version,
        bool listed,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlPackagesManage)
            .InvokeAsync(
                listed ? "relist-package" : "unlist-package",
                ct => store.SetListedAsync(id, version, listed, ct),
                token);

    public ValueTask<bool> SetRepositoryMetadataAsync(
        string id,
        string version,
        PackageRepositoryMetadataDocument metadata,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlPackagesManage)
            .InvokeAsync(
                "set-package-metadata",
                ct => store.SetRepositoryMetadataAsync(id, version, Map(metadata), ct),
                token);

    private static PackageRepositoryMetadata Map(PackageRepositoryMetadataDocument metadata) =>
        new(
            [.. metadata.Owners],
            metadata.Downloads,
            metadata.Verified,
            metadata.Deprecation is { } deprecation
                ? new PackageDeprecation(
                    [.. deprecation.Reasons],
                    deprecation.Message!,
                    deprecation.AlternatePackage is { } alternate
                        ? new AlternatePackage(alternate.Id, alternate.Range)
                        : null)
                : null);

    private static IReadOnlyList<PackageSummaryDocument> Map(IEnumerable<TestPackage> packages) =>
        [.. packages.Select(Map)];

    private static PackageSummaryDocument Map(TestPackage package) =>
        new(
            new PackageIdentity(package.Identity.Id, package.NormalizedVersion),
            package.IsListed,
            package.Published);
}

internal sealed class KernelInstrumentationControlCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    KernelRequestInstrumentation instrumentation)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits),
        IKernelInstrumentationControlCapability,
        IKernelInstrumentationFixtureCapability
{
    public int FaultCapacity => instrumentation.FaultCapacity;
    public int RequestCapacity => instrumentation.RequestCapacity;
    public long EvictedRequestCount => instrumentation.EvictedRequestCount;

    public async ValueTask<IReadOnlyList<FaultRuleDocument>> GetFaultsAsync(
        CancellationToken token) =>
        [.. (await GetFaultRulesAsync(token)).Select(KernelInstrumentationDocuments.Fault)];

    public ValueTask<IReadOnlyList<FaultRule>> GetFaultRulesAsync(CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlInstrumentationManage)
            .InvokeAsync(
                "get-faults",
                _ => ValueTask.FromResult(instrumentation.GetFaults()),
                token);

    public ValueTask<string?> TryAddFaultAsync(
        FaultRuleDocument rule,
        CancellationToken token) =>
        TryAddFaultRuleAsync(KernelInstrumentationDocuments.Fault(rule), token);

    public ValueTask<string?> TryAddFaultRuleAsync(FaultRule rule, CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlInstrumentationManage)
            .InvokeAsync(
                "add-fault",
                _ =>
                {
                    try
                    {
                        instrumentation.AddFault(rule);
                        return ValueTask.FromResult<string?>(null);
                    }
                    catch (InvalidOperationException exception)
                    {
                        return ValueTask.FromResult<string?>(exception.Message);
                    }
                },
                token);

    public async ValueTask ClearFaultsAsync(CancellationToken token) =>
        await Gate(BuiltInCapabilityNames.ControlInstrumentationManage)
            .InvokeAsync(
                "clear-faults",
                _ =>
                {
                    instrumentation.ClearFaults();
                    return ValueTask.FromResult(true);
                },
                token);

    public async ValueTask<IReadOnlyList<RequestRecordDocument>> GetRequestsAsync(
        CancellationToken token) =>
        [.. (await GetRequestRecordsAsync(token)).Select(KernelInstrumentationDocuments.Request)];

    public ValueTask<IReadOnlyList<RequestRecord>> GetRequestRecordsAsync(
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ControlInstrumentationManage)
            .InvokeAsync(
                "get-requests",
                async ct =>
                {
                    await instrumentation.WaitForCompletedRequestsAsync(ct);
                    return instrumentation.GetRequests();
                },
                token);

    public async ValueTask ClearRequestsAsync(CancellationToken token) =>
        await Gate(BuiltInCapabilityNames.ControlInstrumentationManage)
            .InvokeAsync(
                "clear-requests",
                _ =>
                {
                    instrumentation.ClearRequests();
                    return ValueTask.FromResult(true);
                },
                token);
}

/// <summary>
/// Kernel-side translation between instrumentation implementation types and the
/// abstraction documents extensions see.
/// </summary>
internal static class KernelInstrumentationDocuments
{
    public static FaultRuleDocument Fault(FaultRule rule) =>
        new(
            rule.Id,
            rule.Method ?? string.Empty,
            rule.PathContains ?? string.Empty,
            (int)rule.StatusCode,
            (long)rule.Delay.TotalMilliseconds,
            rule.RemainingMatches);

    public static FaultRule Fault(FaultRuleDocument document) =>
        new(
            document.Id,
            string.IsNullOrEmpty(document.Method) ? null : document.Method,
            string.IsNullOrEmpty(document.RoutePattern) ? null : document.RoutePattern,
            (System.Net.HttpStatusCode)document.StatusCode,
            document.RemainingMatches ?? 0,
            TimeSpan.FromMilliseconds(document.DelayMilliseconds));

    public static RequestRecordDocument Request(RequestRecord record) =>
        new(
            record.Sequence,
            record.Timestamp,
            record.Method,
            record.Path,
            record.StatusCode,
            record.DurationMilliseconds,
            record.FaultRuleId,
            record.AuthenticatedUser);
}

internal sealed class ServerOperationsCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    StorageHealth storage,
    ServerDiagnostics diagnostics,
    ServerHostingOptions hosting,
    string? storageDirectory,
    IExtensionHealthSource extensionHealth)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits), IServerOperationsCapability
{
    public string Mode => hosting.Mode.ToString().ToLowerInvariant();

    public ValueTask<CapabilityStorageReport> GetReadinessAsync(CancellationToken token) =>
        Gate(BuiltInCapabilityNames.OperationsQuery)
            .InvokeAsync(
                "readiness",
                _ =>
                {
                    var storageReport = Map(storage.GetReadiness());
                    if (!storageReport.Ready)
                    {
                        return ValueTask.FromResult(storageReport);
                    }

                    var extensions = extensionHealth.GetHealth();
                    return ValueTask.FromResult(
                        extensions.Status == "healthy"
                            ? storageReport
                            : storageReport with
                            {
                                Ready = extensions.Ready,
                                Status = extensions.Status,
                                Dependency = "extensions",
                                Reason = extensions.Reason
                            });
                },
                token);

    public ValueTask<CapabilityStorageReport> GetStorageReportAsync(CancellationToken token) =>
        Gate(BuiltInCapabilityNames.OperationsQuery)
            .InvokeAsync("storage-health", _ => ValueTask.FromResult(Map(storage.GetReport())), token);

    public ValueTask<CapabilityDiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken token) =>
        Gate(BuiltInCapabilityNames.OperationsQuery)
            .InvokeAsync(
                "diagnostics",
                _ => ValueTask.FromResult(new CapabilityDiagnosticsSnapshot(
                    diagnostics.RequestCount,
                    diagnostics.FailedRequestCount,
                    diagnostics.PublishedPackageCount,
                    diagnostics.StorageFailureCount)),
                token);

    public ValueTask<StorageBackupManifest?> CreateBackupAsync(
        OperationExecutionContext context,
        StreamHandle destination,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.BackupInvoke)
            .InvokeAsync(
                "create",
                async ct =>
                {
                    if (storageDirectory is null)
                    {
                        return null;
                    }

                    var file = context.Content.Resolve(destination).FilePath
                        ?? throw new InvalidOperationException(
                            "Backup requires a kernel-issued file handle.");
                    return await StorageBackup.CreateAsync(storageDirectory, file, ct);
                },
                token);

    public ValueTask<StorageBackupManifest?> RestoreAsync(
        OperationExecutionContext context,
        StreamHandle source,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.RestoreInvoke)
            .InvokeAsync(
                "restore",
                async ct =>
                {
                    if (storageDirectory is null)
                    {
                        return null;
                    }

                    var file = context.Content.Resolve(source).FilePath
                        ?? throw new InvalidOperationException(
                            "Restore requires a kernel-issued file handle.");
                    return await StorageBackup.RestoreAsync(file, storageDirectory, ct);
                },
                token);

    private static CapabilityStorageReport Map(StorageHealthReport report) =>
        new(
            report.Ready,
            report.Status,
            report.Dependency,
            report.Reason,
            report.PackageCount,
            report.StorageBytes,
            report.VulnerabilitySnapshotCount,
            report.VulnerabilitySnapshotRetentionLimit);
}

internal sealed class VulnerabilityReadCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    VulnerabilitySnapshotProvider snapshots)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits),
        IVulnerabilityReadCapability
{
    public VulnerabilitySnapshot Active =>
        Gate(BuiltInCapabilityNames.VulnerabilityStateRead)
            .InvokeAsync(
                "active",
                _ => ValueTask.FromResult(snapshots.Active),
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public bool TryGet(string snapshotId, out VulnerabilitySnapshot? snapshot)
    {
        var result = Gate(BuiltInCapabilityNames.VulnerabilityStateRead)
            .InvokeAsync(
                "get",
                _ =>
                {
                    var found = snapshots.TryGet(snapshotId, out var value);
                    return ValueTask.FromResult((found, value));
                },
                CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        snapshot = result.value;
        return result.found;
    }
}

/// <summary>
/// The extension-facing vulnerability catalog. It converts kernel snapshot state into
/// documents and registers page payloads as kernel content, so the vulnerability owner
/// never touches a kernel snapshot type or an execution context.
/// </summary>
internal sealed class VulnerabilityCatalogCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    VulnerabilitySnapshotProvider snapshots)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits),
        IVulnerabilityCatalogCapability
{
    public ValueTask<VulnerabilityCatalogDocument> GetActiveAsync(CancellationToken token) =>
        Gate(BuiltInCapabilityNames.VulnerabilityStateRead)
            .InvokeAsync(
                "active",
                _ =>
                {
                    var snapshot = snapshots.Active;
                    return ValueTask.FromResult(new VulnerabilityCatalogDocument(
                        snapshot.Id,
                        snapshot.UpdatedAt,
                        [
                            .. snapshot.Pages.Select(page =>
                                new VulnerabilityCatalogPageDocument(
                                    page.Name,
                                    page.Sha256,
                                    page.UpdatedAt,
                                    page.Comment))
                        ]));
                },
                token);

    public ValueTask<ContentDescriptor?> OpenPageAsync(
        string snapshotId,
        string pageName,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.VulnerabilityStateRead)
            .InvokeAsync(
                "open-page",
                _ =>
                {
                    if (!snapshots.TryGet(snapshotId, out var snapshot) ||
                        !snapshot!.TryGetPage(pageName, out var page))
                    {
                        return ValueTask.FromResult<ContentDescriptor?>(null);
                    }

                    var handle = OperationExecutionScope.Required.Content.RegisterBytes(
                        page!.Content,
                        "application/json");
                    return ValueTask.FromResult<ContentDescriptor?>(new ContentDescriptor(
                        handle,
                        page.Sha256,
                        page.Content.Length,
                        SupportsRanges: false));
                },
                token);
}

/// <summary>
/// The narrow, read-only host clock. It is the kernel-owned capability a separately
/// compiled module may request when it needs a real host read.
/// </summary>
internal sealed class HostClockCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    TimeProvider clock)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits), IHostClockCapability
{
    public ValueTask<DateTimeOffset> GetUtcNowAsync(CancellationToken token) =>
        Gate(BuiltInCapabilityNames.HostClockRead).InvokeAsync(
            "utc-now",
            _ => ValueTask.FromResult(clock.GetUtcNow()),
            token);
}

internal sealed class TypedEventPublisher(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    ServerDiagnostics diagnostics)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits), ITypedEventPublisher
{
    public async ValueTask PublishAsync(KernelEventKind kind, CancellationToken token) =>
        await Gate(BuiltInCapabilityNames.EventsPublish)
            .InvokeAsync(
                kind.ToString(),
                _ =>
                {
                    if (kind == KernelEventKind.PackagePublished)
                    {
                        diagnostics.RecordPackagePublished();
                    }

                    return ValueTask.FromResult(true);
                },
                token);
}

internal sealed class ExtensionStateCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    ExtensionStateStore store)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits),
        IExtensionStateCapability
{
    public ValueTask<T?> ReadAsync<T>(string key, CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ExtensionStateRead)
            .InvokeAsync(
                "read",
                ct => store.ReadAsync<T>(OwnerId, key, ct, MaximumStreamBytes),
                token);

    public async ValueTask WriteAsync<T>(string key, T value, CancellationToken token) =>
        await Gate(BuiltInCapabilityNames.ExtensionStateWrite)
            .InvokeAsync(
                "write",
                async ct =>
                {
                    await store.WriteAsync(OwnerId, key, value, ct, MaximumStreamBytes);
                    return true;
                },
                token);

    public ValueTask<ExtensionStateFileSet?> ReadLegacyFileSetAsync(
        string logicalName,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.ExtensionStateRead)
            .InvokeAsync(
                "read-legacy-file-set",
                ct => store.ReadLegacyFileSetAsync(
                    OwnerId,
                    logicalName,
                    ct,
                    MaximumStreamBytes),
                token);
}

internal sealed class OutboundHttpCapability(
    string hostInstanceId,
    string ownerId,
    ImmutableHashSet<string> grants,
    CapabilityAuditLog audit,
    CapabilityLimits limits,
    KernelOutboundHttpClient client)
    : CapabilityHandle(hostInstanceId, ownerId, grants, audit, limits),
        IOutboundHttpCapability
{
    private const string AllowedHost = "api.nuget.org";

    public ValueTask<OutboundHttpResponse> SendAsync(
        OutboundHttpRequest request,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.OutboundHttp)
            .InvokeAsync(
                "send",
                async ct =>
                {
                    if (request.Uri.Scheme != Uri.UriSchemeHttps ||
                        !string.Equals(request.Uri.Host, AllowedHost, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(request.Method, HttpMethod.Get.Method, StringComparison.Ordinal) ||
                        request.MaximumResponseBytes <= 0 ||
                        request.MaximumResponseBytes > MaximumStreamBytes)
                    {
                        throw new CapabilityDeniedException(
                            OwnerId,
                            BuiltInCapabilityNames.OutboundHttp);
                    }

                    using var message = new HttpRequestMessage(HttpMethod.Get, request.Uri);
                    foreach (var header in request.Headers)
                    {
                        message.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    using var response = await client.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct);
                    try
                    {
                        var declaredLength = response.Content.Headers.ContentLength;
                        if (declaredLength > request.MaximumResponseBytes)
                        {
                            throw new CapabilityStreamLimitExceededException(
                                declaredLength.Value,
                                request.MaximumResponseBytes);
                        }

                        var content = await OutboundHttpContent.ReadBoundedAsync(
                            response.Content,
                            request.MaximumResponseBytes,
                            ct);
                        return new OutboundHttpResponse(
                            (int)response.StatusCode,
                            response.Headers
                                .Concat(response.Content.Headers)
                                .ToImmutableDictionary(
                                    header => header.Key,
                                    header => string.Join(",", header.Value),
                                    StringComparer.OrdinalIgnoreCase),
                            [.. response.Content.Headers.ContentEncoding],
                            declaredLength,
                            content);
                    }

                    catch
                    {
                        response.Dispose();
                        throw;
                    }
                },
                token);
}

internal static class OutboundHttpContent
{
    public static async ValueTask<byte[]> ReadBoundedAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(content);
        await using var source = await content.ReadAsStreamAsync(token);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, token);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > maximumBytes)
            {
                throw new CapabilityStreamLimitExceededException(
                    destination.Length + read,
                    maximumBytes);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }
}

internal sealed class KernelOutboundHttpClient : IDisposable
{
    private readonly HttpClient _client = new(
        new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completion,
        CancellationToken token) =>
        _client.SendAsync(request, completion, token);

    public void Dispose() => _client.Dispose();
}

internal sealed class ResponseLifetimeStream(Stream inner, IDisposable lifetime) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) =>
        inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) =>
        inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
            lifetime.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }
}
