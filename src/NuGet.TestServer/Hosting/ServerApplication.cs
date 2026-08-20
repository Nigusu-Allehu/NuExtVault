using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Hosting.Endpoints;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Operations;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.Hosting;

public static class ServerApplication
{
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
        int maximumAuthenticationFailures = 5,
        SupplyChainOptions? supplyChain = null,
        IPackagePolicyScanner? packageScanner = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        runtimeState ??= RuntimeStateConfiguration.FromConfiguration(builder.Configuration);
        var profile = mode == ServerMode.Production
            ? ServerProfiles.Production
            : storageDirectory is null
                ? ServerProfiles.Embedded
                : ServerProfiles.Standard;
        var composition = mode == ServerMode.Production && storageDirectory is null
            ? ServerComposition.CreateProductionWithTemporaryStorage(
                url,
                authentication,
                vulnerabilities,
                runtimeState,
                packageLimits,
                trustedProxies,
                maximumAuthenticationFailures,
                supplyChain ?? new SupplyChainOptions(),
                packageScanner)
            : ServerComposition.Create(
                profile,
                url,
                storageDirectory,
                authentication,
                vulnerabilities,
                runtimeState,
                packageLimits,
                trustedProxies,
                maximumAuthenticationFailures,
                supplyChain ?? (mode == ServerMode.Production ? new SupplyChainOptions() : null),
                packageScanner);
        return BuildWithOwnership(builder, composition);
    }

    internal static WebApplication Build(
        ServerComposition composition,
        string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(composition);
        return BuildWithOwnership(WebApplication.CreateBuilder(args ?? []), composition);
    }

    private static WebApplication BuildWithOwnership(
        WebApplicationBuilder builder,
        ServerComposition composition)
    {
        try
        {
            return Build(builder, composition);
        }
        catch
        {
            composition.StorageLease?.Dispose();
            throw;
        }
    }

    private static WebApplication Build(
        WebApplicationBuilder builder,
        ServerComposition composition)
    {
        var hosting = composition.Hosting;
        var runtimeState = composition.RuntimeState;
        var packageLimits = composition.PackageLimits;
        var storageDirectory = composition.StorageDirectory;
        builder.WebHost.UseUrls(hosting.Url);
        if (hosting.Mode == ServerMode.Production)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole();
        }

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
        if (composition.StorageLease is not null)
        {
            builder.Services.AddSingleton<TemporaryStorageLease>(_ => composition.StorageLease);
        }
        builder.Services.AddSingleton(composition);
        builder.Services.AddSingleton(composition.Profile);
        builder.Services.AddSingleton(composition.ExtensionGraph);
        builder.Services.AddSingleton(hosting);
        builder.Services.AddSingleton(hosting.Authentication);
        builder.Services.AddSingleton(
            new AuthenticationAttemptLimiter(
                composition.MaximumAuthenticationFailures,
                TimeSpan.FromMinutes(1),
                TimeProvider.System));
        builder.Services.AddSingleton<ISecurityAuditSink>(
            new SecurityAuditSink(storageDirectory));
        builder.Services.AddSingleton<IPackageOwnershipStore>(
            new PackageOwnershipStore(storageDirectory));
        builder.Services.AddSingleton(runtimeState);
        builder.Services.AddSingleton(packageLimits);
        builder.Services.AddSingleton(PackageVisibilityPolicy.Instance);
        builder.Services.AddSingleton<IPackageStore>(_ =>
            storageDirectory is null
                ? new InMemoryPackageStore(limits: packageLimits)
                : new DurablePackageStore(storageDirectory, packageLimits));
        builder.Services.AddSingleton<IPackageCandidateStore>(provider =>
            new PackageCandidateReader(provider.GetRequiredService<IPackageStore>()));
        builder.Services.AddSingleton(provider => new PackageSupplyChainService(
            provider.GetRequiredService<IPackageStore>(),
            storageDirectory,
            composition.SupplyChain,
            composition.PackageScanner,
            provider.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(new StorageHealth(storageDirectory));
        builder.Services.AddSingleton<ServerDiagnostics>();
        builder.Services.AddSingleton<KernelRequestInstrumentation>();
        builder.Services.AddSingleton(
            composition.Vulnerabilities);
        builder.Services.AddSingleton(_ => new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        });
        builder.Services.AddSingleton<CapabilityAuditLog>();
        builder.Services.AddSingleton(new CapabilityLimits(
            MaximumConcurrentCalls: 64,
            MaximumStreamBytes: Math.Max(
                packageLimits.MaxRequestBodyBytes,
                packageLimits.MaxPackageBytes)));
        builder.Services.AddSingleton(provider => new CapabilityBroker(
            composition.InstanceId,
            composition.ExtensionGraph,
            provider.GetRequiredService<CapabilityAuditLog>(),
            provider.GetRequiredService<CapabilityLimits>(),
            new CapabilityServices(
                provider.GetRequiredService<IPackageStore>(),
                provider.GetRequiredService<IPackageCandidateStore>(),
                provider.GetRequiredService<PackageVisibilityPolicy>(),
                provider.GetRequiredService<PackageSupplyChainService>(),
                provider.GetRequiredService<KernelRequestInstrumentation>(),
                provider.GetRequiredService<StorageHealth>(),
                provider.GetRequiredService<ServerDiagnostics>(),
                composition.Hosting,
                composition.StorageDirectory,
                composition.Vulnerabilities,
                provider.GetRequiredService<HttpClient>())));
        builder.Services.AddSingleton(_ =>
            ServiceIndexResourceRegistry.Create(composition.ExtensionGraph));
        builder.Services.AddSingleton(provider => BuiltInOperationOwners.CreateRegistry(
            provider.GetRequiredService<CapabilityBroker>(),
            composition.ExtensionGraph,
            provider.GetRequiredService<ServiceIndexResourceRegistry>(),
            packageLimits));
        builder.Services.AddSingleton(provider => new OperationDispatcher(
            provider.GetRequiredService<OperationRegistry>(),
            provider.GetRequiredService<ServerDiagnostics>()));
        builder.Services.AddSingleton(provider => new OperationGateway(
            provider.GetRequiredService<OperationDispatcher>(),
            provider.GetRequiredService<ISecurityAuditSink>(),
            composition.InstanceId,
            provider.GetRequiredService<KernelRequestInstrumentation>()));

        var app = builder.Build();
        try
        {
            if (composition.StorageLease is not null)
            {
                _ = app.Services.GetRequiredService<TemporaryStorageLease>();
            }
            _ = app.Services.GetRequiredService<IPackageStore>();
            _ = app.Services.GetRequiredService<PackageSupplyChainService>();

            // Ownership, contracts, and route coverage are validated before listening.
            _ = app.Services.GetRequiredService<OperationGateway>();
        }
        catch
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        MapMiddleware(app);
        ProtocolEndpoints.Map(app);
        ModerationEndpoints.Map(app);
        HealthEndpoints.Map(app);
        if (hosting.Mode == ServerMode.Test)
        {
            ControlEndpoints.Map(app);
        }

        return app;
    }

    private static void MapMiddleware(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var diagnostics = context.RequestServices.GetRequiredService<ServerDiagnostics>();
            var logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("NuGet.TestServer.Requests");
            var started = Stopwatch.GetTimestamp();
            using var activity = diagnostics.StartRequest(context);
            try
            {
                await next(context);
                activity?.SetTag("http.response.status_code", context.Response.StatusCode);
                logger.LogInformation(
                    "Handled {Method} {Path} with {StatusCode} in {ElapsedMilliseconds} ms",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception exception)
            {
                diagnostics.RecordException(context);
                activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                logger.LogError(
                    exception,
                    "Request {Method} {Path} failed",
                    context.Request.Method,
                    context.Request.Path);
                throw;
            }
            finally
            {
                diagnostics.RecordRequest(context, Stopwatch.GetElapsedTime(started));
            }
        });

        app.Use((context, next) =>
        {
            var gateway = context.RequestServices.GetRequiredService<OperationGateway>();
            return gateway.InstrumentAsync(context, next);
        });

        app.UseMiddleware<NuGetAuthenticationMiddleware>();
    }

    public sealed record PackageContentRequest(string? Content);

}
