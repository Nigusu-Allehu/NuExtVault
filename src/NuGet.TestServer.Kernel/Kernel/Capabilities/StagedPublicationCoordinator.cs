using System.Text.Json;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.Kernel.Capabilities;

/// <summary>
/// The kernel-owned command for one journalled publication. The extension supplies only
/// serializable values; the kernel owns the bytes, the transaction, and the state write.
/// </summary>
internal sealed record StagedPublicationCommand(
    string StagedContentId,
    string? StagedSymbolContentId,
    string IdempotencyKey,
    string StateKey,
    long? ExpectedStateToken,
    byte[] StatePayload);

/// <summary>
/// The kernel component that owns staged content promotion. It couples staged content,
/// the recovery journal, the authoritative package transaction, the extension state
/// compare-and-swap, and audit so an extension never mutates authoritative state and
/// never has to compensate for a partial failure.
/// </summary>
internal sealed class StagedPublicationCoordinator(
    string hostInstanceId,
    StagedContentStore staged,
    PublicationJournal journal,
    TransactionalStateStore state,
    IPackageStore packages,
    Func<PackageSupplyChainService> supplyChain,
    PackageTransferLimits packageLimits,
    ServerDiagnostics diagnostics,
    TimeProvider clock)
{
    internal StagedContentStore Content => staged;

    internal PublicationJournal Journal => journal;

    public async ValueTask<StagedContentWriteResult> StagePackageAsync(
        string ownerId,
        Stream content,
        long maximumBytes,
        CancellationToken token)
    {
        var written = await staged.WriteAsync(
            ownerId,
            content,
            "application/octet-stream",
            maximumBytes,
            packageId: null,
            packageVersion: null,
            token);
        if (written.Status != StagedContentWriteStatus.Succeeded || written.Record is null)
        {
            return Rejected(written);
        }

        TestPackage? package = null;
        try
        {
            await using var stream = staged.Open(written.Record);
            package = await TestPackage.FromStreamAsync(
                stream,
                packageLimits,
                clock,
                token);
            var identified = written.Record with
            {
                PackageId = package.Identity.Id,
                PackageVersion = package.NormalizedVersion
            };
            await staged.AnnotateAsync(identified, token);
            return new StagedContentWriteResult(
                StagedContentWriteOutcome.Succeeded,
                Handle(identified),
                new StagedPackageIdentity(package.Identity.Id, package.NormalizedVersion),
                null);
        }
        catch (InvalidPackageException exception)
        {
            await staged.TransitionAsync(
                ownerId,
                written.Record.ContentId,
                StagedContentState.Released,
                CancellationToken.None);
            return new StagedContentWriteResult(
                StagedContentWriteOutcome.InvalidContent,
                null,
                null,
                Redact(exception.Message));
        }
        catch (PackageLimitExceededException exception)
        {
            await staged.TransitionAsync(
                ownerId,
                written.Record.ContentId,
                StagedContentState.Released,
                CancellationToken.None);
            return new StagedContentWriteResult(
                StagedContentWriteOutcome.ContentTooLarge,
                null,
                null,
                Redact(exception.Message));
        }
        finally
        {
            package?.Dispose();
        }
    }

    public async ValueTask<StagedContentWriteResult> StageSymbolsAsync(
        string ownerId,
        Stream content,
        StagedPackageIdentity expectedIdentity,
        long maximumBytes,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        var written = await staged.WriteAsync(
            ownerId,
            content,
            "application/octet-stream",
            maximumBytes,
            expectedIdentity.PackageId,
            expectedIdentity.PackageVersion,
            token);
        if (written.Status != StagedContentWriteStatus.Succeeded || written.Record is null)
        {
            return Rejected(written);
        }

        TestPackage? symbols = null;
        try
        {
            await using var stream = staged.Open(written.Record);
            symbols = await TestPackage.FromStreamAsync(stream, packageLimits, clock, token);
            if (!string.Equals(
                    symbols.Identity.Id,
                    expectedIdentity.PackageId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    symbols.NormalizedVersion,
                    KernelPackageVersion(expectedIdentity.PackageVersion),
                    StringComparison.OrdinalIgnoreCase))
            {
                await staged.TransitionAsync(
                    ownerId,
                    written.Record.ContentId,
                    StagedContentState.Released,
                    CancellationToken.None);
                return new StagedContentWriteResult(
                    StagedContentWriteOutcome.IdentityMismatch,
                    null,
                    null,
                    "The symbol package identity does not match the staged package.");
            }

            return new StagedContentWriteResult(
                StagedContentWriteOutcome.Succeeded,
                Handle(written.Record),
                new StagedPackageIdentity(symbols.Identity.Id, symbols.NormalizedVersion),
                null);
        }
        catch (InvalidPackageException exception)
        {
            await staged.TransitionAsync(
                ownerId,
                written.Record.ContentId,
                StagedContentState.Released,
                CancellationToken.None);
            return new StagedContentWriteResult(
                StagedContentWriteOutcome.InvalidContent,
                null,
                null,
                Redact(exception.Message));
        }
        catch (PackageLimitExceededException exception)
        {
            await staged.TransitionAsync(
                ownerId,
                written.Record.ContentId,
                StagedContentState.Released,
                CancellationToken.None);
            return new StagedContentWriteResult(
                StagedContentWriteOutcome.ContentTooLarge,
                null,
                null,
                Redact(exception.Message));
        }
        finally
        {
            symbols?.Dispose();
        }
    }

    public async ValueTask<StagedContentReleaseResult> ReleaseAsync(
        string ownerId,
        string contentId,
        CancellationToken token)
    {
        var record = staged.Find(ownerId, contentId);
        if (record is null)
        {
            return new StagedContentReleaseResult(
                StagedContentReleaseOutcome.NotFound,
                "No staged content matches the handle for this extension.");
        }

        if (record.State != StagedContentState.Staged)
        {
            return new StagedContentReleaseResult(
                StagedContentReleaseOutcome.AlreadyReleased,
                null);
        }

        return await staged.TransitionAsync(
            ownerId,
            contentId,
            StagedContentState.Released,
            token)
            ? new StagedContentReleaseResult(StagedContentReleaseOutcome.Released, null)
            : new StagedContentReleaseResult(
                StagedContentReleaseOutcome.AlreadyReleased,
                null);
    }

    /// <summary>
    /// Promotes staged content. The journal is written before the package transaction
    /// starts and the outcome is recorded before any dependent state moves, so a retry
    /// replays the recorded result and a crash is finished deterministically by
    /// <see cref="RecoverAsync"/>.
    /// </summary>
    public async ValueTask<AtomicPublicationResult> PublishAsync(
        string ownerId,
        StagedPublicationCommand command,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            return Failure(
                PublicationRequestOutcome.InvalidContent,
                command.IdempotencyKey ?? string.Empty,
                "A publication request requires an idempotency key.");
        }

        if (journal.Find(ownerId, command.IdempotencyKey) is
            { Phase: PublicationJournalPhase.Committed } replayed)
        {
            return replayed.StagedContentId == command.StagedContentId &&
                   replayed.StagedSymbolContentId == command.StagedSymbolContentId &&
                   replayed.StateKey == command.StateKey &&
                   replayed.StatePayload == Convert.ToBase64String(command.StatePayload)
                ? Replay(replayed)
                : Failure(
                    PublicationRequestOutcome.InvalidContent,
                    command.IdempotencyKey,
                    "The idempotency key is already bound to a different publication request.");
        }

        var record = staged.Find(ownerId, command.StagedContentId);
        if (record is null || record.State == StagedContentState.Released)
        {
            return Failure(
                PublicationRequestOutcome.HandleNotFound,
                command.IdempotencyKey,
                "No staged content matches the handle for this extension.");
        }

        if (record.State == StagedContentState.Promoted)
        {
            return Failure(
                PublicationRequestOutcome.HandleNotFound,
                command.IdempotencyKey,
                "The staged content was already promoted.");
        }

        if (staged.IsExpired(record))
        {
            await staged.TransitionAsync(
                ownerId,
                record.ContentId,
                StagedContentState.Released,
                CancellationToken.None);
            return Failure(
                PublicationRequestOutcome.HandleExpired,
                command.IdempotencyKey,
                "The staged content lease expired.");
        }

        StagedContentRecord? symbolRecord = null;
        if (command.StagedSymbolContentId is { Length: > 0 } symbolId)
        {
            symbolRecord = staged.Find(ownerId, symbolId);
            if (symbolRecord is null || symbolRecord.State != StagedContentState.Staged)
            {
                return Failure(
                    PublicationRequestOutcome.HandleNotFound,
                    command.IdempotencyKey,
                    "No staged symbol content matches the handle for this extension.");
            }

            if (staged.IsExpired(symbolRecord))
            {
                return Failure(
                    PublicationRequestOutcome.HandleExpired,
                    command.IdempotencyKey,
                    "The staged symbol lease expired.");
            }

            if (!string.Equals(
                    symbolRecord.PackageId,
                    record.PackageId,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    KernelPackageVersion(symbolRecord.PackageVersion!),
                    KernelPackageVersion(record.PackageVersion!),
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure(
                    PublicationRequestOutcome.InvalidContent,
                    command.IdempotencyKey,
                    "The staged symbol identity does not match the staged package.");
            }
        }

        // Fail closed before publishing when the declared state transition can no longer
        // apply: a losing compare-and-swap must never publish a package.
        var current = await state.ReadAsync(ownerId, command.StateKey, token);
        if (current?.ETag != command.ExpectedStateToken)
        {
            return Failure(
                PublicationRequestOutcome.StateConcurrencyConflict,
                command.IdempotencyKey,
                "The staging record changed while the promotion was in flight.");
        }

        TestPackage? package;
        try
        {
            await using var stream = staged.Open(record);
            package = await TestPackage.FromStreamAsync(stream, packageLimits, clock, token);
        }
        catch (InvalidPackageException exception)
        {
            return Failure(
                PublicationRequestOutcome.InvalidContent,
                command.IdempotencyKey,
                Redact(exception.Message));
        }
        catch (PackageLimitExceededException exception)
        {
            return Failure(
                PublicationRequestOutcome.QuotaExceeded,
                command.IdempotencyKey,
                Redact(exception.Message));
        }

        var entry = new PublicationJournalEntry(
            Guid.NewGuid().ToString("N"),
            ownerId,
            command.IdempotencyKey,
            record.ContentId,
            symbolRecord?.ContentId,
            command.StateKey,
            command.ExpectedStateToken,
            package.Identity.Id,
            package.NormalizedVersion,
            record.ContentSha256,
            PublicationJournalPhase.Pending,
            PublicationRequestOutcome.Failed.ToString(),
            null,
            null,
            Convert.ToBase64String(command.StatePayload),
            clock.GetUtcNow(),
            clock.GetUtcNow());
        var (journalled, created) = await journal.BeginAsync(entry, token);
        if (!created)
        {
            package.Dispose();
            if (!MatchesRequest(journalled, entry))
            {
                return Failure(
                    PublicationRequestOutcome.InvalidContent,
                    command.IdempotencyKey,
                    "The idempotency key is already bound to a different publication request.");
            }

            return journalled.Phase switch
            {
                PublicationJournalPhase.Committed => Replay(journalled),
                PublicationJournalPhase.Resolved => await FinishAsync(journalled, token),
                _ => await ResolvePendingAsync(journalled, abortIfMissing: false, token)
            };
        }

        PackagePublicationResult result;
        PublicationJournalEntry resolved;
        var addedSymbol = false;
        try
        {
            var conditional = await state.ExecuteConditionalWriteAsync(
                ownerId,
                command.StateKey,
                command.StatePayload,
                command.ExpectedStateToken,
                async cancellationToken =>
                {
                    if (symbolRecord is not null)
                    {
                        addedSymbol = await StoreSymbolAsync(symbolRecord, cancellationToken);
                    }

                    var publication = await supplyChain().PublishAsync(
                        new PackagePublicationRequest(
                            package,
                            ownerId,
                            "staging",
                            Administrator: true),
                        cancellationToken);
                    var publicationOutcome = Map(publication.Outcome);
                    await journal.ResolveAsync(
                        journalled,
                        publicationOutcome.ToString(),
                        publicationOutcome == PublicationRequestOutcome.Published
                            ? null
                            : Redact(publication.Message),
                        cancellationToken);
                    return publication;
                },
                publication =>
                {
                    var publicationOutcome = Map(publication.Outcome);
                    return publicationOutcome is PublicationRequestOutcome.Published
                        or PublicationRequestOutcome.Duplicate;
                },
                token);
            result = conditional.Result;
            resolved = journal.Find(ownerId, command.IdempotencyKey)!;
            if (addedSymbol &&
                Map(result.Outcome) is not PublicationRequestOutcome.Published
                    and not PublicationRequestOutcome.Duplicate
                    and not PublicationRequestOutcome.Quarantined)
            {
                await packages.DeleteStoredSymbolAsync(
                    symbolRecord!.PackageId!,
                    symbolRecord.PackageVersion!,
                    CancellationToken.None);
            }
        }
        catch (StateConcurrencyException)
        {
            package.Dispose();
            if (addedSymbol)
            {
                await packages.DeleteStoredSymbolAsync(
                    symbolRecord!.PackageId!,
                    symbolRecord.PackageVersion!,
                    CancellationToken.None);
            }
            await journal.AbortAsync(journalled, CancellationToken.None);
            return Failure(
                PublicationRequestOutcome.StateConcurrencyConflict,
                command.IdempotencyKey,
                "The staging record changed while the promotion was in flight.");
        }
        catch (OperationCanceledException)
        {
            package.Dispose();
            if (addedSymbol)
            {
                await packages.DeleteStoredSymbolAsync(
                    symbolRecord!.PackageId!,
                    symbolRecord.PackageVersion!,
                    CancellationToken.None);
            }
            return Failure(
                PublicationRequestOutcome.Canceled,
                command.IdempotencyKey,
                "The publication was interrupted. Recovery will reconcile its outcome.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            package.Dispose();
            if (addedSymbol)
            {
                await packages.DeleteStoredSymbolAsync(
                    symbolRecord!.PackageId!,
                    symbolRecord.PackageVersion!,
                    CancellationToken.None);
            }
            diagnostics.RecordStorageFailure();
            return Failure(
                PublicationRequestOutcome.Failed,
                command.IdempotencyKey,
                "The publication was interrupted by a storage failure. Recovery will " +
                "reconcile its outcome.");
        }
        var outcome = Map(result.Outcome);
        if (outcome == PublicationRequestOutcome.Published)
        {
            diagnostics.RecordPackagePublished();
        }

        return await FinishAsync(resolved, token);
    }

    /// <summary>
    /// Finishes every journal entry an interrupted host left behind. Entries whose
    /// outcome was never recorded are resolved from the authoritative package state, so
    /// recovery never republishes and never leaves a promotable staging record for a
    /// package that is already published.
    /// </summary>
    public async ValueTask RecoverAsync(CancellationToken token)
    {
        foreach (var entry in journal.ReadUnfinished())
        {
            if (entry.Phase == PublicationJournalPhase.Pending)
            {
                await ResolvePendingAsync(entry, abortIfMissing: true, token);
                continue;
            }

            await FinishAsync(entry, token);
        }
    }

    private async ValueTask<AtomicPublicationResult> ResolvePendingAsync(
        PublicationJournalEntry entry,
        bool abortIfMissing,
        CancellationToken token)
    {
        var status = await supplyChain().GetStatusAsync(
            entry.PackageId,
            entry.PackageVersion,
            token);
        if (status is null)
        {
            if (abortIfMissing)
            {
                await journal.AbortAsync(entry, token);
                return Failure(
                    PublicationRequestOutcome.Failed,
                    entry.IdempotencyKey,
                    "The interrupted publication did not mutate authoritative package state.");
            }

            return Failure(
                PublicationRequestOutcome.StateConcurrencyConflict,
                entry.IdempotencyKey,
                "A publication with this idempotency key is still in progress.");
        }

        if (!string.Equals(status.ContentHash, entry.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Authoritative package content conflicts with the staged publication journal.");
        }

        var outcome = status.State switch
        {
            PackageModerationState.Published => PublicationRequestOutcome.Published,
            PackageModerationState.Quarantined => PublicationRequestOutcome.Quarantined,
            PackageModerationState.Rejected => PublicationRequestOutcome.RejectedByPolicy,
            _ => PublicationRequestOutcome.Failed
        };
        var resolved = await journal.ResolveAsync(
            entry,
            outcome.ToString(),
            outcome == PublicationRequestOutcome.Published
                ? null
                : "The publication was interrupted and did not complete.",
            token);
        return await FinishAsync(resolved, token);
    }

    /// <summary>
    /// Applies everything that must follow a recorded publication outcome: the staged
    /// transition and, on success, the declared extension-state compare-and-swap. It is
    /// idempotent, so calling it after a crash produces the same committed entry.
    /// </summary>
    private async ValueTask<AtomicPublicationResult> FinishAsync(
        PublicationJournalEntry entry,
        CancellationToken token)
    {
        if (entry.Phase == PublicationJournalPhase.Committed)
        {
            return Replay(entry);
        }

        var outcome = Enum.TryParse<PublicationRequestOutcome>(entry.Outcome, out var parsed)
            ? parsed
            : PublicationRequestOutcome.Failed;
        long? stateToken = null;
        if (outcome == PublicationRequestOutcome.Published ||
            outcome == PublicationRequestOutcome.Duplicate)
        {
            await staged.TransitionAsync(
                entry.OwnerId,
                entry.StagedContentId,
                StagedContentState.Promoted,
                CancellationToken.None);
            if (entry.StagedSymbolContentId is { Length: > 0 } symbolId)
            {
                var symbol = staged.Find(entry.OwnerId, symbolId);
                if (symbol is null)
                {
                    return new AtomicPublicationResult(
                        PublicationRequestOutcome.HandleNotFound,
                        entry.PackageId,
                        entry.PackageVersion,
                        null,
                        entry.IdempotencyKey,
                        false,
                        "The staged symbol content is no longer available.");
                }

                await StoreSymbolAsync(symbol, token);
                await staged.TransitionAsync(
                    entry.OwnerId,
                    symbolId,
                    StagedContentState.Promoted,
                    CancellationToken.None);
            }

            stateToken = await ApplyStateAsync(entry, token);
            if (stateToken is null)
            {
                return new AtomicPublicationResult(
                    PublicationRequestOutcome.StateConcurrencyConflict,
                    entry.PackageId,
                    entry.PackageVersion,
                    null,
                    entry.IdempotencyKey,
                    false,
                    "The package was published, but the staging record changed before " +
                    "the terminal state could be recorded. Recovery will retry the transition.");
            }
        }

        var committed = await journal.CommitAsync(entry, stateToken, token);
        return Replay(committed) with { Replayed = false };
    }

    private async ValueTask<bool> StoreSymbolAsync(
        StagedContentRecord symbol,
        CancellationToken token)
    {
        if (await packages.FindStoredSymbolAsync(symbol.PackageId!, symbol.PackageVersion!, token)
            is { } existing)
        {
            await using var source = staged.Open(symbol);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, token);
            if (!existing.AsSpan().SequenceEqual(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
            {
                throw new InvalidDataException(
                    "Published symbol content conflicts with the staged symbol content.");
            }

            return false;
        }

        await using var stream = staged.Open(symbol);
        using var content = new MemoryStream(
            symbol.ContentLength > 0 && symbol.ContentLength <= int.MaxValue
                ? (int)symbol.ContentLength
                : 0);
        await stream.CopyToAsync(content, token);
        await packages.AddSymbolAsync(content.ToArray(), token);
        return true;
    }

    private async ValueTask<long?> ApplyStateAsync(
        PublicationJournalEntry entry,
        CancellationToken token)
    {
        var payload = Convert.FromBase64String(entry.StatePayload);
        try
        {
            var record = await state.WriteAsync(
                entry.OwnerId,
                entry.StateKey,
                payload,
                entry.ExpectedStateToken,
                token);
            return record.ETag;
        }
        catch (StateConcurrencyException)
        {
            // The transition may already have been applied before an interruption. The
            // kernel adopts the stored record only when it is byte-identical to the
            // declared transition; anything else fails closed and stays uncommitted.
            var current = await state.ReadAsync(entry.OwnerId, entry.StateKey, token);
            return current is not null && current.Value.AsSpan().SequenceEqual(payload)
                ? current.ETag
                : null;
        }
        catch (StateQuotaExceededException)
        {
            return null;
        }
    }

    private static AtomicPublicationResult Replay(PublicationJournalEntry entry) =>
        new(
            Enum.TryParse<PublicationRequestOutcome>(entry.Outcome, out var parsed)
                ? parsed
                : PublicationRequestOutcome.Failed,
            entry.PackageId,
            entry.PackageVersion,
            entry.StateToken,
            entry.IdempotencyKey,
            true,
            entry.FailureDetail);

    private static AtomicPublicationResult Failure(
        PublicationRequestOutcome outcome,
        string idempotencyKey,
        string detail) =>
        new(outcome, null, null, null, idempotencyKey, false, detail);

    private static bool MatchesRequest(
        PublicationJournalEntry existing,
        PublicationJournalEntry requested) =>
        existing.StagedContentId == requested.StagedContentId &&
        existing.StagedSymbolContentId == requested.StagedSymbolContentId &&
        existing.StateKey == requested.StateKey &&
        existing.ExpectedStateToken == requested.ExpectedStateToken &&
        existing.PackageId == requested.PackageId &&
        existing.PackageVersion == requested.PackageVersion &&
        existing.ContentSha256 == requested.ContentSha256 &&
        existing.StatePayload == requested.StatePayload;

    private static StagedContentWriteResult Rejected(StagedContentWriteOutput written) =>
        new(
            written.Status == StagedContentWriteStatus.QuotaExceeded
                ? StagedContentWriteOutcome.QuotaExceeded
                : StagedContentWriteOutcome.ContentTooLarge,
            null,
            null,
            written.Detail);

    private static StagedContentHandle Handle(StagedContentRecord record) =>
        new(
            record.ContentId,
            record.ContentType,
            record.ContentLength,
            record.ContentSha256,
            record.ExpiresAt);

    private static string KernelPackageVersion(string version) =>
        NuGet.Versioning.NuGetVersion.TryParse(version, out var parsed)
            ? TestPackage.NormalizeVersion(parsed)
            : version.ToLowerInvariant();

    private static PublicationRequestOutcome Map(PackagePublicationOutcome outcome) =>
        outcome switch
        {
            PackagePublicationOutcome.Published => PublicationRequestOutcome.Published,
            PackagePublicationOutcome.Duplicate => PublicationRequestOutcome.Duplicate,
            PackagePublicationOutcome.Quarantined => PublicationRequestOutcome.Quarantined,
            PackagePublicationOutcome.Rejected => PublicationRequestOutcome.RejectedByPolicy,
            PackagePublicationOutcome.Unauthorized => PublicationRequestOutcome.Unauthorized,
            PackagePublicationOutcome.QuotaExceeded => PublicationRequestOutcome.QuotaExceeded,
            _ => PublicationRequestOutcome.Failed
        };

    /// <summary>
    /// Keeps failure detail free of storage paths and internal type names so a staging
    /// caller can never learn where the kernel keeps its content.
    /// </summary>
    private static string Redact(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "The request failed.";
        }

        var trimmed = detail.Trim();
        return trimmed.Contains(Path.DirectorySeparatorChar) ||
               trimmed.Contains(Path.AltDirectorySeparatorChar) ||
               trimmed.Contains("NuGet.TestServer.", StringComparison.Ordinal)
            ? "The request failed."
            : trimmed.Length > 512 ? trimmed[..512] : trimmed;
    }

    internal string HostInstanceId => hostInstanceId;

    internal static byte[] Serialize<TState>(TState value) =>
        JsonSerializer.SerializeToUtf8Bytes(value);
}
