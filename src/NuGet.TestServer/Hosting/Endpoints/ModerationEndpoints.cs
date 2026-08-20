using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Kernel;

namespace NuGet.TestServer.Hosting.Endpoints;

/// <summary>
/// Moderation and operational routes. Handlers bind inputs and dispatch through the
/// operation registry.
/// </summary>
internal static class ModerationEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost(
                "/__admin/packages/{id}/{version}/{action}",
                (
                    HttpContext context,
                    string id,
                    string version,
                    string action,
                    string? reason,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<ModeratePackageRequest, ModeratePackageResponse>(
                        context,
                        OperationIds.ModerationModerate,
                        _ => ValueTask.FromResult(new ModeratePackageRequest(
                            new PackageIdentity(id, version),
                            BindAction(action, reason),
                            context.User.Identity?.Name ?? "administrator",
                            reason!)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Admin)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ModerationModerate));

        app.MapGet(
                "/__admin/supply-chain/audit",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<GetModerationAuditRequest, GetModerationAuditResponse>(
                        context,
                        OperationIds.ModerationGetAudit,
                        new GetModerationAuditRequest(null, int.MaxValue),
                        token))
            .WithMetadata(NuGetAccessRequirement.Admin)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ModerationGetAudit));

        app.MapGet(
                "/__admin/packages/{id}/{version}/validations",
                (
                    HttpContext context,
                    string id,
                    string version,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<
                        GetPackageValidationsRequest,
                        GetPackageValidationsResponse>(
                        context,
                        OperationIds.ModerationGetValidations,
                        new GetPackageValidationsRequest(new PackageIdentity(id, version)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Admin)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ModerationGetValidations));
    }

    private static ModerationAction BindAction(string action, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new OperationBindingException(new OperationHttpResult(
                StatusCodes.Status400BadRequest,
                new JsonResponseBody("A moderation reason is required.")));
        }

        return action.ToLowerInvariant() switch
        {
            "approve" => ModerationAction.Approve,
            "reject" => ModerationAction.Reject,
            "quarantine" => ModerationAction.Quarantine,
            "delete" => ModerationAction.Delete,
            _ => throw new OperationBindingException(new OperationHttpResult(
                StatusCodes.Status400BadRequest,
                new JsonResponseBody(
                    "Moderation action must be approve, reject, quarantine, or delete.")))
        };
    }
}

internal static class HealthEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet(
                "/health/live",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    Liveness(context, gateway, token))
            .WithMetadata(NuGetAccessRequirement.Anonymous)
            .WithMetadata(new OperationRouteMetadata(OperationIds.HealthGetLiveness));

        app.MapGet(
                "/__test/health",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    Liveness(context, gateway, token))
            .WithMetadata(NuGetAccessRequirement.Anonymous)
            .WithMetadata(new OperationRouteMetadata(OperationIds.HealthGetLiveness));

        app.MapGet(
                "/health/ready",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<GetReadinessRequest, GetReadinessResponse>(
                        context,
                        OperationIds.HealthGetReadiness,
                        new GetReadinessRequest(),
                        token))
            .WithMetadata(NuGetAccessRequirement.Anonymous)
            .WithMetadata(new OperationRouteMetadata(OperationIds.HealthGetReadiness));

        app.MapGet(
                "/health/storage",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<GetStorageHealthRequest, GetStorageHealthResponse>(
                        context,
                        OperationIds.HealthGetStorage,
                        new GetStorageHealthRequest(),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.HealthGetStorage));
    }

    private static Task<IResult> Liveness(
        HttpContext context,
        OperationGateway gateway,
        CancellationToken token) =>
        gateway.ExecuteAsync<GetLivenessRequest, GetLivenessResponse>(
            context,
            OperationIds.HealthGetLiveness,
            new GetLivenessRequest(),
            token);
}
