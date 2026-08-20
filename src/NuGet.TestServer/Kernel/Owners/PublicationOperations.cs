using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.Kernel.Owners;

/// <summary>
/// Package management owners. They wrap the existing publication and store logic and
/// keep package content streaming.
/// </summary>
internal sealed class PublicationOperations(
    IPackageReadCapability packages,
    IPackageMutationCapability mutations,
    IPublicationCapability publication,
    ITypedEventPublisher events,
    PackageTransferLimits limits)
{
    public void Register(OperationRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Register(
            BuiltInExtensionIds.Publication,
            new DelegateOperationOwner<PushPackageRequest, PushPackageResponse>(
                OperationIds.PackageManagementPush,
                PushAsync));
        builder.Register(
            BuiltInExtensionIds.Publication,
            new DelegateOperationOwner<PushSymbolsRequest, PushSymbolsResponse>(
                OperationIds.PackageManagementPushSymbols,
                PushSymbolsAsync));
        builder.Register(
            BuiltInExtensionIds.Publication,
            new DelegateOperationOwner<ListPackagesRequest, ListPackagesResponse>(
                OperationIds.PackageManagementList,
                ListAsync));
        builder.Register(
            BuiltInExtensionIds.Publication,
            new DelegateOperationOwner<UnlistPackageRequest, UnlistPackageResponse>(
                OperationIds.PackageManagementUnlist,
                UnlistAsync));
        builder.Register(
            BuiltInExtensionIds.Publication,
            new DelegateOperationOwner<RelistPackageRequest, RelistPackageResponse>(
                OperationIds.PackageManagementRelist,
                RelistAsync));
        builder.Register(
            BuiltInExtensionIds.Publication,
            new DelegateOperationOwner<DeletePackageRequest, DeletePackageResponse>(
                OperationIds.PackageManagementDelete,
                DeleteAsync));
    }

    private async ValueTask<OperationResponse<PushPackageResponse>> PushAsync(
        PushPackageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var content = context.Content.Resolve(request.Content);
        TestPackage? package = null;
        try
        {
            package = await TestPackage.FromStreamAsync(
                OpenContent(content),
                limits,
                cancellationToken: token);
            if (!context.Authorization.AllowsPackage(package.Identity.Id))
            {
                context.Authorization.RecordDenial(
                    $"Package '{package.Identity.Id}' is outside configured namespaces.");
                return OperationResponse<PushPackageResponse>.Failure(
                    OperationErrorPolicy.PolicyDenied(
                        $"Package '{package.Identity.Id}' is outside configured namespaces."));
            }

            var identity = new PackageIdentity(package.Identity.Id, package.NormalizedVersion);
            var result = await publication.PublishAsync(
                new PackagePublicationRequest(
                    package,
                    request.Actor,
                    request.Source,
                    request.IsAdministrator),
                token);
            package = null;
            if (result.Outcome == PackagePublicationOutcome.Published)
            {
                await events.PublishAsync(KernelEventKind.PackagePublished, token);
            }

            context.Complete(RenderPublication(result, context.RequestPath ?? "/package"));
            return OperationResponse<PushPackageResponse>.Success(
                new PushPackageResponse(identity, MapOutcome(result.Outcome)));
        }
        finally
        {
            package?.Dispose();
        }
    }

    private async ValueTask<OperationResponse<PushSymbolsResponse>> PushSymbolsAsync(
        PushSymbolsRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var content = context.Content.Resolve(request.Content);
        var symbols = await SymbolPackageReader.ReadAsync(
            OpenContent(content),
            content.Length,
            limits.MaxRequestBodyBytes,
            token);
        var package = TestPackage.FromContent(symbols);
        await mutations.AddSymbolAsync(symbols, token);
        var identity = new PackageIdentity(package.Identity.Id, package.NormalizedVersion);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status201Created,
            new JsonResponseBody(new { id = identity.Id, version = identity.Version }),
            $"/__test/packages/{Uri.EscapeDataString(identity.Id)}/{identity.Version}/symbols"));
        return OperationResponse<PushSymbolsResponse>.Success(new PushSymbolsResponse(identity));
    }

    private async ValueTask<OperationResponse<ListPackagesResponse>> ListAsync(
        ListPackagesRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var listedPackages = request.PackageId is null
            ? await packages.GetAllAsync(token)
            : await packages.FindByIdAsync(request.PackageId, token);
        var response = new ListPackagesResponse(
            [
                .. listedPackages
                    .Skip(Math.Max(0, request.Skip))
                    .Take(Math.Max(0, request.Take))
                    .Select(package => new PackageSummaryDocument(
                    new PackageIdentity(package.Id, package.NormalizedVersion),
                        package.IsListed,
                        package.Published))
            ]);
        context.Complete(new OperationHttpResult(
            StatusCodes.Status200OK,
            new JsonResponseBody(response.Packages.Select(package => new
            {
                id = package.Package.Id,
                version = package.Package.Version,
                listed = package.Listed,
                published = package.Published
            }).ToArray())));
        return OperationResponse<ListPackagesResponse>.Success(response);
    }

    private async ValueTask<OperationResponse<UnlistPackageResponse>> UnlistAsync(
        UnlistPackageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var denial = await AuthorizeManagementAsync(request.Package.Id, context, token);
        if (denial is not null)
        {
            return OperationResponse<UnlistPackageResponse>.Failure(denial);
        }

        if (!await mutations.SetListedAsync(
                request.Package.Id,
                request.Package.Version,
                false,
                token))
        {
            return OperationResponse<UnlistPackageResponse>.Failure(NotFound(request.Package));
        }

        context.Complete(new OperationHttpResult(StatusCodes.Status204NoContent));
        return OperationResponse<UnlistPackageResponse>.Success(
            new UnlistPackageResponse(request.Package));
    }

    private async ValueTask<OperationResponse<RelistPackageResponse>> RelistAsync(
        RelistPackageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var denial = await AuthorizeManagementAsync(request.Package.Id, context, token);
        if (denial is not null)
        {
            return OperationResponse<RelistPackageResponse>.Failure(denial);
        }

        if (!await mutations.SetListedAsync(
                request.Package.Id,
                request.Package.Version,
                true,
                token))
        {
            return OperationResponse<RelistPackageResponse>.Failure(NotFound(request.Package));
        }

        context.Complete(new OperationHttpResult(StatusCodes.Status204NoContent));
        return OperationResponse<RelistPackageResponse>.Success(
            new RelistPackageResponse(request.Package));
    }

    private async ValueTask<OperationResponse<DeletePackageResponse>> DeleteAsync(
        DeletePackageRequest request,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var denial = await AuthorizeManagementAsync(request.Package.Id, context, token);
        if (denial is not null)
        {
            return OperationResponse<DeletePackageResponse>.Failure(denial);
        }

        if (!await publication.DeleteControlledAsync(
                request.Package.Id,
                request.Package.Version,
                request.Actor,
                request.Reason,
                token))
        {
            return OperationResponse<DeletePackageResponse>.Failure(NotFound(request.Package));
        }

        context.Complete(new OperationHttpResult(StatusCodes.Status204NoContent));
        return OperationResponse<DeletePackageResponse>.Success(
            new DeletePackageResponse(request.Package));
    }

    private async ValueTask<OperationError?> AuthorizeManagementAsync(
        string packageId,
        OperationExecutionContext context,
        CancellationToken token)
    {
        var authorization = context.Authorization;
        if (!authorization.HasIdentity)
        {
            return null;
        }

        var owner = await publication.GetOwnerAsync(packageId, token);
        if ((owner is not null &&
             string.Equals(owner, authorization.IdentityName, StringComparison.Ordinal)) ||
            authorization.IsAdministrator)
        {
            return null;
        }

        var detail = owner is null
            ? $"Package '{packageId}' has no recorded owner."
            : $"Package '{packageId}' is owned by another identity.";
        authorization.RecordDenial(detail);
        return OperationErrorPolicy.PolicyDenied(detail);
    }

    private static Stream OpenContent(OperationContent content) =>
        content.Stream ??
        (content.Bytes is { } bytes
            ? new MemoryStream(bytes.ToArray(), writable: false)
            : throw new InvalidOperationException("Package content has no readable payload."));

    private static OperationHttpResult RenderPublication(
        PackagePublicationResult publication,
        string requestPath) =>
        publication.Outcome switch
        {
            PackagePublicationOutcome.Published => new OperationHttpResult(
                StatusCodes.Status201Created,
                new JsonResponseBody(publication),
                requestPath),
            PackagePublicationOutcome.Duplicate => new OperationHttpResult(
                StatusCodes.Status200OK,
                new JsonResponseBody(publication)),
            PackagePublicationOutcome.Quarantined => new OperationHttpResult(
                StatusCodes.Status202Accepted,
                new JsonResponseBody(publication),
                requestPath),
            PackagePublicationOutcome.Rejected => new OperationHttpResult(
                StatusCodes.Status422UnprocessableEntity,
                new JsonResponseBody(publication)),
            PackagePublicationOutcome.Unauthorized => new OperationHttpResult(
                StatusCodes.Status403Forbidden),
            PackagePublicationOutcome.QuotaExceeded => new OperationHttpResult(
                StatusCodes.Status429TooManyRequests),
            _ => new OperationHttpResult(
                StatusCodes.Status409Conflict,
                new JsonResponseBody(publication))
        };

    private static PublicationOutcome MapOutcome(PackagePublicationOutcome outcome) =>
        outcome switch
        {
            PackagePublicationOutcome.Published => PublicationOutcome.Published,
            PackagePublicationOutcome.Duplicate => PublicationOutcome.Duplicate,
            PackagePublicationOutcome.Quarantined => PublicationOutcome.Quarantined,
            PackagePublicationOutcome.Rejected => PublicationOutcome.Rejected,
            PackagePublicationOutcome.Unauthorized => PublicationOutcome.Unauthorized,
            PackagePublicationOutcome.QuotaExceeded => PublicationOutcome.QuotaExceeded,
            _ => PublicationOutcome.Conflict
        };

    private static OperationError NotFound(PackageIdentity package) =>
        OperationErrorPolicy.NotFound(
            $"Package '{package.Id}' version '{package.Version}' does not exist.");
}

/// <summary>
/// Reads symbol content once into an exactly sized buffer. The previous
/// implementation copied the whole symbol package twice. The declared length is
/// never trusted beyond the configured request-body limit, so a client cannot make
/// the server allocate more than it would accept.
/// </summary>
internal static class SymbolPackageReader
{
    private const int MaximumEagerBufferBytes = 64 * 1024 * 1024;

    public static async ValueTask<byte[]> ReadAsync(
        Stream content,
        long expectedLength,
        long maximumLength,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(content);
        var eagerLength = Math.Min(
            expectedLength,
            Math.Min(maximumLength <= 0 ? MaximumEagerBufferBytes : maximumLength, MaximumEagerBufferBytes));
        if (eagerLength <= 0)
        {
            using var growing = new MemoryStream();
            await content.CopyToAsync(growing, token);
            return growing.ToArray();
        }

        var exact = new byte[eagerLength];
        var read = await content.ReadAtLeastAsync(
            exact,
            exact.Length,
            throwOnEndOfStream: false,
            token);
        if (read < exact.Length)
        {
            return exact[..read];
        }

        // The declared length is authoritative for HTTP uploads, but never trust it
        // silently: append anything that follows instead of truncating content.
        var probe = new byte[1];
        var extra = await content.ReadAsync(probe, token);
        if (extra == 0)
        {
            return exact;
        }

        using var buffer = new MemoryStream(exact.Length * 2);
        await buffer.WriteAsync(exact, token);
        await buffer.WriteAsync(probe.AsMemory(0, extra), token);
        await content.CopyToAsync(buffer, token);
        return buffer.ToArray();
    }
}
