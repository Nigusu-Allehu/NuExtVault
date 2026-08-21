using System.Diagnostics;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http.Features;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Extensions.Vulnerabilities;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Kernel.Routing;
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
        var officialExtensions = OfficialExtensionComposition.Create(composition);
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
        var supplyChainOptions = (composition.SupplyChain ?? new SupplyChainOptions()).Validate();
        var packageScanner = composition.PackageScanner ?? new SafePackagePolicyScanner();
        builder.Services.AddSingleton(supplyChainOptions);
        builder.Services.AddSingleton<IPackagePolicyScanner>(packageScanner);
        builder.Services.AddSingleton<PolicyPackageHandleRegistry>();
        builder.Services.AddSingleton<PackagePolicyInspectionService>();
        builder.Services.AddSingleton(provider => new PackageSupplyChainService(
            provider.GetRequiredService<IPackageStore>(),
            storageDirectory,
            supplyChainOptions,
            packageScanner,
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<SupplyChainPolicyEvaluator>(),
            provider.GetRequiredService<PolicyPackageHandleRegistry>()));
        builder.Services.AddSingleton(new StorageHealth(storageDirectory));
        builder.Services.AddSingleton<ServerDiagnostics>();
        builder.Services.AddSingleton<KernelRequestInstrumentation>();
        officialExtensions.AddServices(builder.Services);
        builder.Services.AddSingleton<KernelOutboundHttpClient>();
        builder.Services.AddSingleton<CapabilityAuditLog>();
        builder.Services.AddSingleton(new CapabilityLimits(
            MaximumConcurrentCalls: 64,
            MaximumStreamBytes: Math.Max(
                packageLimits.MaxRequestBodyBytes,
                packageLimits.MaxPackageBytes)));
        builder.Services.AddSingleton(_ => CreateExtensionStateStore(storageDirectory));
        builder.Services.AddSingleton(provider => new CapabilityBroker(
            composition.InstanceId,
            composition.ExtensionGraph,
            provider.GetRequiredService<CapabilityAuditLog>(),
            provider.GetRequiredService<CapabilityLimits>(),
            new CapabilityServices(
                provider.GetRequiredService<IPackageStore>(),
                provider.GetRequiredService<IPackageCandidateStore>(),
                provider.GetRequiredService<PackageVisibilityPolicy>(),
                () => provider.GetRequiredService<PackageSupplyChainService>(),
                provider.GetRequiredService<PackagePolicyInspectionService>(),
                provider.GetRequiredService<KernelRequestInstrumentation>(),
                provider.GetRequiredService<StorageHealth>(),
                provider.GetRequiredService<ServerDiagnostics>(),
                composition.Hosting,
                composition.StorageDirectory,
                officialExtensions.VulnerabilitySnapshots,
                provider.GetRequiredService<TransactionalStateStore>(),
                officialExtensions,
                provider.GetRequiredService<KernelOutboundHttpClient>(),
                packageLimits,
                provider.GetRequiredService<TimeProvider>())));
        builder.Services.AddSingleton(provider => PolicyParticipantRegistry.Create(
            composition.ExtensionGraph,
            composition.Modules,
            provider.GetRequiredService<CapabilityBroker>()));
        builder.Services.AddSingleton(provider =>
        {
            var requirements = composition.Profile.PolicyRequirements
                .ToDictionary(
                   requirement => requirement.PolicyPoint,
                   requirement => new PolicyAggregationRequirement(
                       PolicyAggregationKind.AllMustAllow,
                       requirement.RequiredAuthoritativeParticipants,
                       requirement.MinimumAuthoritativeParticipants,
                       TimeSpan.FromSeconds(30)),
                   StringComparer.Ordinal);
            return new SupplyChainPolicyEvaluator(
                provider.GetRequiredService<PolicyParticipantRegistry>(),
                requirements);
        });
        builder.Services.AddSingleton(_ =>
            ServiceIndexResourceRegistry.Create(composition.ExtensionGraph));
        builder.Services.AddSingleton(provider => BuiltInOperationOwners.CreateRegistry(
            provider.GetRequiredService<CapabilityBroker>(),
            composition.ExtensionGraph,
            provider.GetRequiredService<ServiceIndexResourceRegistry>(),
            officialExtensions,
            packageLimits,
            composition.Modules));
        builder.Services.AddSingleton(_ => KernelRouteTable.Create(
            composition.ExtensionGraph,
            packageLimits,
            composition.HasProductionIdentity));
        builder.Services.AddSingleton(provider => new KernelUrlProjector(
            provider.GetRequiredService<KernelRouteTable>()));
        builder.Services.AddSingleton(provider => new OperationDispatcher(
            provider.GetRequiredService<OperationRegistry>(),
            provider.GetRequiredService<ServerDiagnostics>()));
        builder.Services.AddSingleton(provider => new OperationGateway(
            provider.GetRequiredService<OperationDispatcher>(),
            provider.GetRequiredService<ISecurityAuditSink>(),
            composition.InstanceId,
            provider.GetRequiredService<KernelRequestInstrumentation>(),
            provider.GetRequiredService<KernelUrlProjector>(),
            composition.Hosting.Transport));

        var app = builder.Build();
        KernelRouteTable routes;
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

            // The route table is generated from validated descriptors and frozen here,
            // before any listener exists.
            routes = app.Services.GetRequiredService<KernelRouteTable>();
        }
        catch
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        MapMiddleware(app);
        KernelEndpointMapper.Map(app, routes);

        return app;
    }

    private static TransactionalStateStore CreateExtensionStateStore(string? storageDirectory)
    {
        if (storageDirectory is null)
        {
            return new TransactionalStateStore(root: null, KernelStateParticipants.BuiltIn);
        }

        var legacyVulnerabilities = new LegacyStateFileSetRegistration(
            Path.Combine(storageDirectory, "vulnerabilities"),
            MaximumFileBytes: 32L * 1024 * 1024,
            MaximumTotalBytes: 512L * 1024 * 1024,
            MaximumFileCount: 64).Validate();
        return new TransactionalStateStore(
            Path.Combine(storageDirectory, "extension-state"),
            KernelStateParticipants.BuiltIn,
            quotas: null,
            ImmutableDictionary<
                    string,
                    ImmutableDictionary<string, LegacyStateFileSetRegistration>>
                .Empty
                .Add(
                    BuiltInExtensionIds.Vulnerabilities,
                    ImmutableDictionary<string, LegacyStateFileSetRegistration>
                        .Empty
                        .Add(VulnerabilityExtension.LegacyStateName, legacyVulnerabilities)));
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
}
