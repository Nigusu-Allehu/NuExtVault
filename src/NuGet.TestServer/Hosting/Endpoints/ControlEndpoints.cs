using System.Text.Json;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Control;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Faults;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.Hosting.Endpoints;

/// <summary>
/// Test-control routes. Handlers bind inputs and dispatch through the operation
/// registry.
/// </summary>
internal static class ControlEndpoints
{
    private const long LegacyJsonPackageLimit = 4L * 1024 * 1024;

    public static void Map(WebApplication app)
    {
        var limits = app.Services.GetRequiredService<PackageTransferLimits>();

        app.MapGet(
                "/__test/state",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<GetControlStateRequest, GetControlStateResponse>(
                        context,
                        OperationIds.ControlGetState,
                        new GetControlStateRequest(),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlGetState));

        app.MapPost(
                "/__test/reset",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<ResetControlStateRequest, ResetControlStateResponse>(
                        context,
                        OperationIds.ControlReset,
                        new ResetControlStateRequest(),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlReset));

        app.MapGet(
                "/__test/packages",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<GetControlPackagesRequest, GetControlPackagesResponse>(
                        context,
                        OperationIds.ControlGetPackages,
                        new GetControlPackagesRequest(),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlGetPackages));

        app.MapPost(
                "/__test/packages",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<AddControlPackageRequest, AddControlPackageResponse>(
                        context,
                        OperationIds.ControlAddPackage,
                        execution => BindControlPackageAsync(context, execution, limits, token),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlAddPackage));

        app.MapDelete(
                "/__test/packages/{id}/{version}",
                (
                    HttpContext context,
                    string id,
                    string version,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<
                        DeleteControlPackageRequest,
                        DeleteControlPackageResponse>(
                        context,
                        OperationIds.ControlDeletePackage,
                        new DeleteControlPackageRequest(new PackageIdentity(id, version)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlDeletePackage));

        app.MapPost(
                "/__test/packages/{id}/{version}/list",
                (
                    HttpContext context,
                    string id,
                    string version,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<
                        RelistControlPackageRequest,
                        RelistControlPackageResponse>(
                        context,
                        OperationIds.ControlRelistPackage,
                        new RelistControlPackageRequest(new PackageIdentity(id, version)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlRelistPackage));

        app.MapPost(
                "/__test/packages/{id}/{version}/unlist",
                (
                    HttpContext context,
                    string id,
                    string version,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<
                        UnlistControlPackageRequest,
                        UnlistControlPackageResponse>(
                        context,
                        OperationIds.ControlUnlistPackage,
                        new UnlistControlPackageRequest(new PackageIdentity(id, version)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlUnlistPackage));

        app.MapPut(
                "/__test/packages/{id}/{version}/metadata",
                (
                    HttpContext context,
                    string id,
                    string version,
                    PackageRepositoryMetadata metadata,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<
                        UpdatePackageMetadataRequest,
                        UpdatePackageMetadataResponse>(
                        context,
                        OperationIds.ControlUpdatePackageMetadata,
                        new UpdatePackageMetadataRequest(
                            new PackageIdentity(id, version),
                            CreateMetadataDocument(metadata)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlUpdatePackageMetadata));

        app.MapGet(
                "/__test/requests",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<GetRequestsRequest, GetRequestsResponse>(
                        context,
                        OperationIds.ControlGetRequests,
                        new GetRequestsRequest(),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlGetRequests));

        app.MapDelete(
                "/__test/requests",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<ClearRequestsRequest, ClearRequestsResponse>(
                        context,
                        OperationIds.ControlClearRequests,
                        new ClearRequestsRequest(),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlClearRequests));

        app.MapGet(
                "/__test/faults",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<GetFaultsRequest, GetFaultsResponse>(
                        context,
                        OperationIds.ControlGetFaults,
                        new GetFaultsRequest(),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlGetFaults));

        app.MapPost(
                "/__test/faults",
                (
                    HttpContext context,
                    FaultRule rule,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<AddFaultRequest, AddFaultResponse>(
                        context,
                        OperationIds.ControlAddFault,
                        new AddFaultRequest(ControlOperations.CreateFaultDocument(rule)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlAddFault));

        app.MapDelete(
                "/__test/faults",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<ClearFaultsRequest, ClearFaultsResponse>(
                        context,
                        OperationIds.ControlClearFaults,
                        new ClearFaultsRequest(),
                        token))
            .WithMetadata(NuGetAccessRequirement.Control)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ControlClearFaults));
    }

    private static async ValueTask<AddControlPackageRequest> BindControlPackageAsync(
        HttpContext context,
        OperationExecutionContext execution,
        PackageTransferLimits limits,
        CancellationToken token)
    {
        var request = context.Request;
        if (request.ContentType?.StartsWith(
                "application/json",
                StringComparison.OrdinalIgnoreCase) != true)
        {
            return new AddControlPackageRequest(
                execution.Content.RegisterStream(
                    request.Body,
                    request.ContentType ?? "application/octet-stream",
                    request.ContentLength ?? 0));
        }

        var legacyPackageLimit = Math.Min(limits.MaxPackageBytes, LegacyJsonPackageLimit);
        var maximumBase64Length = checked(((legacyPackageLimit + 2) / 3) * 4);
        var legacyRequestLimit = Math.Min(
            limits.MaxRequestBodyBytes,
            checked(maximumBase64Length + 1024));
        EndpointBinding.EnsureLegacyJsonUploadLimit(
            context,
            legacyRequestLimit,
            legacyPackageLimit);

        ServerApplication.PackageContentRequest? packageRequest;
        try
        {
            packageRequest = await request
                .ReadFromJsonAsync<ServerApplication.PackageContentRequest>(
                    cancellationToken: token);
        }
        catch (JsonException)
        {
            throw InvalidJson();
        }

        if (packageRequest?.Content is null)
        {
            throw InvalidJson();
        }

        if (packageRequest.Content.Length > maximumBase64Length)
        {
            throw new OperationBindingException(new OperationHttpResult(
                StatusCodes.Status413PayloadTooLarge,
                new ProblemResponseBody(
                    $"Legacy JSON control uploads are limited to {legacyPackageLimit} decoded " +
                    "bytes. Use 'application/octet-stream' for larger packages.")));
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(packageRequest.Content);
        }
        catch (FormatException)
        {
            throw new OperationBindingException(new OperationHttpResult(
                StatusCodes.Status400BadRequest,
                new ProblemResponseBody("Package content must be valid base64.")));
        }

        return new AddControlPackageRequest(
            execution.Content.RegisterBytes(content, "application/octet-stream"));
    }

    private static OperationBindingException InvalidJson() =>
        new(new OperationHttpResult(
            StatusCodes.Status400BadRequest,
            new ProblemResponseBody(
                "The package request must contain valid JSON and base64 content.")));

    private static PackageRepositoryMetadataDocument CreateMetadataDocument(
        PackageRepositoryMetadata metadata) =>
        new(
            metadata.Owners is null ? default : [.. metadata.Owners],
            metadata.Downloads,
            metadata.Verified,
            metadata.Deprecation is { } deprecation
                ? new PackageDeprecationDocument(
                    deprecation.Reasons is null ? default : [.. deprecation.Reasons],
                    deprecation.Message,
                    deprecation.AlternatePackage is { } alternate
                        ? new PackageAlternateDocument(alternate.Id, alternate.Range)
                        : null)
                : null);
}
