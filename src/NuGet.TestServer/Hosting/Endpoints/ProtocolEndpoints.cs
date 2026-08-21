using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Routing;
using NuGet.TestServer.Packages;
using NuGet.Versioning;

namespace NuGet.TestServer.Hosting.Endpoints;

/// <summary>
/// NuGet protocol endpoint descriptors. They declare the transport surface only; the
/// kernel generates and freezes the routes and dispatches the declared operations.
/// </summary>
internal static class ProtocolEndpoints
{
    public static ImmutableArray<EndpointDescriptor> ServiceIndex { get; } =
    [
        new()
        {
            Name = "service-index.get",
            Methods = ["GET", "HEAD"],
            Head = EndpointHeadPolicy.MirrorsGet,
            PathTemplate = "/v3/index.json",
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetServiceIndexRequest, GetServiceIndexResponse>(
                    OperationIds.ServiceIndexGet)
            ],
            Handler = EndpointHandler.Create<GetServiceIndexRequest, GetServiceIndexResponse>(
                OperationIds.ServiceIndexGet,
                _ => new GetServiceIndexRequest())
        }
    ];

    public static ImmutableArray<EndpointDescriptor> Protocol { get; } =
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
                ValueTask.FromResult(BindFlatContainerContent(request)))
        },
        new()
        {
            Name = "registration.index",
            Methods = ["GET", "HEAD"],
            Head = EndpointHeadPolicy.MirrorsGet,
            PathTemplate = "/registration/{id}/index.json",
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
                EndpointDescriptor
                    .Operation<GetRegistrationIndexRequest, GetRegistrationIndexResponse>(
                        OperationIds.RegistrationGetIndex)
            ],
            Handler = EndpointHandler
                .Create<GetRegistrationIndexRequest, GetRegistrationIndexResponse>(
                    OperationIds.RegistrationGetIndex,
                    request => new GetRegistrationIndexRequest(request.GetRoute("id")))
        },
        new()
        {
            Name = "registration.page",
            Methods = ["GET", "HEAD"],
            Head = EndpointHeadPolicy.MirrorsGet,
            PathTemplate = "/registration/{id}/page/{lower}/{upper}.json",
            RouteParameters =
            [
                new EndpointParameter("id", Kind: RouteParameterKind.PackageId),
                new EndpointParameter("lower", Kind: RouteParameterKind.PackageVersion),
                new EndpointParameter("upper", Kind: RouteParameterKind.PackageVersion)
            ],
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor
                    .Operation<GetRegistrationPageRequest, GetRegistrationPageResponse>(
                        OperationIds.RegistrationGetPage)
            ],
            Handler = EndpointHandler
                .Create<GetRegistrationPageRequest, GetRegistrationPageResponse>(
                    OperationIds.RegistrationGetPage,
                    request => new GetRegistrationPageRequest(
                        request.GetRoute("id"),
                        request.GetRoute("lower"),
                        request.GetRoute("upper")))
        },
        new()
        {
            Name = "registration.leaf",
            Methods = ["GET", "HEAD"],
            Head = EndpointHeadPolicy.MirrorsGet,
            PathTemplate = "/registration/{id}/{version}.json",
            RouteParameters =
            [
                new EndpointParameter("id", Kind: RouteParameterKind.PackageId),
                new EndpointParameter("version", Kind: RouteParameterKind.PackageVersion)
            ],
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor
                    .Operation<GetRegistrationLeafRequest, GetRegistrationLeafResponse>(
                        OperationIds.RegistrationGetLeaf)
            ],
            Handler = EndpointHandler
                .Create<GetRegistrationLeafRequest, GetRegistrationLeafResponse>(
                    OperationIds.RegistrationGetLeaf,
                    request => new GetRegistrationLeafRequest(
                        new PackageIdentity(request.GetRoute("id"), request.GetRoute("version"))))
        },
        new()
        {
            Name = "search.query",
            Methods = ["GET", "HEAD"],
            Head = EndpointHeadPolicy.MirrorsGet,
            PathTemplate = "/query",
            QueryParameters =
            [
                new EndpointParameter("q", IsRequired: false),
                new EndpointParameter("skip", IsRequired: false),
                new EndpointParameter("take", IsRequired: false),
                new EndpointParameter("prerelease", IsRequired: false),
                new EndpointParameter("packageType", IsRequired: false)
            ],
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Read),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<SearchRequest, SearchResponse>(
                    OperationIds.SearchQuery)
            ],
            Handler = EndpointHandler.Create<SearchRequest, SearchResponse>(
                OperationIds.SearchQuery,
                request => new SearchRequest(
                    request.GetQuery("q") ?? string.Empty,
                    request.GetQueryInt32("skip") ?? 0,
                    request.GetQueryInt32("take") ?? 20,
                    request.GetQueryBoolean("prerelease") ?? false,
                    request.GetQuery("packageType")))
        }
    ];

    private static EndpointInvocation BindFlatContainerContent(EndpointRequest request)
    {
        var id = request.GetRoute("id");
        var version = request.GetRoute("version");
        var fileName = request.GetRoute("fileName");
        var package = new PackageIdentity(id, version);
        var normalizedId = id.ToLowerInvariant();
        var normalizedVersion = NormalizeVersion(version);
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

        return EndpointInvocation.Result(new OperationHttpResult(StatusCodes.Status404NotFound));
    }

    internal static string NormalizeVersion(string version) =>
        NuGetVersion.TryParse(version, out var parsed)
            ? TestPackage.NormalizeVersion(parsed)
            : version.ToLowerInvariant();
}
