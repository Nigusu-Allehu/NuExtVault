using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Extensions.PackageManagement;

internal static class PackageManagementEndpoints
{
    public static ImmutableArray<EndpointDescriptor> All { get; } =
    [
        new()
        {
            Name = "publication.push",
            Methods = ["PUT"],
            PathTemplate = "/package",
            Body = EndpointBodyBinding.Stream,
            Access = new EndpointAccessPolicy(
                EndpointAccessKind.Write,
                EndpointAccessKind.Publish),
            Limits = EndpointLimits.PackageTransfer,
            Operations =
            [
                EndpointDescriptor.Operation<PushPackageRequest, PushPackageResponse>(
                    OperationIds.PackageManagementPush)
            ],
            Handler = EndpointHandler.Create(async (request, token) =>
                EndpointInvocation.Operation<PushPackageRequest, PushPackageResponse>(
                    OperationIds.PackageManagementPush,
                    new PushPackageRequest(
                        await request.BindUploadAsync(
                            "The multipart request contains no package.",
                            token))))
        },
        new()
        {
            Name = "publication.push-symbols",
            Methods = ["PUT"],
            PathTemplate = "/symbolpackage",
            Body = EndpointBodyBinding.Stream,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Write),
            Limits = EndpointLimits.PackageTransfer,
            Operations =
            [
                EndpointDescriptor.Operation<PushSymbolsRequest, PushSymbolsResponse>(
                    OperationIds.PackageManagementPushSymbols)
            ],
            Handler = EndpointHandler.Create(async (request, token) =>
                EndpointInvocation.Operation<PushSymbolsRequest, PushSymbolsResponse>(
                    OperationIds.PackageManagementPushSymbols,
                    new PushSymbolsRequest(
                        await request.BindUploadAsync(
                            "The multipart request contains no symbol package.",
                            token))))
        },
        new()
        {
            Name = "publication.unlist",
            Methods = ["DELETE"],
            PathTemplate = "/package/{id}/{version}",
            RouteParameters =
            [
                new EndpointParameter("id"),
                new EndpointParameter("version")
            ],
            Body = EndpointBodyBinding.None,
            Access = new EndpointAccessPolicy(
                EndpointAccessKind.Write,
                EndpointAccessKind.Unlist),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<UnlistPackageRequest, UnlistPackageResponse>(
                    OperationIds.PackageManagementUnlist)
            ],
            Handler = EndpointHandler.Create<UnlistPackageRequest, UnlistPackageResponse>(
                OperationIds.PackageManagementUnlist,
                request => new UnlistPackageRequest(
                    new PackageIdentity(request.GetRoute("id"), request.GetRoute("version"))))
        },
        new()
        {
            Name = "publication.delete",
            Methods = ["DELETE"],
            PathTemplate = "/package/{id}/{version}/hard",
            RouteParameters =
            [
                new EndpointParameter("id"),
                new EndpointParameter("version")
            ],
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Delete),
            Limits = EndpointLimits.BodyFree,
            RequiresProductionIdentity = true,
            Operations =
            [
                EndpointDescriptor.Operation<DeletePackageRequest, DeletePackageResponse>(
                    OperationIds.PackageManagementDelete)
            ],
            Handler = EndpointHandler.Create<DeletePackageRequest, DeletePackageResponse>(
                OperationIds.PackageManagementDelete,
                request => new DeletePackageRequest(
                    new PackageIdentity(request.GetRoute("id"), request.GetRoute("version")),
                    "Production hard-delete endpoint."))
        }
    ];
}
