using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Abstractions;

/// <summary>
/// The kernel capability contracts an extension may consume. The kernel owns every
/// implementation; only these action-scoped, serializable declarations cross the
/// assembly boundary, so the official extension assembly never references the kernel.
/// </summary>
internal interface IPackageMetadataReadCapability
{
    ValueTask<ImmutableArray<string>> GetReadableVersionsAsync(
        string packageId,
        CancellationToken token);

    ValueTask<ContentDescriptor?> OpenNuspecAsync(
        string packageId,
        string version,
        CancellationToken token);

    ValueTask<string?> GetPackageHashAsync(
        string packageId,
        string version,
        CancellationToken token);
}

/// <summary>
/// The extension-facing package content read capability. Package bytes never cross the
/// boundary: the kernel leases a bounded, cancellable stream and returns a handle.
/// </summary>
internal interface IPackageContentReadCapability
{
    ValueTask<ContentDescriptor?> OpenPackageAsync(
        string packageId,
        string version,
        CancellationToken token);
}

/// <summary>
/// The extension-facing symbol read capability. It is separate from package content so
/// symbol access can be granted, denied, and audited on its own.
/// </summary>
internal interface IPackageSymbolReadCapability
{
    ValueTask<ContentDescriptor?> OpenSymbolsAsync(
        string packageId,
        string version,
        CancellationToken token);
}

internal interface IPackagePushCapability
{
    ValueTask<PackagePublicationDocument> PublishAsync(
        StreamHandle content,
        CancellationToken token);
}

internal interface IPackageSymbolsPushCapability
{
    ValueTask<PackageIdentity> StoreAsync(
        StreamHandle content,
        CancellationToken token);
}

internal interface IPackageManagementListCapability
{
    ValueTask<ImmutableArray<PackageSummaryDocument>> QueryAsync(
        string? packageId,
        int skip,
        int take,
        CancellationToken token);
}

internal interface IPackageUnlistCapability
{
    ValueTask<PackageMutationDocument> SetUnlistedAsync(
        PackageIdentity package,
        CancellationToken token);
}

internal interface IPackageRelistCapability
{
    ValueTask<PackageMutationDocument> SetListedAsync(
        PackageIdentity package,
        CancellationToken token);
}

internal interface IPackageDeleteCapability
{
    ValueTask<PackageMutationDocument> DeleteAsync(
        PackageIdentity package,
        string reason,
        CancellationToken token);
}

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

internal sealed record ExtensionHealthSnapshot(bool Ready, string Status, string? Reason);

/// <summary>
/// The health an extension publishes to the kernel readiness aggregate. The kernel owns
/// aggregation; an extension only reports its own state.
/// </summary>
internal interface IExtensionHealthSource
{
    ExtensionHealthSnapshot GetHealth();
}

internal interface IOperationsQueryCapability
{
    ValueTask<OperationsLivenessDocument> GetLivenessAsync(CancellationToken token);

    ValueTask<OperationsReadinessDocument> GetReadinessAsync(CancellationToken token);

    ValueTask<OperationsStorageHealthDocument> GetStorageHealthAsync(CancellationToken token);

    ValueTask<OperationsDiagnosticsDocument> GetDiagnosticsAsync(CancellationToken token);
}

internal interface IBackupCheckpointCapability
{
    ValueTask<BackupManifestDocument?> CreateAsync(
        StreamHandle destination,
        string requestedBy,
        CancellationToken token);
}

internal interface IRestoreCheckpointCapability
{
    ValueTask<BackupManifestDocument?> RestoreAsync(
        StreamHandle source,
        string requestedBy,
        CancellationToken token);
}

/// <summary>
/// The extension-facing vulnerability catalog capability. It returns documents and
/// kernel-issued content descriptors, never a catalog implementation type.
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

/// <summary>
/// A transport-neutral vulnerability page payload. The kernel converts it into a bounded
/// content handle; the catalog owner never hands the kernel a stream.
/// </summary>
internal sealed record VulnerabilityCatalogPageContent(
    ReadOnlyMemory<byte> Content,
    string Sha256);

/// <summary>
/// The host-scoped catalog the kernel reads when it serves the vulnerability capability.
/// The official vulnerability feature owns the state; the kernel owns gating, auditing,
/// limits, and content handles. Implementations are supplied per host instance, never
/// through process-global state.
/// </summary>
internal interface IVulnerabilityCatalogSource
{
    VulnerabilityCatalogDocument GetActiveCatalog();

    bool TryGetPageContent(
        string snapshotId,
        string pageName,
        out VulnerabilityCatalogPageContent? content);

    ImmutableArray<VulnerabilityAdvisoryDocument> FindAdvisories(PackageIdentity package);
}

internal interface IExtensionStateCapability
{
    ValueTask<T?> ReadAsync<T>(string key, CancellationToken token);

    ValueTask<ExtensionStateEntry<T>?> ReadEntryAsync<T>(string key, CancellationToken token);

    ValueTask WriteAsync<T>(string key, T value, CancellationToken token);

    ValueTask<long> WriteEntryAsync<T>(
        string key,
        T value,
        long? expectedConcurrencyToken,
        CancellationToken token);

    ValueTask<ExtensionStateFileSet?> ReadLegacyFileSetAsync(
        string logicalName,
        CancellationToken token);
}

internal sealed record ExtensionStateEntry<T>(T Value, long ConcurrencyToken);

internal sealed record ExtensionStateFile(string LogicalName, byte[] Content);

internal sealed record ExtensionStateFileSet(ImmutableArray<ExtensionStateFile> Files);

/// <summary>
/// The failure an extension observes when its state read or write is rejected.
/// </summary>
internal sealed class ExtensionStateException : Exception
{
    public ExtensionStateException(string message)
        : base(message)
    {
    }

    public ExtensionStateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The failure an extension observes when its state write loses an optimistic
/// concurrency check.
/// </summary>
internal sealed class StateConcurrencyException(string key, long? expectedETag, long? actualETag)
    : Exception(
        $"Extension state key '{key}' expected concurrency token " +
        $"'{expectedETag?.ToString() ?? "none"}' but found " +
        $"'{actualETag?.ToString() ?? "none"}'.")
{
    public string Key { get; } = key;

    public long? ExpectedETag { get; } = expectedETag;

    public long? ActualETag { get; } = actualETag;
}

/// <summary>The failure an extension observes when it exceeds its state quota.</summary>
internal sealed class StateQuotaExceededException(string message) : Exception(message);

/// <summary>
/// The failure an extension observes when persisted state was written by an
/// incompatible schema version.
/// </summary>
internal sealed class StateSchemaCompatibilityException(string message) : Exception(message);

internal interface IOutboundHttpCapability
{
    ValueTask<OutboundHttpResponse> SendAsync(
        OutboundHttpRequest request,
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

/// <summary>
/// The failure an extension observes when it requests an ungranted capability.
/// </summary>
internal sealed class CapabilityDeniedException(string ownerId, string capabilityName)
    : InvalidOperationException(
        $"Owner '{ownerId}' was denied undeclared or ungranted capability '{capabilityName}'.")
{
    public string OwnerId { get; } = ownerId;

    public string CapabilityName { get; } = capabilityName;
}

/// <summary>
/// The failure an extension observes when a capability call exceeds the host's
/// concurrency quota. It is part of the capability contract, not an implementation type.
/// </summary>
internal sealed class CapabilityQuotaExceededException(
    string capabilityName,
    int retryAfterSeconds = 1)
    : InvalidOperationException($"Capability '{capabilityName}' exceeded its concurrency quota.")
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

/// <summary>
/// The failure an extension observes when a capability stream exceeds its declared byte
/// limit.
/// </summary>
internal sealed class CapabilityStreamLimitExceededException(
    long declaredLength,
    long maximumLength)
    : InvalidOperationException(
        $"Capability stream length '{declaredLength}' exceeds limit '{maximumLength}'.")
{
    public long DeclaredLength { get; } = declaredLength;

    public long MaximumLength { get; } = maximumLength;
}
