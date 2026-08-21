using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Routing;

namespace NuGet.TestServer.Hosting.Endpoints;

/// <summary>
/// Moderation endpoint descriptors. Binding failures return protocol-compatible client
/// errors before any moderation operation is dispatched.
/// </summary>
internal static class ModerationEndpoints
{
    public static ImmutableArray<EndpointDescriptor> Descriptors { get; } =
    [
        new()
        {
            Name = "moderation.moderate",
            Methods = ["POST"],
            PathTemplate = "/__admin/packages/{id}/{version}/{action}",
            RouteParameters =
            [
                new EndpointParameter("id"),
                new EndpointParameter("version"),
                new EndpointParameter("action")
            ],
            QueryParameters = [new EndpointParameter("reason")],
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Admin),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<ModeratePackageRequest, ModeratePackageResponse>(
                    OperationIds.ModerationModerate)
            ],
            Handler = EndpointHandler.Create<ModeratePackageRequest, ModeratePackageResponse>(
                OperationIds.ModerationModerate,
                request =>
                {
                    var reason = request.GetQuery("reason");
                    return new ModeratePackageRequest(
                        new PackageIdentity(request.GetRoute("id"), request.GetRoute("version")),
                        BindAction(request.GetRoute("action"), reason),
                        request.Caller.IdentityOr("administrator"),
                        reason!);
                })
        },
        new()
        {
            Name = "moderation.audit",
            Methods = ["GET"],
            PathTemplate = "/__admin/supply-chain/audit",
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Admin),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetModerationAuditRequest, GetModerationAuditResponse>(
                    OperationIds.ModerationGetAudit)
            ],
            Handler = EndpointHandler.Create<GetModerationAuditRequest, GetModerationAuditResponse>(
                OperationIds.ModerationGetAudit,
                _ => new GetModerationAuditRequest(null, int.MaxValue))
        },
        new()
        {
            Name = "moderation.validations",
            Methods = ["GET"],
            PathTemplate = "/__admin/packages/{id}/{version}/validations",
            RouteParameters =
            [
                new EndpointParameter("id"),
                new EndpointParameter("version")
            ],
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Admin),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor
                    .Operation<GetPackageValidationsRequest, GetPackageValidationsResponse>(
                        OperationIds.ModerationGetValidations)
            ],
            Handler = EndpointHandler
                .Create<GetPackageValidationsRequest, GetPackageValidationsResponse>(
                    OperationIds.ModerationGetValidations,
                    request => new GetPackageValidationsRequest(
                        new PackageIdentity(request.GetRoute("id"), request.GetRoute("version"))))
        }
    ];

    private static ModerationAction BindAction(string action, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new OperationBindingException(new OperationResult(
                OperationResultStatus.InvalidRequest,
                new OperationDocumentBody("A moderation reason is required.")));
        }

        return action.ToLowerInvariant() switch
        {
            "approve" => ModerationAction.Approve,
            "reject" => ModerationAction.Reject,
            "quarantine" => ModerationAction.Quarantine,
            "delete" => ModerationAction.Delete,
            _ => throw new OperationBindingException(new OperationResult(
                OperationResultStatus.InvalidRequest,
                new OperationDocumentBody(
                    "Moderation action must be approve, reject, quarantine, or delete.")))
        };
    }
}

/// <summary>
/// Health and readiness endpoint descriptors owned by the operations extension.
/// </summary>
internal static class HealthEndpoints
{
    private static IEndpointHandler Liveness { get; } =
        EndpointHandler.Create<GetLivenessRequest, GetLivenessResponse>(
            OperationIds.HealthGetLiveness,
            _ => new GetLivenessRequest());

    public static ImmutableArray<EndpointDescriptor> Descriptors { get; } =
    [
        new()
        {
            Name = "health.live",
            Methods = ["GET"],
            PathTemplate = "/health/live",
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Anonymous),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetLivenessRequest, GetLivenessResponse>(
                    OperationIds.HealthGetLiveness)
            ],
            Handler = Liveness
        },
        new()
        {
            Name = "health.live-legacy",
            Methods = ["GET"],
            PathTemplate = "/__test/health",
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Anonymous),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetLivenessRequest, GetLivenessResponse>(
                    OperationIds.HealthGetLiveness)
            ],
            Handler = Liveness
        },
        new()
        {
            Name = "health.ready",
            Methods = ["GET"],
            PathTemplate = "/health/ready",
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Anonymous),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetReadinessRequest, GetReadinessResponse>(
                    OperationIds.HealthGetReadiness)
            ],
            Handler = EndpointHandler.Create<GetReadinessRequest, GetReadinessResponse>(
                OperationIds.HealthGetReadiness,
                _ => new GetReadinessRequest())
        },
        new()
        {
            Name = "health.storage",
            Methods = ["GET"],
            PathTemplate = "/health/storage",
            Body = EndpointBodyBinding.None,
            Access = EndpointAccessPolicy.Of(EndpointAccessKind.Control),
            Limits = EndpointLimits.BodyFree,
            Operations =
            [
                EndpointDescriptor.Operation<GetStorageHealthRequest, GetStorageHealthResponse>(
                    OperationIds.HealthGetStorage)
            ],
            Handler = EndpointHandler.Create<GetStorageHealthRequest, GetStorageHealthResponse>(
                OperationIds.HealthGetStorage,
                _ => new GetStorageHealthRequest())
        }
    ];
}
