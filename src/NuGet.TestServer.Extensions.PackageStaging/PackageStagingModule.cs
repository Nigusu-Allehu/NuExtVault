using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;

namespace NuTest.PackageStaging;

/// <summary>
/// The Package Staging extension. It is a separately compiled, independently packable,
/// administrator-installed extension that references only the public extension SDK and
/// is absent from every default server profile. It owns staging groups in kernel
/// extension state, stages content through kernel-issued stream handles, and promotes
/// staged packages through the kernel's publication pipeline. It never writes package
/// tables, never marks packages published, and never touches storage paths or secrets.
/// </summary>
public sealed class PackageStagingModule : IExtensionModule
{
    internal const string ExtensionId = "NuTest.PackageStaging";

    public ExtensionModuleContribution Contribution { get; } =
        ExtensionModuleContribution.FromManifest(
            ExtensionManifestJson.Parse(ReadManifest()));

    public void RegisterRoutes(IRouteBinderRegistry routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.Bind<CreateGroupRequest>(
            new RouteIdentity("nutest.staging.create-group"),
            static (request, _) =>
            {
                var options = StagingJson.ReadGroupOptions(request.ReadBody());
                return ValueTask.FromResult(new CreateGroupRequest(
                    request.GetRoute("groupId"),
                    options.MaximumPackages,
                    options.TtlMinutes));
            });

        routes.Bind<ListGroupsRequest>(
            new RouteIdentity("nutest.staging.list-groups"),
            static (request, _) => ValueTask.FromResult(
                new ListGroupsRequest(StagingJson.ReadTake(request))));

        routes.Bind<GetGroupRequest>(
            new RouteIdentity("nutest.staging.get-group"),
            static (request, _) => ValueTask.FromResult(
                new GetGroupRequest(request.GetRoute("groupId"))));

        routes.Bind<UploadPackageRequest>(
            new RouteIdentity("nutest.staging.upload-package"),
            static (request, _) => ValueTask.FromResult(new UploadPackageRequest(
                request.GetRoute("groupId"),
                StagingJson.ReadIdempotencyKey(request),
                request.BindBodyStream())));

        routes.Bind<UploadSymbolRequest>(
            new RouteIdentity("nutest.staging.upload-symbol"),
            static (request, _) => ValueTask.FromResult(new UploadSymbolRequest(
                request.GetRoute("groupId"),
                request.GetRoute("packageId"),
                request.GetRoute("version"),
                StagingJson.ReadIdempotencyKey(request),
                request.BindBodyStream())));

        routes.Bind<InspectRequest>(
            new RouteIdentity("nutest.staging.inspect"),
            static (request, _) => ValueTask.FromResult(new InspectRequest(
                request.GetRoute("groupId"),
                request.GetRoute("packageId"),
                request.GetRoute("version"))));

        routes.Bind<PromoteRequest>(
            new RouteIdentity("nutest.staging.promote"),
            static (request, _) => ValueTask.FromResult(new PromoteRequest(
                request.GetRoute("groupId"),
                request.GetRoute("packageId"),
                request.GetRoute("version"),
                StagingJson.ReadIdempotencyKey(request))));

        routes.Bind<RejectRequest>(
            new RouteIdentity("nutest.staging.reject"),
            static (request, _) => ValueTask.FromResult(new RejectRequest(
                request.GetRoute("groupId"),
                request.GetRoute("packageId"),
                request.GetRoute("version"),
                StagingJson.ReadReason(request.ReadBody()))));

        routes.Bind<ExpireRequest>(
            new RouteIdentity("nutest.staging.expire"),
            static (request, _) => ValueTask.FromResult(
                new ExpireRequest(request.GetRoute("groupId"))));
    }

    public void RegisterOperations(
        IOperationOwnerRegistry operations,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource contributions)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(capabilities);

        var clock = capabilities.GetRequired<IHostClockCapability>(
            Required("host.clock.read"));
        var state = capabilities.GetRequired<ITransactionalStateCapability>(
            Required("extension-state.read"));
        var content = capabilities.GetRequired<IStagedContentWriteCapability>(
            Required("packages.content.write-staged"));
        var publication = capabilities.GetRequired<IAtomicPackagePublicationCapability>(
            Required("publication.request"));
        var handler = new PackageStagingHandler(clock, state, content, publication);

        operations.RegisterNew<CreateGroupRequest, CreateGroupResponse>(
            ExtensionId,
            new OperationIdentity("NuTest.PackageStaging.CreateGroup"),
            handler.CreateGroupAsync);
        operations.RegisterNew<ListGroupsRequest, ListGroupsResponse>(
            ExtensionId,
            new OperationIdentity("NuTest.PackageStaging.ListGroups"),
            handler.ListGroupsAsync);
        operations.RegisterNew<GetGroupRequest, GetGroupResponse>(
            ExtensionId,
            new OperationIdentity("NuTest.PackageStaging.GetGroup"),
            handler.GetGroupAsync);
        operations.RegisterNew<UploadPackageRequest, UploadPackageResponse>(
            ExtensionId,
            new OperationIdentity("NuTest.PackageStaging.UploadPackage"),
            handler.UploadPackageAsync);
        operations.RegisterNew<UploadSymbolRequest, UploadSymbolResponse>(
            ExtensionId,
            new OperationIdentity("NuTest.PackageStaging.UploadSymbol"),
            handler.UploadSymbolAsync);
        operations.RegisterNew<InspectRequest, InspectResponse>(
            ExtensionId,
            new OperationIdentity("NuTest.PackageStaging.Inspect"),
            handler.InspectAsync);
        operations.RegisterNew<PromoteRequest, PromoteResponse>(
            ExtensionId,
            new OperationIdentity("NuTest.PackageStaging.Promote"),
            handler.PromoteAsync);
        operations.RegisterNew<RejectRequest, RejectResponse>(
            ExtensionId,
            new OperationIdentity("NuTest.PackageStaging.Reject"),
            handler.RejectAsync);
        operations.RegisterNew<ExpireRequest, ExpireResponse>(
            ExtensionId,
            new OperationIdentity("NuTest.PackageStaging.Expire"),
            handler.ExpireAsync);
    }

    private static CapabilityRequest Required(string name) =>
        new(new CapabilityIdentity(name), CapabilityRequirement.Required);

    private static byte[] ReadManifest()
    {
        using var stream = typeof(PackageStagingModule).Assembly
            .GetManifestResourceStream("extension-manifest.json")
            ?? throw new InvalidOperationException(
                "The Package Staging manifest resource is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

/// <summary>
/// The staging workflow. Every operation returns a typed outcome; nothing reports
/// success for a missing group, a lost compare-and-swap, or a failed promotion.
/// </summary>
internal sealed class PackageStagingHandler(
    IHostClockCapability clock,
    ITransactionalStateCapability state,
    IStagedContentWriteCapability content,
    IAtomicPackagePublicationCapability publication)
{
    internal const string GroupKeyPrefix = "group.";
    private const int DefaultMaximumPackages = 50;
    private const int DefaultTtlMinutes = 1440;
    private const int MaximumGroupsPerPage = 200;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    internal async ValueTask<OperationResponse<CreateGroupResponse>> CreateGroupAsync(
        CreateGroupRequest request,
        CancellationToken token)
    {
        using var mutation = await EnterMutationAsync(token);
        if (!IsValidGroupId(request.GroupId))
        {
            return Ok(new CreateGroupResponse(
                StagingOutcome.InvalidContent,
                request.GroupId,
                StagingGroupStatus.Expired,
                default,
                "Group identifiers must be short, token-shaped names."));
        }

        var now = await clock.GetUtcNowAsync(token);
        var ttl = Math.Clamp(request.TtlMinutes ?? DefaultTtlMinutes, 1, DefaultTtlMinutes);
        var maximum = Math.Clamp(
            request.MaximumPackages ?? DefaultMaximumPackages,
            1,
            DefaultMaximumPackages);
        var group = new StagingGroupState(
            request.GroupId,
            StagingGroupStatus.Active,
            now,
            now.AddMinutes(ttl),
            maximum,
            []);
        var write = await state.WriteAsync(Key(request.GroupId), group, null, token);
        return write.Outcome switch
        {
            TransactionalStateWriteOutcome.Written => Ok(new CreateGroupResponse(
                StagingOutcome.Succeeded,
                group.GroupId,
                group.Status,
                group.ExpiresAt,
                null)),
            TransactionalStateWriteOutcome.ConcurrencyConflict => Ok(new CreateGroupResponse(
                StagingOutcome.Conflict,
                request.GroupId,
                StagingGroupStatus.Active,
                default,
                "The staging group already exists.")),
            TransactionalStateWriteOutcome.QuotaExceeded => Ok(new CreateGroupResponse(
                StagingOutcome.QuotaExceeded,
                request.GroupId,
                StagingGroupStatus.Active,
                default,
                write.FailureDetail)),
            _ => Ok(new CreateGroupResponse(
                StagingOutcome.Failed,
                request.GroupId,
                StagingGroupStatus.Active,
                default,
                write.FailureDetail))
        };
    }

    internal async ValueTask<OperationResponse<ListGroupsResponse>> ListGroupsAsync(
        ListGroupsRequest request,
        CancellationToken token)
    {
        var take = Math.Clamp(request.Take <= 0 ? 50 : request.Take, 1, MaximumGroupsPerPage);
        var keys = await state.ListKeysAsync(GroupKeyPrefix, take, token);
        var groups = ImmutableArray.CreateBuilder<StagingGroupView>();
        foreach (var key in keys)
        {
            var entry = await state.ReadEntryAsync<StagingGroupState>(key, token);
            if (entry is not null)
            {
                groups.Add(View(entry.Value, entry.ConcurrencyToken));
            }
        }

        return Ok(new ListGroupsResponse(StagingOutcome.Succeeded, groups.ToImmutable()));
    }

    internal async ValueTask<OperationResponse<GetGroupResponse>> GetGroupAsync(
        GetGroupRequest request,
        CancellationToken token)
    {
        using var mutation = await EnterMutationAsync(token);
        if (!IsValidGroupId(request.GroupId))
        {
            return Ok(new GetGroupResponse(
                StagingOutcome.GroupNotFound,
                null,
                "No staging group matches that identifier."));
        }

        var entry = await state.ReadEntryAsync<StagingGroupState>(Key(request.GroupId), token);
        return entry is null
            ? Ok(new GetGroupResponse(
                StagingOutcome.GroupNotFound,
                null,
                "No staging group matches that identifier."))
            : Ok(new GetGroupResponse(
                StagingOutcome.Succeeded,
                View(entry.Value, entry.ConcurrencyToken),
                null));
    }

    internal async ValueTask<OperationResponse<UploadPackageResponse>> UploadPackageAsync(
        UploadPackageRequest request,
        CancellationToken token)
    {
        using var mutation = await EnterMutationAsync(token);
        if (!IsValidGroupId(request.GroupId))
        {
            return Ok(new UploadPackageResponse(
                StagingOutcome.GroupNotFound, null, null, null, 0,
                "No staging group matches that identifier."));
        }

        var entry = await state.ReadEntryAsync<StagingGroupState>(Key(request.GroupId), token);
        if (entry is null)
        {
            return Ok(new UploadPackageResponse(
                StagingOutcome.GroupNotFound, null, null, null, 0,
                "No staging group matches that identifier."));
        }

        var now = await clock.GetUtcNowAsync(token);
        var group = entry.Value;
        if (group.Status != StagingGroupStatus.Active)
        {
            return Ok(new UploadPackageResponse(
                StagingOutcome.GroupInactive, null, null, null, 0,
                "The staging group is no longer active."));
        }

        if (now >= group.ExpiresAt)
        {
            return Ok(new UploadPackageResponse(
                StagingOutcome.GroupExpired, null, null, null, 0,
                "The staging group expired."));
        }

        if (request.IdempotencyKey is { Length: > 0 } key &&
            group.Packages.FirstOrDefault(package =>
                string.Equals(package.UploadIdempotencyKey, key, StringComparison.Ordinal))
                is { } replayed)
        {
            return Ok(new UploadPackageResponse(
                StagingOutcome.Succeeded,
                replayed.PackageId,
                replayed.Version,
                replayed.ContentSha256,
                replayed.ContentLength,
                null));
        }

        if (group.Packages.Count(package => package.Status == StagedPackageStatus.Staged) >=
            group.MaximumPackages)
        {
            return Ok(new UploadPackageResponse(
                StagingOutcome.QuotaExceeded, null, null, null, 0,
                "The staging group reached its package quota."));
        }

        var staged = await content.WritePackageAsync(request.Content, token);
        if (staged.Outcome != StagedContentWriteOutcome.Succeeded ||
            staged.Handle is null ||
            staged.Identity is null)
        {
            return Ok(new UploadPackageResponse(
                Map(staged.Outcome), null, null, null, 0, staged.FailureDetail));
        }

        if (group.Packages.Any(package =>
                package.Status == StagedPackageStatus.Staged &&
                string.Equals(package.PackageId, staged.Identity.PackageId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(package.Version, staged.Identity.PackageVersion, StringComparison.OrdinalIgnoreCase)))
        {
            await content.ReleaseAsync(staged.Handle, token);
            return Ok(new UploadPackageResponse(
                StagingOutcome.DuplicatePackage,
                staged.Identity.PackageId,
                staged.Identity.PackageVersion,
                null,
                0,
                "That package version is already staged in this group."));
        }

        var record = new StagedPackageRecord(
            staged.Identity.PackageId,
            staged.Identity.PackageVersion,
            staged.Handle.HandleId,
            staged.Handle.ContentSha256,
            staged.Handle.ContentLength,
            null,
            StagedPackageStatus.Staged,
            now,
            null,
            request.IdempotencyKey,
            null,
            null,
            null,
            null);
        var write = await state.WriteAsync(
            Key(request.GroupId),
            group with { Packages = group.Packages.Add(record) },
            entry.ConcurrencyToken,
            token);
        if (write.Outcome != TransactionalStateWriteOutcome.Written)
        {
            await content.ReleaseAsync(staged.Handle, token);
            return Ok(new UploadPackageResponse(
                write.Outcome == TransactionalStateWriteOutcome.ConcurrencyConflict
                    ? StagingOutcome.Conflict
                    : StagingOutcome.Failed,
                null,
                null,
                null,
                0,
                write.FailureDetail));
        }

        return Ok(new UploadPackageResponse(
            StagingOutcome.Succeeded,
            record.PackageId,
            record.Version,
            record.ContentSha256,
            record.ContentLength,
            null));
    }

    internal async ValueTask<OperationResponse<UploadSymbolResponse>> UploadSymbolAsync(
        UploadSymbolRequest request,
        CancellationToken token)
    {
        using var mutation = await EnterMutationAsync(token);
        if (!IsValidGroupId(request.GroupId))
        {
            return Ok(new UploadSymbolResponse(
                StagingOutcome.GroupNotFound, null, null, null,
                "No staging group matches that identifier."));
        }

        var entry = await state.ReadEntryAsync<StagingGroupState>(Key(request.GroupId), token);
        if (entry is null)
        {
            return Ok(new UploadSymbolResponse(
                StagingOutcome.GroupNotFound, null, null, null,
                "No staging group matches that identifier."));
        }

        var group = entry.Value;
        var index = IndexOf(group, request.PackageId, request.Version);
        if (index < 0)
        {
            return Ok(new UploadSymbolResponse(
                StagingOutcome.PackageNotFound, null, null, null,
                "No staged package matches that identity."));
        }

        var package = group.Packages[index];
        if (package.Status != StagedPackageStatus.Staged)
        {
            return Ok(new UploadSymbolResponse(
                StagingOutcome.AlreadyResolved, null, null, null,
                "The staged package is no longer pending."));
        }

        if (request.IdempotencyKey is { Length: > 0 } idempotencyKey &&
            string.Equals(
                package.SymbolUploadIdempotencyKey,
                idempotencyKey,
                StringComparison.Ordinal) &&
            package.SymbolHandleId is not null)
        {
            return Ok(new UploadSymbolResponse(
                StagingOutcome.Succeeded,
                package.PackageId,
                package.Version,
                package.SymbolContentSha256,
                null));
        }

        var staged = await content.WriteSymbolsAsync(
            request.Content,
            new StagedPackageIdentity(package.PackageId, package.Version),
            token);
        if (staged.Outcome != StagedContentWriteOutcome.Succeeded || staged.Handle is null)
        {
            return Ok(new UploadSymbolResponse(
                Map(staged.Outcome), null, null, null, staged.FailureDetail));
        }

        var write = await state.WriteAsync(
            Key(request.GroupId),
            group with
            {
                Packages = group.Packages.SetItem(
                    index,
                    package with
                    {
                        SymbolHandleId = staged.Handle.HandleId,
                        SymbolUploadIdempotencyKey = request.IdempotencyKey,
                        SymbolContentSha256 = staged.Handle.ContentSha256
                    })
            },
            entry.ConcurrencyToken,
            token);
        if (write.Outcome != TransactionalStateWriteOutcome.Written)
        {
            await content.ReleaseAsync(staged.Handle, token);
            return Ok(new UploadSymbolResponse(
                write.Outcome == TransactionalStateWriteOutcome.ConcurrencyConflict
                    ? StagingOutcome.Conflict
                    : StagingOutcome.Failed,
                null,
                null,
                null,
                write.FailureDetail));
        }

        if (package.SymbolHandleId is { Length: > 0 } previousSymbolHandleId)
        {
            await content.ReleaseAsync(
                new StagedContentHandle(
                    previousSymbolHandleId,
                    "application/octet-stream",
                    0,
                    package.SymbolContentSha256 ?? string.Empty,
                    default),
                token);
        }

        return Ok(new UploadSymbolResponse(
            StagingOutcome.Succeeded,
            package.PackageId,
            package.Version,
            staged.Handle.ContentSha256,
            null));
    }

    internal async ValueTask<OperationResponse<InspectResponse>> InspectAsync(
        InspectRequest request,
        CancellationToken token)
    {
        using var mutation = await EnterMutationAsync(token);
        if (!IsValidGroupId(request.GroupId))
        {
            return Ok(new InspectResponse(
                StagingOutcome.GroupNotFound, null,
                "No staging group matches that identifier."));
        }

        var entry = await state.ReadEntryAsync<StagingGroupState>(Key(request.GroupId), token);
        if (entry is null)
        {
            return Ok(new InspectResponse(
                StagingOutcome.GroupNotFound, null,
                "No staging group matches that identifier."));
        }

        var index = IndexOf(entry.Value, request.PackageId, request.Version);
        return index < 0
            ? Ok(new InspectResponse(
                StagingOutcome.PackageNotFound, null,
                "No staged package matches that identity."))
            : Ok(new InspectResponse(
                StagingOutcome.Succeeded, entry.Value.Packages[index], null));
    }

    internal async ValueTask<OperationResponse<PromoteResponse>> PromoteAsync(
        PromoteRequest request,
        CancellationToken token)
    {
        using var mutation = await EnterMutationAsync(token);
        if (!IsValidGroupId(request.GroupId))
        {
            return Ok(new PromoteResponse(
                StagingOutcome.GroupNotFound, null, null, false,
                "No staging group matches that identifier."));
        }

        var entry = await state.ReadEntryAsync<StagingGroupState>(Key(request.GroupId), token);
        if (entry is null)
        {
            return Ok(new PromoteResponse(
                StagingOutcome.GroupNotFound, null, null, false,
                "No staging group matches that identifier."));
        }

        var group = entry.Value;
        var index = IndexOf(group, request.PackageId, request.Version);
        if (index < 0)
        {
            return Ok(new PromoteResponse(
                StagingOutcome.PackageNotFound, null, null, false,
                "No staged package matches that identity."));
        }

        var package = group.Packages[index];
        if (package.Status != StagedPackageStatus.Staged)
        {
            return Ok(new PromoteResponse(
                StagingOutcome.AlreadyResolved,
                package.PackageId,
                package.Version,
                package.Status == StagedPackageStatus.Promoted,
                $"The staged package is already {package.Status.ToString().ToLowerInvariant()}."));
        }

        var now = await clock.GetUtcNowAsync(token);
        if (now >= group.ExpiresAt)
        {
            return Ok(new PromoteResponse(
                StagingOutcome.GroupExpired, package.PackageId, package.Version, false,
                "The staging group expired."));
        }

        // The kernel owns the whole promotion: it consumes the staged handle, runs the
        // publication pipeline, and commits this exact state transition under the token
        // read above. The extension performs no follow-up compare-and-swap.
        var promoted = group with
        {
            Packages = group.Packages.SetItem(
                index,
                package with
                {
                    Status = StagedPackageStatus.Promoted,
                    ResolvedAt = now,
                    PromotionIdempotencyKey = request.IdempotencyKey
                })
        };
        var result = await publication.PublishAsync(
            new AtomicPublicationRequest<StagingGroupState>(
                new StagedContentHandle(
                    package.ContentHandleId,
                    "application/octet-stream",
                    package.ContentLength,
                    package.ContentSha256,
                    group.ExpiresAt),
                package.SymbolHandleId is { Length: > 0 } symbolHandleId
                    ? new StagedContentHandle(
                        symbolHandleId,
                        "application/octet-stream",
                        0,
                        string.Empty,
                        group.ExpiresAt)
                    : null,
                string.IsNullOrWhiteSpace(request.IdempotencyKey)
                    ? $"{request.GroupId}/{package.PackageId}/{package.Version}"
                    : request.IdempotencyKey,
                new ExtensionStateTransition<StagingGroupState>(
                    Key(request.GroupId),
                    entry.ConcurrencyToken,
                    promoted)),
            token);
        return Ok(new PromoteResponse(
            Map(result.Outcome),
            result.PackageId ?? package.PackageId,
            result.PackageVersion ?? package.Version,
            result.Replayed,
            result.FailureDetail));
    }

    internal async ValueTask<OperationResponse<RejectResponse>> RejectAsync(
        RejectRequest request,
        CancellationToken token)
    {
        using var mutation = await EnterMutationAsync(token);
        if (!IsValidGroupId(request.GroupId))
        {
            return Ok(new RejectResponse(
                StagingOutcome.GroupNotFound, null,
                "No staging group matches that identifier."));
        }

        var entry = await state.ReadEntryAsync<StagingGroupState>(Key(request.GroupId), token);
        if (entry is null)
        {
            return Ok(new RejectResponse(
                StagingOutcome.GroupNotFound, null,
                "No staging group matches that identifier."));
        }

        var group = entry.Value;
        var index = IndexOf(group, request.PackageId, request.Version);
        if (index < 0)
        {
            return Ok(new RejectResponse(
                StagingOutcome.PackageNotFound, null,
                "No staged package matches that identity."));
        }

        var package = group.Packages[index];
        if (package.Status != StagedPackageStatus.Staged)
        {
            return Ok(new RejectResponse(
                StagingOutcome.AlreadyResolved,
                package.Status,
                $"The staged package is already {package.Status.ToString().ToLowerInvariant()}."));
        }

        var now = await clock.GetUtcNowAsync(token);
        var write = await state.WriteAsync(
            Key(request.GroupId),
            group with
            {
                Packages = group.Packages.SetItem(
                    index,
                    package with
                    {
                        Status = StagedPackageStatus.Rejected,
                        ResolvedAt = now,
                        SymbolHandleId = null,
                        SymbolUploadIdempotencyKey = null,
                        SymbolContentSha256 = null,
                        Detail = request.Reason
                    })
            },
            entry.ConcurrencyToken,
            token);
        if (write.Outcome != TransactionalStateWriteOutcome.Written)
        {
            return Ok(new RejectResponse(
                write.Outcome == TransactionalStateWriteOutcome.ConcurrencyConflict
                    ? StagingOutcome.Conflict
                    : StagingOutcome.Failed,
                null,
                write.FailureDetail));
        }

        await ReleaseAsync(package, token);
        return Ok(new RejectResponse(StagingOutcome.Succeeded, StagedPackageStatus.Rejected, null));
    }

    internal async ValueTask<OperationResponse<ExpireResponse>> ExpireAsync(
        ExpireRequest request,
        CancellationToken token)
    {
        using var mutation = await EnterMutationAsync(token);
        if (!IsValidGroupId(request.GroupId))
        {
            return Ok(new ExpireResponse(
                StagingOutcome.GroupNotFound, 0,
                "No staging group matches that identifier."));
        }

        var entry = await state.ReadEntryAsync<StagingGroupState>(Key(request.GroupId), token);
        if (entry is null)
        {
            return Ok(new ExpireResponse(
                StagingOutcome.GroupNotFound, 0,
                "No staging group matches that identifier."));
        }

        var group = entry.Value;
        var now = await clock.GetUtcNowAsync(token);
        var packages = group.Packages;
        var expired = new List<StagedPackageRecord>();
        for (var index = 0; index < packages.Length; index++)
        {
            if (packages[index].Status != StagedPackageStatus.Staged)
            {
                continue;
            }

            expired.Add(packages[index]);
            packages = packages.SetItem(
                index,
                packages[index] with
                {
                    Status = StagedPackageStatus.Expired,
                    ResolvedAt = now,
                    SymbolHandleId = null,
                    SymbolUploadIdempotencyKey = null,
                    SymbolContentSha256 = null
                });
        }

        var write = await state.WriteAsync(
            Key(request.GroupId),
            group with { Status = StagingGroupStatus.Expired, Packages = packages },
            entry.ConcurrencyToken,
            token);
        if (write.Outcome != TransactionalStateWriteOutcome.Written)
        {
            return Ok(new ExpireResponse(
                write.Outcome == TransactionalStateWriteOutcome.ConcurrencyConflict
                    ? StagingOutcome.Conflict
                    : StagingOutcome.Failed,
                0,
                write.FailureDetail));
        }

        foreach (var package in expired)
        {
            await ReleaseAsync(package, token);
        }

        return Ok(new ExpireResponse(StagingOutcome.Succeeded, expired.Count, null));
    }

    private async ValueTask ReleaseAsync(StagedPackageRecord package, CancellationToken token)
    {
        await content.ReleaseAsync(
            new StagedContentHandle(
                package.ContentHandleId,
                "application/octet-stream",
                package.ContentLength,
                package.ContentSha256,
                default),
            token);
        if (package.SymbolHandleId is { Length: > 0 } symbolHandleId)
        {
            await content.ReleaseAsync(
                new StagedContentHandle(
                    symbolHandleId,
                    "application/octet-stream",
                    0,
                    string.Empty,
                    default),
                token);
        }
    }

    private async ValueTask<IDisposable> EnterMutationAsync(CancellationToken token)
    {
        await _mutationGate.WaitAsync(token);
        return new MutationLease(_mutationGate);
    }

    private sealed class MutationLease(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }

    internal static string Key(string groupId) => GroupKeyPrefix + groupId;

    private static StagingGroupView View(StagingGroupState group, long concurrencyToken) =>
        new(
            group.GroupId,
            group.Status,
            group.CreatedAt,
            group.ExpiresAt,
            group.MaximumPackages,
            concurrencyToken,
            group.Packages);

    private static int IndexOf(StagingGroupState group, string packageId, string version)
    {
        for (var index = 0; index < group.Packages.Length; index++)
        {
            if (string.Equals(
                    group.Packages[index].PackageId,
                    packageId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    group.Packages[index].Version,
                    version,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    internal static bool IsValidGroupId(string? groupId) =>
        !string.IsNullOrWhiteSpace(groupId) &&
        groupId.Length <= 64 &&
        groupId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.');

    private static StagingOutcome Map(StagedContentWriteOutcome outcome) => outcome switch
    {
        StagedContentWriteOutcome.Succeeded => StagingOutcome.Succeeded,
        StagedContentWriteOutcome.QuotaExceeded => StagingOutcome.QuotaExceeded,
        StagedContentWriteOutcome.ContentTooLarge => StagingOutcome.ContentTooLarge,
        StagedContentWriteOutcome.InvalidContent => StagingOutcome.InvalidContent,
        StagedContentWriteOutcome.IdentityMismatch => StagingOutcome.IdentityMismatch,
        StagedContentWriteOutcome.Canceled => StagingOutcome.Canceled,
        _ => StagingOutcome.Failed
    };

    private static StagingOutcome Map(PublicationRequestOutcome outcome) => outcome switch
    {
        PublicationRequestOutcome.Published => StagingOutcome.Succeeded,
        PublicationRequestOutcome.Duplicate => StagingOutcome.DuplicatePackage,
        PublicationRequestOutcome.Quarantined => StagingOutcome.Quarantined,
        PublicationRequestOutcome.RejectedByPolicy or
            PublicationRequestOutcome.RejectedBySignature or
            PublicationRequestOutcome.RejectedByScanner => StagingOutcome.RejectedByPolicy,
        PublicationRequestOutcome.HandleNotFound or
            PublicationRequestOutcome.HandleExpired => StagingOutcome.PackageNotFound,
        PublicationRequestOutcome.StateConcurrencyConflict => StagingOutcome.Conflict,
        PublicationRequestOutcome.InvalidContent => StagingOutcome.InvalidContent,
        PublicationRequestOutcome.Unauthorized => StagingOutcome.Unauthorized,
        PublicationRequestOutcome.QuotaExceeded => StagingOutcome.QuotaExceeded,
        PublicationRequestOutcome.Canceled => StagingOutcome.Canceled,
        _ => StagingOutcome.Failed
    };

    private static OperationResponse<T> Ok<T>(T value) => OperationResponse<T>.Success(value);
}
