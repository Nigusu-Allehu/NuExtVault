using Microsoft.AspNetCore.Http.Features;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Packages;
using NuGet.Versioning;

namespace NuGet.TestServer.Hosting.Endpoints;

/// <summary>
/// NuGet protocol routes. Handlers bind HTTP inputs and dispatch through the
/// operation registry; no protocol feature logic remains here.
/// </summary>
internal static class ProtocolEndpoints
{
    public static void Map(WebApplication app)
    {
        MapServiceIndex(app);
        MapVulnerabilities(app);
        MapFlatContainer(app);
        MapRegistration(app);
        MapSearch(app);
        MapPublication(app);
    }

    private static void MapServiceIndex(WebApplication app) =>
        app.MapMethods(
                "/v3/index.json",
                [HttpMethods.Get, HttpMethods.Head],
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<GetServiceIndexRequest, GetServiceIndexResponse>(
                        context,
                        OperationIds.ServiceIndexGet,
                        new GetServiceIndexRequest(EndpointBinding.GetRoot(context)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Read)
            .WithMetadata(new OperationRouteMetadata(OperationIds.ServiceIndexGet));

    private static void MapVulnerabilities(WebApplication app)
    {
        app.MapMethods(
                "/v3/vulnerabilities/index.json",
                [HttpMethods.Get, HttpMethods.Head],
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<
                        GetVulnerabilityIndexRequest,
                        GetVulnerabilityIndexResponse>(
                        context,
                        OperationIds.VulnerabilitiesGetIndex,
                        new GetVulnerabilityIndexRequest(EndpointBinding.GetRoot(context)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Read)
            .WithMetadata(new OperationRouteMetadata(OperationIds.VulnerabilitiesGetIndex));

        app.MapMethods(
                "/v3/vulnerabilities/{snapshotId}/{pageName}.json",
                [HttpMethods.Get, HttpMethods.Head],
                (
                    HttpContext context,
                    string snapshotId,
                    string pageName,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<
                        GetVulnerabilityPageRequest,
                        GetVulnerabilityPageResponse>(
                        context,
                        OperationIds.VulnerabilitiesGetPage,
                        new GetVulnerabilityPageRequest(snapshotId, pageName),
                        token))
            .WithMetadata(NuGetAccessRequirement.Read)
            .WithMetadata(new OperationRouteMetadata(OperationIds.VulnerabilitiesGetPage));
    }

    private static void MapFlatContainer(WebApplication app)
    {
        app.MapMethods(
                "/flatcontainer/{id}/index.json",
                [HttpMethods.Get, HttpMethods.Head],
                (
                    HttpContext context,
                    string id,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<GetPackageVersionsRequest, GetPackageVersionsResponse>(
                        context,
                        OperationIds.FlatContainerGetVersions,
                        new GetPackageVersionsRequest(id),
                        token))
            .WithMetadata(NuGetAccessRequirement.Read)
            .WithMetadata(new OperationRouteMetadata(OperationIds.FlatContainerGetVersions));

        app.MapMethods(
                "/flatcontainer/{id}/{version}/{fileName}",
                [HttpMethods.Get, HttpMethods.Head],
                async Task<IResult> (
                    HttpContext context,
                    string id,
                    string version,
                    string fileName,
                    OperationGateway gateway,
                    CancellationToken token) =>
                {
                    var package = new PackageIdentity(id, version);
                    var normalizedId = id.ToLowerInvariant();
                    var normalizedVersion = EndpointBinding.NormalizeVersion(version);
                    if (fileName.Equals(
                            $"{normalizedId}.{normalizedVersion}.nupkg",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return await gateway.ExecuteAsync<GetPackageRequest, GetPackageResponse>(
                            context,
                            OperationIds.FlatContainerGetPackage,
                            new GetPackageRequest(package),
                            token);
                    }

                    if (fileName.Equals(
                            $"{normalizedId}.nuspec",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return await gateway.ExecuteAsync<GetNuspecRequest, GetNuspecResponse>(
                            context,
                            OperationIds.FlatContainerGetNuspec,
                            new GetNuspecRequest(package),
                            token);
                    }

                    if (fileName.Equals(
                            $"{normalizedId}.{normalizedVersion}.nupkg.sha512",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return await gateway
                            .ExecuteAsync<GetPackageHashRequest, GetPackageHashResponse>(
                                context,
                                OperationIds.FlatContainerGetHash,
                                new GetPackageHashRequest(package),
                                token);
                    }

                    return Results.NotFound();
                })
            .WithMetadata(NuGetAccessRequirement.Read)
            .WithMetadata(new OperationRouteMetadata(
                OperationIds.FlatContainerGetPackage,
                OperationIds.FlatContainerGetNuspec,
                OperationIds.FlatContainerGetHash));
    }

    private static void MapRegistration(WebApplication app)
    {
        app.MapMethods(
                "/registration/{id}/index.json",
                [HttpMethods.Get, HttpMethods.Head],
                (
                    HttpContext context,
                    string id,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<
                        GetRegistrationIndexRequest,
                        GetRegistrationIndexResponse>(
                        context,
                        OperationIds.RegistrationGetIndex,
                        new GetRegistrationIndexRequest(id, EndpointBinding.GetRoot(context)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Read)
            .WithMetadata(new OperationRouteMetadata(OperationIds.RegistrationGetIndex));

        app.MapMethods(
                "/registration/{id}/page/{lower}/{upper}.json",
                [HttpMethods.Get, HttpMethods.Head],
                (
                    HttpContext context,
                    string id,
                    string lower,
                    string upper,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<GetRegistrationPageRequest, GetRegistrationPageResponse>(
                        context,
                        OperationIds.RegistrationGetPage,
                        new GetRegistrationPageRequest(
                            id,
                            lower,
                            upper,
                            EndpointBinding.GetRoot(context)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Read)
            .WithMetadata(new OperationRouteMetadata(OperationIds.RegistrationGetPage));

        app.MapMethods(
                "/registration/{id}/{version}.json",
                [HttpMethods.Get, HttpMethods.Head],
                (
                    HttpContext context,
                    string id,
                    string version,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<GetRegistrationLeafRequest, GetRegistrationLeafResponse>(
                        context,
                        OperationIds.RegistrationGetLeaf,
                        new GetRegistrationLeafRequest(
                            new PackageIdentity(id, version),
                            EndpointBinding.GetRoot(context)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Read)
            .WithMetadata(new OperationRouteMetadata(OperationIds.RegistrationGetLeaf));
    }

    private static void MapSearch(WebApplication app) =>
        app.MapMethods(
                "/query",
                [HttpMethods.Get, HttpMethods.Head],
                (
                    HttpContext context,
                    string? q,
                    int? skip,
                    int? take,
                    bool? prerelease,
                    string? packageType,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<SearchRequest, SearchResponse>(
                        context,
                        OperationIds.SearchQuery,
                        new SearchRequest(
                            q ?? string.Empty,
                            skip ?? 0,
                            take ?? 20,
                            prerelease ?? false,
                            packageType,
                            EndpointBinding.GetRoot(context)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Read)
            .WithMetadata(new OperationRouteMetadata(OperationIds.SearchQuery));

    private static void MapPublication(WebApplication app)
    {
        var production = app.Services.GetRequiredService<AuthenticationConfiguration>().Profile ==
            AuthenticationProfile.Production;

        app.MapPut(
                "/package",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<PushPackageRequest, PushPackageResponse>(
                        context,
                        OperationIds.PackageManagementPush,
                        async execution => new PushPackageRequest(
                            await EndpointBinding.BindUploadAsync(
                                context,
                                execution,
                                "The multipart request contains no package.",
                                token),
                            execution.Authorization.IdentityName ?? "anonymous",
                            "default",
                            execution.Authorization.IsAdministrator),
                        token))
            .WithMetadata(
                production ? NuGetAccessRequirement.Publish : NuGetAccessRequirement.Write)
            .WithMetadata(new OperationRouteMetadata(OperationIds.PackageManagementPush));

        app.MapPut(
                "/symbolpackage",
                (HttpContext context, OperationGateway gateway, CancellationToken token) =>
                    gateway.ExecuteAsync<PushSymbolsRequest, PushSymbolsResponse>(
                        context,
                        OperationIds.PackageManagementPushSymbols,
                        async execution => new PushSymbolsRequest(
                            await EndpointBinding.BindUploadAsync(
                                context,
                                execution,
                                "The multipart request contains no symbol package.",
                                token)),
                        token))
            .WithMetadata(NuGetAccessRequirement.Write)
            .WithMetadata(new OperationRouteMetadata(OperationIds.PackageManagementPushSymbols));

        app.MapDelete(
                "/package/{id}/{version}",
                (
                    HttpContext context,
                    string id,
                    string version,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<UnlistPackageRequest, UnlistPackageResponse>(
                        context,
                        OperationIds.PackageManagementUnlist,
                        execution => ValueTask.FromResult(new UnlistPackageRequest(
                            new PackageIdentity(id, version),
                            execution.Authorization.IdentityName ?? "anonymous")),
                        token))
            .WithMetadata(
                production ? NuGetAccessRequirement.Unlist : NuGetAccessRequirement.Write)
            .WithMetadata(new OperationRouteMetadata(OperationIds.PackageManagementUnlist));

        if (!production)
        {
            return;
        }

        app.MapDelete(
                "/package/{id}/{version}/hard",
                (
                    HttpContext context,
                    string id,
                    string version,
                    OperationGateway gateway,
                    CancellationToken token) =>
                    gateway.ExecuteAsync<DeletePackageRequest, DeletePackageResponse>(
                        context,
                        OperationIds.PackageManagementDelete,
                        new DeletePackageRequest(
                            new PackageIdentity(id, version),
                            context.User.Identity?.Name ?? "administrator",
                            "Production hard-delete endpoint."),
                        token))
            .WithMetadata(NuGetAccessRequirement.Delete)
            .WithMetadata(new OperationRouteMetadata(OperationIds.PackageManagementDelete));
    }
}

internal static class EndpointBinding
{
    public static string GetRoot(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}";

    public static string NormalizeVersion(string version) =>
        NuGetVersion.TryParse(version, out var parsed)
            ? TestPackage.NormalizeVersion(parsed)
            : version.ToLowerInvariant();

    /// <summary>
    /// Binds an uploaded package or symbol package to a kernel content handle. The
    /// payload is never buffered by the gateway.
    /// </summary>
    public static async ValueTask<StreamHandle> BindUploadAsync(
        HttpContext context,
        OperationExecutionContext execution,
        string missingFileDetail,
        CancellationToken token)
    {
        var request = context.Request;
        if (!request.HasFormContentType)
        {
            return execution.Content.RegisterStream(
                request.Body,
                request.ContentType ?? "application/octet-stream",
                request.ContentLength ?? 0);
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(token);
        }
        catch (InvalidDataException exception)
        {
            throw new OperationBindingException(new OperationHttpResult(
                StatusCodes.Status413PayloadTooLarge,
                new ProblemResponseBody(exception.Message)));
        }

        var file = form.Files.FirstOrDefault();
        if (file is null)
        {
            throw new OperationBindingException(new OperationHttpResult(
                StatusCodes.Status500InternalServerError,
                new ProblemResponseBody(missingFileDetail)));
        }

        return execution.Content.RegisterStream(
            file.OpenReadStream(),
            file.ContentType ?? "application/octet-stream",
            file.Length);
    }

    /// <summary>
    /// Applies the legacy JSON control-upload limits before binding base64 content.
    /// </summary>
    public static void EnsureLegacyJsonUploadLimit(
        HttpContext context,
        long legacyRequestLimit,
        long legacyPackageLimit)
    {
        if (context.Request.ContentLength > legacyRequestLimit)
        {
            throw new OperationBindingException(new OperationHttpResult(
                StatusCodes.Status413PayloadTooLarge,
                new ProblemResponseBody(
                    $"Legacy JSON control uploads are limited to {legacyPackageLimit} decoded " +
                    "bytes. Use 'application/octet-stream' for larger packages.")));
        }

        var requestSize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (requestSize is { IsReadOnly: false })
        {
            requestSize.MaxRequestBodySize = legacyRequestLimit;
        }
    }
}
