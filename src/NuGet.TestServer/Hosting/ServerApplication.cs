using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Http.Features;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Faults;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Requests;
using NuGet.TestServer.Vulnerabilities;
using NuGet.Versioning;

namespace NuGet.TestServer.Hosting;

public static class ServerApplication
{
    private const long LegacyJsonPackageLimit = 4L * 1024 * 1024;

    public static WebApplication Build(
        string[]? args = null,
        string? url = null,
        string? storageDirectory = null,
        AuthenticationConfiguration? authentication = null,
        VulnerabilitySnapshotProvider? vulnerabilities = null,
        ServerMode mode = ServerMode.Test,
        RuntimeStateConfiguration? runtimeState = null,
        PackageTransferLimits? packageLimits = null,
        TrustedProxyOptions? trustedProxies = null,
        int maximumAuthenticationFailures = 5)
    {
        var hosting = ServerHostingOptions.Create(
            mode,
            url ?? "http://127.0.0.1:0",
            authentication ?? AuthenticationConfiguration.Anonymous,
            trustedProxies);
        var builder = WebApplication.CreateBuilder(args ?? []);
        runtimeState ??= RuntimeStateConfiguration.FromConfiguration(builder.Configuration);
        packageLimits = (packageLimits ?? PackageTransferLimits.Default).Validate();
        builder.WebHost.UseUrls(hosting.Url);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = packageLimits.MaxRequestBodyBytes;
        });
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MemoryBufferThreshold = 64 * 1024;
            options.MultipartBodyLengthLimit = packageLimits.MaxRequestBodyBytes;
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(hosting);
        builder.Services.AddSingleton(hosting.Authentication);
        builder.Services.AddSingleton(
            new AuthenticationAttemptLimiter(
                maximumAuthenticationFailures,
                TimeSpan.FromMinutes(1),
                TimeProvider.System));
        builder.Services.AddSingleton<ISecurityAuditSink>(
            new SecurityAuditSink(storageDirectory));
        builder.Services.AddSingleton<IPackageOwnershipStore>(
            new PackageOwnershipStore(storageDirectory));
        builder.Services.AddSingleton(runtimeState);
        builder.Services.AddSingleton(packageLimits);
        builder.Services.AddSingleton<IPackageStore>(_ =>
            storageDirectory is null
                ? new InMemoryPackageStore(limits: packageLimits)
                : new DurablePackageStore(storageDirectory, packageLimits));
        builder.Services.AddSingleton<FaultRuleStore>();
        builder.Services.AddSingleton<RequestRecorder>();
        builder.Services.AddSingleton(
            vulnerabilities ??
            new VulnerabilitySnapshotProvider(EmbeddedVulnerabilitySnapshot.Load()));

        var app = builder.Build();
        try
        {
            _ = app.Services.GetRequiredService<IPackageStore>();
        }
        catch
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        MapMiddleware(app, mode);
        MapProtocolEndpoints(app);
        MapHealthEndpoint(app, mode);
        if (mode == ServerMode.Test)
        {
            MapControlEndpoints(app);
        }

        return app;
    }

    private static void MapMiddleware(WebApplication app, ServerMode mode)
    {
        if (mode == ServerMode.Test)
        {
            app.Use(async (context, next) =>
            {
                var recorder = context.RequestServices.GetRequiredService<RequestRecorder>();
                var faults = context.RequestServices.GetRequiredService<FaultRuleStore>();
                var sequence = recorder.NextSequence();
                var started = Stopwatch.GetTimestamp();
                string? faultRuleId = null;

                try
                {
                    var fault = context.Request.Path.StartsWithSegments("/__test")
                        ? null
                        : faults.Match(context.Request.Method, context.Request.Path);
                    if (fault is not null)
                    {
                        faultRuleId = fault.Id;
                        if (fault.Delay > TimeSpan.Zero)
                        {
                            await Task.Delay(fault.Delay, context.RequestAborted);
                        }

                        context.Response.StatusCode = (int)fault.StatusCode;
                        return;
                    }

                    await next(context);
                }
                finally
                {
                    if (!ClearsRequestHistory(context.Request))
                    {
                        recorder.Add(new RequestRecord(
                            sequence,
                            recorder.UtcNow,
                            context.Request.Method,
                            context.Request.Path,
                            context.Response.StatusCode,
                            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                            faultRuleId,
                            context.User.Identity?.Name));
                    }
                }
            });
        }

        app.UseMiddleware<NuGetAuthenticationMiddleware>();
    }

    private static void MapProtocolEndpoints(WebApplication app)
    {
        app.MapMethods(
            "/v3/index.json",
            [HttpMethods.Get, HttpMethods.Head],
            (HttpContext context, VulnerabilitySnapshotProvider vulnerabilities) =>
        {
            var root = GetRoot(context);
            var resources = new object[]
            {
                Descriptor($"{root}/flatcontainer/", "PackageBaseAddress/3.0.0"),
                Descriptor($"{root}/registration/", "RegistrationsBaseUrl/3.6.0"),
                Descriptor($"{root}/query", "SearchQueryService/3.0.0-beta"),
                Descriptor($"{root}/query", "SearchQueryService/3.5.0"),
                Descriptor($"{root}/package", "PackagePublish/2.0.0"),
                Descriptor($"{root}/symbolpackage", "SymbolPackagePublish/4.9.0"),
                Descriptor($"{root}/v3/vulnerabilities/index.json", "VulnerabilityInfo/6.7.0")
            };
            return Results.Json(new Dictionary<string, object?>
            {
                ["version"] = "3.0.0",
                ["resources"] = resources
            });
        }).WithMetadata(NuGetAccessRequirement.Read);

        app.MapMethods(
            "/v3/vulnerabilities/index.json",
            [HttpMethods.Get, HttpMethods.Head],
            (HttpContext context, VulnerabilitySnapshotProvider vulnerabilities) =>
                Results.Bytes(
                    vulnerabilities.Active.CreateLocalIndex(new Uri($"{GetRoot(context)}/")),
                    "application/json"))
            .WithMetadata(NuGetAccessRequirement.Read);

        app.MapMethods(
            "/v3/vulnerabilities/{snapshotId}/{pageName}.json",
            [HttpMethods.Get, HttpMethods.Head],
            IResult (
                string snapshotId,
                string pageName,
                VulnerabilitySnapshotProvider vulnerabilities) =>
            {
                return vulnerabilities.TryGet(snapshotId, out var snapshot) &&
                       snapshot!.TryGetPage(pageName, out var page)
                    ? Results.Bytes(page!.Content.ToArray(), "application/json")
                    : Results.NotFound();
            })
            .WithMetadata(NuGetAccessRequirement.Read);

        app.MapMethods(
            "/flatcontainer/{id}/index.json",
            [HttpMethods.Get, HttpMethods.Head],
            async Task<IResult> (string id, IPackageStore store, CancellationToken token) =>
            {
                var packages = await store.FindByIdAsync(id, token);
                return packages.Count == 0
                    ? Results.NotFound()
                    : Results.Json(new
                    {
                        versions = packages.Select(package => package.NormalizedVersion)
                    });
            }).WithMetadata(NuGetAccessRequirement.Read);

        app.MapMethods(
            "/flatcontainer/{id}/{version}/{fileName}",
            [HttpMethods.Get, HttpMethods.Head],
            async Task<IResult> (
                string id,
                string version,
                string fileName,
                IPackageStore store,
                CancellationToken token) =>
            {
                var package = await store.FindAsync(id, version, token);
                if (package is null)
                {
                    return Results.NotFound();
                }

                var normalizedId = package.Identity.Id.ToLowerInvariant();
                if (fileName.Equals($"{normalizedId}.{package.NormalizedVersion}.nupkg",
                        StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        return Results.File(
                            package.OpenReadStream(),
                            "application/octet-stream",
                            enableRangeProcessing: true);
                    }
                    catch (FileNotFoundException)
                    {
                        return Results.NotFound();
                    }
                }

                if (fileName.Equals($"{normalizedId}.nuspec", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.File(package.NuspecContent, "text/xml; charset=utf-8");
                }

                if (fileName.Equals(
                        $"{normalizedId}.{package.NormalizedVersion}.nupkg.sha512",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Text(package.PackageHash, "text/plain; charset=utf-8");
                }

                return Results.NotFound();
            }).WithMetadata(NuGetAccessRequirement.Read);

        app.MapMethods(
            "/registration/{id}/index.json",
            [HttpMethods.Get, HttpMethods.Head],
            async Task<IResult> (
                HttpContext context,
                string id,
                IPackageStore store,
                VulnerabilitySnapshotProvider vulnerabilities,
                CancellationToken token) =>
            {
                var packages = await store.FindByIdAsync(id, token);
                if (packages.Count == 0)
                {
                    return Results.NotFound();
                }

                var root = GetRoot(context);
                var normalizedId = packages[0].Identity.Id.ToLowerInvariant();
                return Results.Json(new Dictionary<string, object?>
                {
                    ["@id"] = $"{root}/registration/{normalizedId}/index.json",
                    ["count"] = 1,
                    ["items"] = new[]
                    {
                        RegistrationPage(context, packages, vulnerabilities)
                    }
                });
            }).WithMetadata(NuGetAccessRequirement.Read);

        app.MapMethods(
            "/registration/{id}/page/{lower}/{upper}.json",
            [HttpMethods.Get, HttpMethods.Head],
            async Task<IResult> (
                HttpContext context,
                string id,
                string lower,
                string upper,
                IPackageStore store,
                VulnerabilitySnapshotProvider vulnerabilities,
                CancellationToken token) =>
            {
                var packages = await store.FindByIdAsync(id, token);
                return RegistrationPageBounds.Matches(packages, lower, upper)
                    ? Results.Json(RegistrationPage(context, packages, vulnerabilities))
                    : Results.NotFound();
            }).WithMetadata(NuGetAccessRequirement.Read);

        app.MapMethods(
            "/registration/{id}/{version}.json",
            [HttpMethods.Get, HttpMethods.Head],
            async Task<IResult> (
                HttpContext context,
                string id,
                string version,
                IPackageStore store,
                VulnerabilitySnapshotProvider vulnerabilities,
                CancellationToken token) =>
            {
                var package = await store.FindAsync(id, version, token);
                return package is null
                    ? Results.NotFound()
                    : Results.Json(RegistrationLeaf(context, package, vulnerabilities));
            }).WithMetadata(NuGetAccessRequirement.Read);

        app.MapMethods(
            "/query",
            [HttpMethods.Get, HttpMethods.Head],
            async Task<IResult> (
                HttpContext context,
                IPackageStore store,
                string? q,
                int? skip,
                int? take,
                bool? prerelease,
                string? packageType,
                CancellationToken token) =>
            {
                var page = await store.SearchAsync(
                    q ?? string.Empty,
                    prerelease ?? false,
                    skip ?? 0,
                    take ?? 20,
                    token,
                    packageType);

                return Results.Json(new
                {
                    totalHits = page.TotalHits,
                    data = page.Items.Select(item => SearchResult(
                        context,
                        item.Package,
                        item.Versions))
                });
            }).WithMetadata(NuGetAccessRequirement.Read);

        app.MapPut("/package", PublishPackageAsync)
            .WithMetadata(
                app.Services.GetRequiredService<AuthenticationConfiguration>().Profile ==
                AuthenticationProfile.Production
                    ? NuGetAccessRequirement.Publish
                    : NuGetAccessRequirement.Write);

        app.MapPut("/symbolpackage", PublishSymbolPackageAsync)
            .WithMetadata(NuGetAccessRequirement.Write);

        app.MapDelete(
            "/package/{id}/{version}",
            async Task<IResult> (
                HttpContext context,
                string id,
                string version,
                IPackageStore store,
                IPackageOwnershipStore ownership,
                ISecurityAuditSink audits,
                CancellationToken token) =>
            {
                if (!CanManagePackage(context, id, ownership, audits))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                return await store.SetListedAsync(id, version, false, token)
                    ? Results.NoContent()
                    : Results.NotFound();
            })
            .WithMetadata(
                app.Services.GetRequiredService<AuthenticationConfiguration>().Profile ==
                AuthenticationProfile.Production
                    ? NuGetAccessRequirement.Unlist
                    : NuGetAccessRequirement.Write);

        if (app.Services.GetRequiredService<AuthenticationConfiguration>().Profile ==
            AuthenticationProfile.Production)
        {
            app.MapDelete(
                "/package/{id}/{version}/hard",
                async Task<IResult> (
                    HttpContext context,
                    string id,
                    string version,
                    IPackageStore store,
                    IPackageOwnershipStore ownership,
                    ISecurityAuditSink audits,
                    CancellationToken token) =>
                {
                    if (!CanManagePackage(context, id, ownership, audits))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }

                    return await store.DeleteAsync(id, version, token)
                        ? Results.NoContent()
                        : Results.NotFound();
                })
                .WithMetadata(NuGetAccessRequirement.Delete);
        }
    }

    private static void MapHealthEndpoint(WebApplication app, ServerMode mode)
    {
        app.MapGet("/__test/health", () => Results.Json(new
        {
            status = "healthy",
            mode = mode.ToString().ToLowerInvariant()
        }))
            .WithMetadata(NuGetAccessRequirement.Anonymous);
    }

    private static void MapControlEndpoints(WebApplication app)
    {
        app.MapGet(
            "/__test/state",
            async (IPackageStore packages, FaultRuleStore faults, RequestRecorder requests) =>
                Results.Json(new
                {
                    packageCount = (await packages.GetAllAsync()).Count,
                    faultCount = faults.GetAll().Count,
                    faultCapacity = faults.Capacity,
                    requestCount = requests.GetAll().Count,
                    requestCapacity = requests.Capacity,
                    evictedRequestCount = requests.EvictedCount
                }))
            .WithMetadata(NuGetAccessRequirement.Control);

        app.MapPost(
            "/__test/reset",
            async (IPackageStore packages, FaultRuleStore faults, RequestRecorder requests) =>
            {
                await packages.ResetAsync();
                faults.Reset();
                requests.Reset();
                return Results.NoContent();
            })
            .WithMetadata(NuGetAccessRequirement.Control);

        app.MapGet(
            "/__test/packages",
            async (IPackageStore store, CancellationToken token) =>
                Results.Json((await store.GetAllAsync(token)).Select(PackageSummary)))
            .WithMetadata(NuGetAccessRequirement.Control);

        app.MapPost(
            "/__test/packages",
            async Task<IResult> (
                HttpRequest request,
                IPackageStore store,
                PackageTransferLimits limits,
                CancellationToken token) =>
            {
                if (request.ContentType?.StartsWith(
                        "application/json",
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    var legacyPackageLimit = Math.Min(
                        limits.MaxPackageBytes,
                        LegacyJsonPackageLimit);
                    var maximumBase64Length = checked(
                        ((legacyPackageLimit + 2) / 3) * 4);
                    var legacyRequestLimit = Math.Min(
                        limits.MaxRequestBodyBytes,
                        checked(maximumBase64Length + 1024));
                    if (request.ContentLength > legacyRequestLimit)
                    {
                        return Results.Problem(
                            $"Legacy JSON control uploads are limited to " +
                            $"{legacyPackageLimit} decoded bytes. Use " +
                            "'application/octet-stream' for larger packages.",
                            statusCode: StatusCodes.Status413PayloadTooLarge);
                    }

                    var requestSize = request.HttpContext.Features
                        .Get<IHttpMaxRequestBodySizeFeature>();
                    if (requestSize is { IsReadOnly: false })
                    {
                        requestSize.MaxRequestBodySize = legacyRequestLimit;
                    }

                    PackageContentRequest? packageRequest;
                    try
                    {
                        packageRequest = await request.ReadFromJsonAsync<PackageContentRequest>(
                            cancellationToken: token);
                    }
                    catch (JsonException)
                    {
                        return Results.Problem(
                            "The package request must contain valid JSON and base64 content.",
                            statusCode: StatusCodes.Status400BadRequest);
                    }

                    if (packageRequest?.Content is null)
                    {
                        return Results.Problem(
                            "The package request must contain valid JSON and base64 content.",
                            statusCode: StatusCodes.Status400BadRequest);
                    }

                    if (packageRequest.Content.Length > maximumBase64Length)
                    {
                        return Results.Problem(
                            $"Legacy JSON control uploads are limited to " +
                            $"{legacyPackageLimit} decoded bytes. Use " +
                            "'application/octet-stream' for larger packages.",
                            statusCode: StatusCodes.Status413PayloadTooLarge);
                    }

                    byte[] content;
                    try
                    {
                        content = Convert.FromBase64String(packageRequest.Content);
                    }
                    catch (FormatException)
                    {
                        return Results.Problem(
                            "Package content must be valid base64.",
                            statusCode: StatusCodes.Status400BadRequest);
                    }

                    await using var contentStream = new MemoryStream(content, writable: false);
                    return await AddPackageAsync(contentStream, store, limits, token);
                }

                return await AddPackageAsync(request.Body, store, limits, token);
            })
            .WithMetadata(NuGetAccessRequirement.Control);

        app.MapDelete(
            "/__test/packages/{id}/{version}",
            async Task<IResult> (
                string id,
                string version,
                IPackageStore store,
                CancellationToken token) =>
                await store.DeleteAsync(id, version, token)
                    ? Results.NoContent()
                    : Results.NotFound())
            .WithMetadata(NuGetAccessRequirement.Control);

        app.MapPost(
            "/__test/packages/{id}/{version}/list",
            async Task<IResult> (
                string id,
                string version,
                IPackageStore store,
                CancellationToken token) =>
                await store.SetListedAsync(id, version, true, token)
                    ? Results.NoContent()
                    : Results.NotFound())
            .WithMetadata(NuGetAccessRequirement.Control);

        app.MapPost(
            "/__test/packages/{id}/{version}/unlist",
            async Task<IResult> (
                string id,
                string version,
                IPackageStore store,
                CancellationToken token) =>
                await store.SetListedAsync(id, version, false, token)
                    ? Results.NoContent()
                    : Results.NotFound())
            .WithMetadata(NuGetAccessRequirement.Control);

        app.MapPut(
            "/__test/packages/{id}/{version}/metadata",
            async Task<IResult> (
                string id,
                string version,
                PackageRepositoryMetadata metadata,
                IPackageStore store,
                CancellationToken token) =>
            {
                var validationError = ValidateRepositoryMetadata(metadata);
                if (validationError is not null)
                {
                    return Results.Problem(
                        validationError,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                return await store.SetRepositoryMetadataAsync(id, version, metadata, token)
                    ? Results.NoContent()
                    : Results.NotFound();
            })
            .WithMetadata(NuGetAccessRequirement.Control);

        app.MapGet("/__test/requests", (RequestRecorder recorder) => Results.Json(recorder.GetAll()))
            .WithMetadata(NuGetAccessRequirement.Control);
        app.MapDelete("/__test/requests", (RequestRecorder recorder) =>
        {
            recorder.Reset();
            return Results.NoContent();
        }).WithMetadata(NuGetAccessRequirement.Control);

        app.MapGet("/__test/faults", (FaultRuleStore faults) => Results.Json(faults.GetAll()))
            .WithMetadata(NuGetAccessRequirement.Control);
        app.MapPost("/__test/faults", IResult (FaultRule rule, FaultRuleStore faults) =>
        {
            try
            {
                faults.Add(rule);
                return Results.Created($"/__test/faults/{Uri.EscapeDataString(rule.Id)}", rule);
            }
            catch (FaultRuleStore.FaultRuleConflictException exception)
            {
                return Results.Problem(
                    exception.Message,
                    statusCode: StatusCodes.Status409Conflict);
            }
        }).WithMetadata(NuGetAccessRequirement.Control);
        app.MapDelete("/__test/faults", (FaultRuleStore faults) =>
        {
            faults.Reset();
            return Results.NoContent();
        }).WithMetadata(NuGetAccessRequirement.Control);
    }

    private static async Task<IResult> PublishPackageAsync(
        HttpRequest request,
        IPackageStore store,
        IPackageOwnershipStore ownership,
        ISecurityAuditSink audits,
        PackageTransferLimits limits,
        CancellationToken token)
    {
        if (request.HasFormContentType)
        {
            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(token);
            }
            catch (InvalidDataException exception)
            {
                return Results.Problem(
                    exception.Message,
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            var file = form.Files.FirstOrDefault();
            if (file is null)
            {
                return Results.Problem("The multipart request contains no package.");
            }

            await using var stream = file.OpenReadStream();
            return await PublishPackageContentAsync(
                request,
                stream,
                store,
                ownership,
                audits,
                limits,
                token);
        }

        return await PublishPackageContentAsync(
            request,
            request.Body,
            store,
            ownership,
            audits,
            limits,
            token);
    }

    private static async Task<IResult> PublishPackageContentAsync(
        HttpRequest request,
        Stream content,
        IPackageStore store,
        IPackageOwnershipStore ownership,
        ISecurityAuditSink audits,
        PackageTransferLimits limits,
        CancellationToken token)
    {
        TestPackage? package = null;
        try
        {
            package = await TestPackage.FromStreamAsync(
                content,
                limits,
                cancellationToken: token);

            var identity =
                request.HttpContext.Items[typeof(ProductionIdentity)] as ProductionIdentity;
            if (identity is not null)
            {
                if (!identity.AllowsPackage(package.Identity.Id))
                {
                    WriteAuthorizationDenied(
                        request.HttpContext,
                        audits,
                        identity,
                        $"Package '{package.Identity.Id}' is outside configured namespaces.");
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                var publishResult = await ownership.PublishAsync(
                    package.Identity.Id,
                    identity.Name,
                    identity.HasScope(SecurityScope.Admin),
                    async cancellationToken =>
                        (await store.FindByIdAsync(
                            package.Identity.Id,
                            cancellationToken)).Count > 0,
                    async cancellationToken =>
                    {
                        await store.AddAsync(package, cancellationToken);
                    },
                    token);

                if (!publishResult.Authorized)
                {
                    WriteAuthorizationDenied(
                        request.HttpContext,
                        audits,
                        identity,
                        $"Package '{package.Identity.Id}' is owned by another identity.");
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                if (publishResult.OwnershipClaimed)
                {
                    audits.Write(CreateAudit(
                        request.HttpContext,
                        SecurityAuditEventType.PackageOwnershipClaimed,
                        identity.Name,
                        package.Identity.Id));
                }
            }
            else
            {
                await store.AddAsync(package, token);
            }

            var result = Results.Created(
                $"/__test/packages/{Uri.EscapeDataString(package.Identity.Id)}/{package.NormalizedVersion}",
                PackageSummary(package));
            package = null;
            return result;
        }
        catch (PackageLimitExceededException exception)
        {
            return Results.Problem(
                exception.Message,
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch (InvalidPackageException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (DuplicatePackageException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        finally
        {
            package?.Dispose();
        }
    }

    private static async Task<IResult> PublishSymbolPackageAsync(
        HttpRequest request,
        IPackageStore store,
        CancellationToken token)
    {
        byte[] content;
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(token);
            var file = form.Files.FirstOrDefault();
            if (file is null)
            {
                return Results.Problem("The multipart request contains no symbol package.");
            }

            await using var stream = file.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, token);
            content = buffer.ToArray();
        }
        else
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, token);
            content = buffer.ToArray();
        }

        try
        {
            var package = TestPackage.FromContent(content);
            await store.AddSymbolAsync(content, token);
            return Results.Created(
                $"/__test/packages/{Uri.EscapeDataString(package.Identity.Id)}/{package.NormalizedVersion}/symbols",
                new { id = package.Identity.Id, version = package.NormalizedVersion });
        }
        catch (InvalidPackageException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (DuplicatePackageException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> AddPackageAsync(
        Stream content,
        IPackageStore store,
        PackageTransferLimits limits,
        CancellationToken token)
    {
        TestPackage? package = null;
        try
        {
            package = await TestPackage.FromStreamAsync(
                content,
                limits,
                cancellationToken: token);
            await store.AddAsync(package, token);
            var result = Results.Created(
                $"/__test/packages/{Uri.EscapeDataString(package.Identity.Id)}/{package.NormalizedVersion}",
                PackageSummary(package));
            package = null;
            return result;
        }
        catch (PackageLimitExceededException exception)
        {
            return Results.Problem(
                exception.Message,
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }
        catch (InvalidPackageException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (DuplicatePackageException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        finally
        {
            package?.Dispose();
        }
    }

    private static async Task<IResult> AddPackageAsync(
        TestPackage package,
        IPackageStore store,
        CancellationToken token)
    {
        try
        {
            await store.AddAsync(package, token);
            return Results.Created(
                $"/__test/packages/{Uri.EscapeDataString(package.Identity.Id)}/{package.NormalizedVersion}",
                PackageSummary(package));
        }
        catch (DuplicatePackageException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static bool CanManagePackage(
        HttpContext context,
        string packageId,
        IPackageOwnershipStore ownership,
        ISecurityAuditSink audits)
    {
        var identity = context.Items[typeof(ProductionIdentity)] as ProductionIdentity;
        if (identity is null)
        {
            return true;
        }

        var owner = ownership.GetOwner(packageId);
        if ((owner is not null &&
             string.Equals(owner, identity.Name, StringComparison.Ordinal)) ||
            identity.HasScope(SecurityScope.Admin))
        {
            return true;
        }

        WriteAuthorizationDenied(
            context,
            audits,
            identity,
            owner is null
                ? $"Package '{packageId}' has no recorded owner."
                : $"Package '{packageId}' is owned by another identity.");
        return false;
    }

    private static void WriteAuthorizationDenied(
        HttpContext context,
        ISecurityAuditSink audits,
        ProductionIdentity identity,
        string detail) =>
        audits.Write(CreateAudit(
            context,
            SecurityAuditEventType.AuthorizationDenied,
            identity.Name,
            detail));

    private static SecurityAuditEvent CreateAudit(
        HttpContext context,
        SecurityAuditEventType eventType,
        string? identity,
        string? detail) =>
        new(
            DateTimeOffset.UtcNow,
            eventType,
            context.Items[SecurityContextItems.Client] as string ??
            context.Connection.RemoteIpAddress?.ToString() ??
            "unknown",
            identity,
            context.Request.Method,
            context.Request.Path,
            detail);

    private static object PackageSummary(TestPackage package) => new
    {
        id = package.Identity.Id,
        version = package.NormalizedVersion,
        listed = package.IsListed,
        published = package.Published
    };

    private static object RegistrationLeaf(
        HttpContext context,
        TestPackage package,
        VulnerabilitySnapshotProvider vulnerabilities)
    {
        var root = GetRoot(context);
        var id = package.Identity.Id.ToLowerInvariant();
        var version = package.NormalizedVersion;
        var catalogEntry = new Dictionary<string, object?>
        {
            ["@id"] = $"{root}/registration/{id}/{version}.json",
            ["@type"] = "PackageDetails",
            ["id"] = package.Identity.Id,
            ["version"] = version,
            ["authors"] = package.Authors,
            ["owners"] = package.RepositoryMetadata.Owners,
            ["downloads"] = package.RepositoryMetadata.Downloads,
            ["description"] = package.Description,
            ["summary"] = package.Summary,
            ["title"] = string.IsNullOrEmpty(package.Title) ? package.Identity.Id : package.Title,
            ["tags"] = package.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ["projectUrl"] = package.ProjectUrl,
            ["readme"] = package.Readme,
            ["icon"] = package.Icon,
            ["licenseExpression"] = package.LicenseExpression,
            ["licenseFile"] = package.LicenseFile,
            ["licenseUrl"] = package.LicenseUrl,
            ["packageTypes"] = package.EffectivePackageTypes.Select(type => new
            {
                name = type.Name,
                version = type.Version
            }),
            ["repository"] = package.Repository is null ? null : new
            {
                type = package.Repository.Type,
                url = package.Repository.Url,
                commit = package.Repository.Commit,
                branch = package.Repository.Branch
            },
            ["listed"] = package.IsListed,
            ["published"] = package.Published,
            ["dependencyGroups"] = package.DependencyGroups.Select(group => new
            {
                targetFramework = group.TargetFramework.GetShortFolderName(),
                dependencies = group.Packages.Select(dependency => new
                {
                    id = dependency.Id,
                    range = dependency.VersionRange.ToNormalizedString()
                })
            })
        };
        if (package.RepositoryMetadata.Deprecation is { } deprecation)
        {
            catalogEntry["deprecation"] = new
            {
                reasons = deprecation.Reasons,
                message = deprecation.Message,
                alternatePackage = deprecation.AlternatePackage is null ? null : new
                {
                    id = deprecation.AlternatePackage.Id,
                    range = deprecation.AlternatePackage.Range
                }
            };
        }
        var advisories = vulnerabilities.Active.Find(package.Identity.Id, package.Identity.Version);
        if (advisories.Count > 0)
        {
            catalogEntry["vulnerabilities"] = advisories.Select(advisory => new
            {
                advisoryUrl = advisory.Url.AbsoluteUri,
                severity = advisory.Severity.ToString()
            });
        }

        return new Dictionary<string, object?>
        {
            ["@id"] = $"{root}/registration/{id}/{version}.json",
            ["@type"] = "Package",
            ["catalogEntry"] = catalogEntry,
            ["packageContent"] = $"{root}/flatcontainer/{id}/{version}/{id}.{version}.nupkg",
            ["registration"] = $"{root}/registration/{id}/index.json"
        };
    }

    private static Dictionary<string, object?> RegistrationPage(
        HttpContext context,
        IReadOnlyList<TestPackage> packages,
        VulnerabilitySnapshotProvider vulnerabilities)
    {
        var first = packages[0];
        var last = packages[^1];
        var root = GetRoot(context);
        var normalizedId = first.Identity.Id.ToLowerInvariant();
        var parent = $"{root}/registration/{normalizedId}/index.json";
        return new Dictionary<string, object?>
        {
            ["@id"] =
                $"{root}/registration/{normalizedId}/page/{first.NormalizedVersion}/{last.NormalizedVersion}.json",
            ["@type"] = "catalog:CatalogPage",
            ["parent"] = parent,
            ["count"] = packages.Count,
            ["lower"] = first.NormalizedVersion,
            ["upper"] = last.NormalizedVersion,
            ["items"] = packages.Select(
                package => RegistrationLeaf(context, package, vulnerabilities))
        };
    }

    private static object SearchResult(
        HttpContext context,
        TestPackage package,
        IReadOnlyList<TestPackage> versions)
    {
        var root = GetRoot(context);
        var id = package.Identity.Id.ToLowerInvariant();
        var version = package.NormalizedVersion;
        return new Dictionary<string, object?>
        {
            ["@id"] = $"{root}/registration/{id}/{version}.json",
            ["@type"] = "Package",
            ["registration"] = $"{root}/registration/{id}/index.json",
            ["id"] = package.Identity.Id,
            ["version"] = version,
            ["description"] = package.Description,
            ["summary"] = string.IsNullOrEmpty(package.Summary)
                ? package.Description
                : package.Summary,
            ["title"] = string.IsNullOrEmpty(package.Title) ? package.Identity.Id : package.Title,
            ["tags"] = package.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ["authors"] = new[] { package.Authors },
            ["owners"] = package.RepositoryMetadata.Owners,
            ["projectUrl"] = package.ProjectUrl,
            ["totalDownloads"] = versions.Sum(item => item.RepositoryMetadata.Downloads),
            ["verified"] = package.RepositoryMetadata.Verified,
            ["packageTypes"] = package.EffectivePackageTypes.Select(type => new
            {
                name = type.Name,
                version = type.Version
            }),
            ["versions"] = versions.Select(item =>
                new Dictionary<string, object?>
                {
                    ["version"] = item.NormalizedVersion,
                    ["downloads"] = item.RepositoryMetadata.Downloads,
                    ["@id"] = $"{root}/registration/{id}/{item.NormalizedVersion}.json"
                })
        };
    }

    private static Dictionary<string, string> Descriptor(string id, string type) => new()
    {
        ["@id"] = id,
        ["@type"] = type
    };

    private static string GetRoot(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}";

    private static string? ValidateRepositoryMetadata(PackageRepositoryMetadata metadata)
    {
        if (metadata.Downloads < 0)
        {
            return "Downloads cannot be negative.";
        }

        if (metadata.Owners is null || metadata.Owners.Any(string.IsNullOrWhiteSpace))
        {
            return "Owners cannot contain empty values.";
        }

        if (metadata.Deprecation is not { } deprecation)
        {
            return null;
        }

        string[] validReasons = ["Legacy", "CriticalBugs", "Other"];
        if (deprecation.Reasons is null ||
            deprecation.Reasons.Count == 0 ||
            deprecation.Reasons.Any(reason =>
                !validReasons.Contains(reason, StringComparer.OrdinalIgnoreCase)))
        {
            return "Deprecation reasons must be Legacy, CriticalBugs, or Other.";
        }

        if (deprecation.AlternatePackage is { } alternate &&
            (string.IsNullOrWhiteSpace(alternate.Id) ||
             !VersionRange.TryParse(alternate.Range, out _)))
        {
            return "The alternate package requires an ID and valid version range.";
        }

        return null;
    }

    public sealed record PackageContentRequest(string? Content);

    private static bool ClearsRequestHistory(HttpRequest request) =>
        (HttpMethods.IsPost(request.Method) &&
         request.Path.Equals("/__test/reset", StringComparison.OrdinalIgnoreCase)) ||
        (HttpMethods.IsDelete(request.Method) &&
         request.Path.Equals("/__test/requests", StringComparison.OrdinalIgnoreCase));
}
