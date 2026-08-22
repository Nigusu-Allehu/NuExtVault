using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.Kernel.Capabilities;

internal sealed class PackageManagementCapability(
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
        IPackagePushCapability,
        IPackageSymbolsPushCapability,
        IPackageManagementListCapability,
        IPackageUnlistCapability,
        IPackageRelistCapability,
        IPackageDeleteCapability
{
    public ValueTask<PackagePublicationDocument> PublishAsync(
        StreamHandle contentHandle,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesPublish).InvokeAsync(
            "push",
            async ct =>
            {
                var execution = OperationExecutionScope.Required;
                var authorization = execution.Authorization;
                var content = execution.Content.Resolve(contentHandle);
                TestPackage? package = null;
                try
                {
                    package = await TestPackage.FromStreamAsync(
                        OpenContent(content),
                        packageLimits,
                        cancellationToken: ct);
                    var identity = new PackageIdentity(
                        package.Identity.Id,
                        package.NormalizedVersion);
                    if (!execution.Authorization.AllowsPackage(identity.Id))
                    {
                        var detail =
                            $"Package '{identity.Id}' is outside configured namespaces.";
                        execution.Authorization.RecordDenial(detail);
                        return new PackagePublicationDocument(
                            identity,
                            PublicationOutcome.Unauthorized,
                            detail);
                    }

                    var result = await supplyChain.PublishAsync(
                        new PackagePublicationRequest(
                            package,
                            authorization.IdentityName ?? "anonymous",
                            "default",
                            authorization.IsAdministrator),
                        ct);
                    package = null;
                    if (result.Outcome == PackagePublicationOutcome.Published)
                    {
                        diagnostics.RecordPackagePublished();
                    }

                    return new PackagePublicationDocument(
                        identity,
                        MapOutcome(result.Outcome),
                        result.Message);
                }
                finally
                {
                    package?.Dispose();
                }
            },
            token);

    public ValueTask<PackageIdentity> StoreAsync(
        StreamHandle contentHandle,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesContentWrite).InvokeAsync(
            "push-symbols",
            async ct =>
            {
                var content = OperationExecutionScope.Required.Content.Resolve(contentHandle);
                var symbols = await SymbolPackageReader.ReadAsync(
                    OpenContent(content),
                    content.Length,
                    packageLimits.MaxRequestBodyBytes,
                    ct);
                using var package = TestPackage.FromContent(symbols);
                await store.AddSymbolAsync(symbols, ct);
                return new PackageIdentity(package.Identity.Id, package.NormalizedVersion);
            },
            token);

    public ValueTask<ImmutableArray<PackageSummaryDocument>> QueryAsync(
        string? packageId,
        int skip,
        int take,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesMetadataRead).InvokeAsync(
            "list",
            async ct =>
            {
                var found = packageId is null
                    ? await store.GetAllAsync(ct)
                    : await store.FindByIdAsync(packageId, ct);
                return found
                    .Skip(Math.Max(0, skip))
                    .Take(Math.Max(0, take))
                    .Select(package => new PackageSummaryDocument(
                        new PackageIdentity(
                            package.Identity.Id,
                            package.NormalizedVersion),
                        package.IsListed,
                        package.Published))
                    .ToImmutableArray();
            },
            token);

    public ValueTask<PackageMutationDocument> SetUnlistedAsync(
        PackageIdentity package,
        CancellationToken token) =>
        SetListedAsync(
            package,
            listed: false,
            BuiltInCapabilityNames.PackagesUnlist,
            "unlist",
            token);

    public ValueTask<PackageMutationDocument> SetListedAsync(
        PackageIdentity package,
        CancellationToken token) =>
        SetListedAsync(
            package,
            listed: true,
            BuiltInCapabilityNames.PackagesRelist,
            "relist",
            token);

    public ValueTask<PackageMutationDocument> DeleteAsync(
        PackageIdentity package,
        string reason,
        CancellationToken token) =>
        Gate(BuiltInCapabilityNames.PackagesDelete).InvokeAsync(
            "delete",
            async ct =>
            {
                var denial = await AuthorizeMutationAsync(package.Id, ct);
                if (denial is not null)
                {
                    return denial;
                }

                return await supplyChain.DeleteControlledAsync(
                    package.Id,
                    package.Version,
                    OperationExecutionScope.Required.Authorization.IdentityName ??
                    "administrator",
                    reason,
                    ct)
                    ? new PackageMutationDocument(PackageMutationOutcome.Succeeded)
                    : new PackageMutationDocument(PackageMutationOutcome.NotFound);
            },
            token);

    private ValueTask<PackageMutationDocument> SetListedAsync(
        PackageIdentity package,
        bool listed,
        string capability,
        string action,
        CancellationToken token) =>
        Gate(capability).InvokeAsync(
            action,
            async ct =>
            {
                var denial = await AuthorizeMutationAsync(package.Id, ct);
                if (denial is not null)
                {
                    return denial;
                }

                return await store.SetListedAsync(package.Id, package.Version, listed, ct)
                    ? new PackageMutationDocument(PackageMutationOutcome.Succeeded)
                    : new PackageMutationDocument(PackageMutationOutcome.NotFound);
            },
            token);

    private async ValueTask<PackageMutationDocument?> AuthorizeMutationAsync(
        string packageId,
        CancellationToken token)
    {
        var authorization = OperationExecutionScope.Required.Authorization;
        if (!authorization.HasIdentity)
        {
            return null;
        }

        var owner = await supplyChain.GetOwnerAsync(packageId, token);
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
        return new PackageMutationDocument(PackageMutationOutcome.Forbidden, detail);
    }

    private static Stream OpenContent(OperationContent content) =>
        content.Stream ??
        (content.Bytes is { } bytes
            ? new MemoryStream(bytes.ToArray(), writable: false)
            : throw new InvalidOperationException("Package content has no readable payload."));

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
}

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
            Math.Min(
                maximumLength <= 0 ? MaximumEagerBufferBytes : maximumLength,
                MaximumEagerBufferBytes));
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
