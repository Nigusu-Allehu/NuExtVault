using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Extensions.Registration;

internal static class RegistrationEndpoints
{
    public static ImmutableArray<EndpointDescriptor> All { get; } =
    [
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
        }
    ];
}
