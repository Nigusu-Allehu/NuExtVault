using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Routing;

namespace NuGet.TestServer.Hosting.Endpoints;

/// <summary>
/// Package publication endpoint descriptors. Uploads are bound as kernel content
/// handles so package and symbol payloads are never buffered by the gateway.
/// </summary>
internal static class PublicationEndpoints
{
    public static ImmutableArray<EndpointDescriptor> Descriptors { get; } =
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
                            token),
                        request.Caller.IdentityOr("anonymous"),
                        "default",
                        request.Caller.IsAdministrator)))
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
                    new PackageIdentity(request.GetRoute("id"), request.GetRoute("version")),
                    request.Caller.IdentityOr("anonymous")))
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
                    request.Caller.IdentityOr("administrator"),
                    "Production hard-delete endpoint."))
        }
    ];
}
