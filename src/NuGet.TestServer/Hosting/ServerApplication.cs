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
        PackageTransferLimits? packageLimits = null)
    {
        packageLimits = (packageLimits ?? PackageTransferLimits.Default).Validate();
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.WebHost.UseUrls(url ?? "http://127.0.0.1:0");
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
        builder.Services.AddSingleton(authentication ?? AuthenticationConfiguration.Anonymous);
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

        MapMiddleware(app);
        MapProtocolEndpoints(app);
        MapControlEndpoints(app);
        return app;
    }

    private static void MapMiddleware(WebApplication app)
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
        });

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

                var first = packages[0];
                var last = packages[^1];
                var root = GetRoot(context);
                var normalizedId = first.Identity.Id.ToLowerInvariant();
                return Results.Json(new Dictionary<string, object?>
                {
                    ["@id"] = $"{root}/registration/{normalizedId}/index.json",
                    ["count"] = 1,
                    ["items"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["@id"] =
                                $"{root}/registration/{normalizedId}/page/{first.NormalizedVersion}/{last.NormalizedVersion}.json",
                            ["@type"] = "catalog:CatalogPage",
                            ["count"] = packages.Count,
                            ["lower"] = first.NormalizedVersion,
                            ["upper"] = last.NormalizedVersion,
                            ["items"] = packages.Select(
                                package => RegistrationLeaf(context, package, vulnerabilities))
                        }
                    }
                });
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
                CancellationToken token) =>
            {
                var packages = await store.SearchAsync(
                    q ?? string.Empty,
                    prerelease ?? false,
                    skip ?? 0,
                    take ?? 20,
                    token);

                return Results.Json(new
                {
                    totalHits = packages.Count,
                    data = packages.Select(package => SearchResult(context, package))
                });
            }).WithMetadata(NuGetAccessRequirement.Read);

        app.MapPut("/package", PublishPackageAsync)
            .WithMetadata(NuGetAccessRequirement.Write);

        app.MapDelete(
            "/package/{id}/{version}",
            async Task<IResult> (
                string id,
                string version,
                IPackageStore store,
                CancellationToken token) =>
                await store.SetListedAsync(id, version, false, token)
                    ? Results.NoContent()
                    : Results.NotFound())
            .WithMetadata(NuGetAccessRequirement.Write);
    }

    private static void MapControlEndpoints(WebApplication app)
    {
        app.MapGet("/__test/health", () => Results.Json(new { status = "healthy" }))
            .WithMetadata(NuGetAccessRequirement.Anonymous);

        app.MapGet(
            "/__test/state",
            async (IPackageStore packages, FaultRuleStore faults, RequestRecorder requests) =>
                Results.Json(new
                {
                    packageCount = (await packages.GetAllAsync()).Count,
                    faultCount = faults.GetAll().Count,
                    requestCount = requests.GetAll().Count
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

        app.MapGet("/__test/requests", (RequestRecorder recorder) => Results.Json(recorder.GetAll()))
            .WithMetadata(NuGetAccessRequirement.Control);
        app.MapDelete("/__test/requests", (RequestRecorder recorder) =>
        {
            recorder.Reset();
            return Results.NoContent();
        }).WithMetadata(NuGetAccessRequirement.Control);

        app.MapGet("/__test/faults", (FaultRuleStore faults) => Results.Json(faults.GetAll()))
            .WithMetadata(NuGetAccessRequirement.Control);
        app.MapPost("/__test/faults", (FaultRule rule, FaultRuleStore faults) =>
        {
            faults.Add(rule);
            return Results.Created($"/__test/faults/{Uri.EscapeDataString(rule.Id)}", rule);
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
            return await AddPackageAsync(stream, store, limits, token);
        }

        return await AddPackageAsync(request.Body, store, limits, token);
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
            ["description"] = package.Description,
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

    private static object SearchResult(HttpContext context, TestPackage package)
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
            ["summary"] = package.Description,
            ["title"] = package.Identity.Id,
            ["tags"] = package.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ["authors"] = new[] { package.Authors },
            ["owners"] = new[] { package.Authors },
            ["totalDownloads"] = 0,
            ["verified"] = false,
            ["packageTypes"] = Array.Empty<object>(),
            ["versions"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["version"] = version,
                    ["downloads"] = 0,
                    ["@id"] = $"{root}/registration/{id}/{version}.json"
                }
            }
        };
    }

    private static Dictionary<string, string> Descriptor(string id, string type) => new()
    {
        ["@id"] = id,
        ["@type"] = type
    };

    private static string GetRoot(HttpContext context) =>
        $"{context.Request.Scheme}://{context.Request.Host}";

    public sealed record PackageContentRequest(string? Content);
}
