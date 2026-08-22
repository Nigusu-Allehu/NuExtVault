using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Extensions.FlatContainer;

/// <summary>
/// Flat-container endpoint descriptors owned by the <c>NuGet.FlatContainer</c>
/// extension. They declare the transport surface only: the kernel validates them,
/// generates the routes, and dispatches the declared operations.
/// </summary>
internal static class FlatContainerEndpoints
{
    public static ImmutableArray<EndpointDescriptor> Descriptors { get; } =
    [
        new()
        {
            Name = "flatcontainer.versions",
            Methods = ["GET", "HEAD"],
            Head = EndpointHeadPolicy.MirrorsGet,
            PathTemplate = "/flatcontainer/{id}/index.json",
            RouteParameters =
            [
                new EndpointParameter("id", Kind: RouteParameterKind.PackageId)
            ],
            AllowsResourceBaseReference = true,
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetPackageVersionsRequest, GetPackageVersionsResponse>(
                    OperationIds.FlatContainerGetVersions)
            ],
            Handler = EndpointHandler.Create<GetPackageVersionsRequest, GetPackageVersionsResponse>(
                OperationIds.FlatContainerGetVersions,
                request => new GetPackageVersionsRequest(request.GetRoute("id")))
        },
        new()
        {
            Name = "flatcontainer.content",
            Methods = ["GET", "HEAD"],
            Head = EndpointHeadPolicy.MirrorsGet,
            PathTemplate = "/flatcontainer/{id}/{version}/{fileName}",
            RouteParameters =
            [
                new EndpointParameter("id", Kind: RouteParameterKind.PackageId),
                new EndpointParameter("version", Kind: RouteParameterKind.PackageVersion),
                new EndpointParameter("fileName")
            ],
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetPackageRequest, GetPackageResponse>(
                    OperationIds.FlatContainerGetPackage),
                EndpointDescriptor.Operation<GetNuspecRequest, GetNuspecResponse>(
                    OperationIds.FlatContainerGetNuspec),
                EndpointDescriptor.Operation<GetPackageHashRequest, GetPackageHashResponse>(
                    OperationIds.FlatContainerGetHash)
            ],
            Handler = EndpointHandler.Create((request, _) =>
                ValueTask.FromResult(BindContent(request)))
        }
    ];

    private static EndpointInvocation BindContent(EndpointRequest request)
    {
        var id = request.GetRoute("id");
        var version = request.GetRoute("version");
        var fileName = request.GetRoute("fileName");
        var package = new PackageIdentity(id, version);
        var normalizedId = id.ToLowerInvariant();

        // Package version normalization is a kernel-owned identity rule, so the binder
        // reads it through the transport-neutral request surface.
        var normalizedVersion = request.GetNormalizedPackageVersion("version");
        if (fileName.Equals(
                $"{normalizedId}.{normalizedVersion}.nupkg",
                StringComparison.OrdinalIgnoreCase))
        {
            return EndpointInvocation.Operation<GetPackageRequest, GetPackageResponse>(
                OperationIds.FlatContainerGetPackage,
                new GetPackageRequest(package));
        }

        if (fileName.Equals($"{normalizedId}.nuspec", StringComparison.OrdinalIgnoreCase))
        {
            return EndpointInvocation.Operation<GetNuspecRequest, GetNuspecResponse>(
                OperationIds.FlatContainerGetNuspec,
                new GetNuspecRequest(package));
        }

        if (fileName.Equals(
                $"{normalizedId}.{normalizedVersion}.nupkg.sha512",
                StringComparison.OrdinalIgnoreCase))
        {
            return EndpointInvocation.Operation<GetPackageHashRequest, GetPackageHashResponse>(
                OperationIds.FlatContainerGetHash,
                new GetPackageHashRequest(package));
        }

        return EndpointInvocation.Result(new OperationResult(OperationResultStatus.NotFound));
    }
}
