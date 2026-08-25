namespace NuExtVault.Extensions.Sdk;

/// <summary>
/// A kernel-issued opaque handle to content the kernel staged for an extension. The
/// kernel owns the bytes, the storage location, the lease, and the quota; an extension
/// holds only this handle. Handles are bound to one host instance and one extension
/// identity, and every use is audited.
/// </summary>
public sealed record StagedContentHandle(
    string HandleId,
    string ContentType,
    long ContentLength,
    string ContentSha256,
    DateTimeOffset ExpiresAt);

/// <summary>The identity the kernel extracted from staged package content.</summary>
public sealed record StagedPackageIdentity(string PackageId, string PackageVersion);

/// <summary>The outcome of staging content through the kernel.</summary>
public enum StagedContentWriteOutcome
{
    Succeeded,
    QuotaExceeded,
    ContentTooLarge,
    InvalidContent,
    IdentityMismatch,
    Canceled,
    Failed
}

/// <summary>The result of staging content through the kernel.</summary>
public sealed record StagedContentWriteResult(
    StagedContentWriteOutcome Outcome,
    StagedContentHandle? Handle,
    StagedPackageIdentity? Identity,
    string? FailureDetail);

/// <summary>The outcome of releasing staged content.</summary>
public enum StagedContentReleaseOutcome
{
    Released,
    NotFound,
    AlreadyReleased,
    Denied,
    Failed
}

/// <summary>The result of releasing staged content.</summary>
public sealed record StagedContentReleaseResult(
    StagedContentReleaseOutcome Outcome,
    string? FailureDetail);

/// <summary>
/// The extension-facing capability for staging bounded content through the kernel.
/// Content arrives as a kernel-issued <see cref="StreamHandle"/> and is never buffered
/// into an extension contract. The kernel parses and validates package and symbol
/// identity with its own parser, enforces per-owner quotas and leases, and returns an
/// opaque handle. Action-scoped, transport-neutral, bounded, cancellable, and audited.
/// </summary>
public interface IStagedContentWriteCapability
{
    /// <summary>
    /// Stages package content. The kernel validates the archive, extracts the package
    /// identity, and rejects malformed archives, unsafe entry paths, and oversized
    /// content without staging anything.
    /// </summary>
    ValueTask<StagedContentWriteResult> WritePackageAsync(
        StreamHandle content,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stages symbol content for an already staged package. The kernel validates symbol
    /// archive integrity and rejects content whose identity does not match.
    /// </summary>
    ValueTask<StagedContentWriteResult> WriteSymbolsAsync(
        StreamHandle content,
        StagedPackageIdentity expectedIdentity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases staged content and its lease. Releasing is idempotent and never reports
    /// success for content the caller does not own.
    /// </summary>
    ValueTask<StagedContentReleaseResult> ReleaseAsync(
        StagedContentHandle handle,
        CancellationToken cancellationToken);
}

/// <summary>The outcome of an atomic publication request.</summary>
public enum PublicationRequestOutcome
{
    Published,
    Duplicate,
    Quarantined,
    RejectedByPolicy,
    RejectedBySignature,
    RejectedByScanner,
    HandleNotFound,
    HandleExpired,
    StateConcurrencyConflict,
    InvalidContent,
    Unauthorized,
    QuotaExceeded,
    Canceled,
    Failed
}

/// <summary>
/// The authoritative extension-state transition the kernel commits as part of a
/// publication. The extension never applies its own follow-up compare-and-swap.
/// </summary>
public sealed record ExtensionStateTransition<TState>(
    string Key,
    long? ExpectedConcurrencyToken,
    TState Value);

/// <summary>
/// An atomic publication request. The kernel couples staged-handle transition, the
/// declared extension-state compare-and-swap, the authoritative package transaction,
/// audit, and a stable idempotency result behind one recovery journal.
/// </summary>
public sealed record AtomicPublicationRequest<TState>(
    StagedContentHandle PackageContent,
    StagedContentHandle? SymbolContent,
    string IdempotencyKey,
    ExtensionStateTransition<TState> StateTransition);

/// <summary>The result of an atomic publication request.</summary>
public sealed record AtomicPublicationResult(
    PublicationRequestOutcome Outcome,
    string? PackageId,
    string? PackageVersion,
    long? StateConcurrencyToken,
    string IdempotencyKey,
    bool Replayed,
    string? FailureDetail);

/// <summary>
/// The extension-facing capability for atomic package publication through the kernel's
/// quarantine, signature, scanner, and policy pipeline. A retry with the same
/// idempotency key returns the recorded result instead of publishing twice, and a crash
/// can never leave a published package with a still-promotable staging record.
/// </summary>
public interface IAtomicPackagePublicationCapability
{
    ValueTask<AtomicPublicationResult> PublishAsync<TState>(
        AtomicPublicationRequest<TState> request,
        CancellationToken cancellationToken);
}

/// <summary>A versioned state entry with its concurrency token for CAS writes.</summary>
public sealed record TransactionalStateEntry<T>(T Value, long ConcurrencyToken);

/// <summary>The outcome of a transactional state write.</summary>
public enum TransactionalStateWriteOutcome
{
    Written,
    ConcurrencyConflict,
    QuotaExceeded,
    NotFound,
    Invalid
}

/// <summary>The result of a transactional state write.</summary>
public sealed record TransactionalStateWriteResult(
    TransactionalStateWriteOutcome Outcome,
    long ConcurrencyToken,
    string? FailureDetail);

/// <summary>
/// The extension-facing capability for authoritative extension state. The kernel owns
/// the store, its schema registration, quotas, checkpoints, and restore; the extension
/// reads and writes through bounded, audited, action-scoped calls that return typed
/// outcomes instead of throwing.
/// </summary>
public interface ITransactionalStateCapability
{
    /// <summary>Reads a value with its concurrency token, or <c>null</c> when absent.</summary>
    ValueTask<TransactionalStateEntry<T>?> ReadEntryAsync<T>(
        string key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Writes a value. Pass <c>null</c> for <paramref name="expectedConcurrencyToken"/>
    /// to require that the key does not exist yet.
    /// </summary>
    ValueTask<TransactionalStateWriteResult> WriteAsync<T>(
        string key,
        T value,
        long? expectedConcurrencyToken,
        CancellationToken cancellationToken);

    /// <summary>Deletes a value under compare-and-swap.</summary>
    ValueTask<TransactionalStateWriteResult> DeleteAsync(
        string key,
        long expectedConcurrencyToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists this extension's state keys under a prefix, bounded by
    /// <paramref name="take"/>, in ordinal order.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ListKeysAsync(
        string keyPrefix,
        int take,
        CancellationToken cancellationToken);
}
