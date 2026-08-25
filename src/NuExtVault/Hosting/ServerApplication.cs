using System.Diagnostics;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http.Features;
using NuExtVault.Authentication;
using NuExtVault.Extensions;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Extensions.Vulnerabilities;
using NuExtVault.Kernel;
using NuExtVault.Kernel.Capabilities;
using NuExtVault.Kernel.Routing;
using NuExtVault.Operations;
using NuExtVault.Packages;
using NuExtVault.Vulnerabilities;

namespace NuExtVault.Hosting;

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
        FileStream? storageRootLease = null;
        try
        {
            if (composition.StorageDirectory is not null)
            {
                Directory.CreateDirectory(composition.StorageDirectory);
                storageRootLease =
                    DurablePackageStore.AcquireRootLease(composition.StorageDirectory);
                DurableOwnerIdentityMigrator.Migrate(
                    composition.StorageDirectory,
                    CreateOwnerIdentityMigrations(composition));
            }

            return Build(builder, composition, storageRootLease);
        }
        catch
        {
            storageRootLease?.Dispose();
            composition.StorageLease?.Dispose();
            composition.ExternalExtensions.Dispose();
            throw;
        }
    }

    private static WebApplication Build(
        WebApplicationBuilder builder,
        ServerComposition composition,
        FileStream? storageRootLease)
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
        builder.Services.AddSingleton<IHostedService>(composition.ExternalExtensions);
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
                : new DurablePackageStore(
                    storageDirectory,
                    packageLimits,
                    storageRootLease ??
                    throw new InvalidOperationException("The storage root lease is unavailable.")));
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
        builder.Services.AddSingleton(_ => CreateExtensionStateStore(storageDirectory, composition));
        builder.Services.AddSingleton(_ => new StagedContentStore(
            storageDirectory,
            composition.InstanceId,
            quotas: null,
            TimeProvider.System));
        builder.Services.AddHostedService<StagedContentReclaimer>();
        builder.Services.AddSingleton(_ => new PublicationJournal(storageDirectory));
        builder.Services.AddSingleton(provider => new StagedPublicationCoordinator(
            composition.InstanceId,
            provider.GetRequiredService<StagedContentStore>(),
            provider.GetRequiredService<PublicationJournal>(),
            provider.GetRequiredService<TransactionalStateStore>(),
            provider.GetRequiredService<IPackageStore>(),
            () => provider.GetRequiredService<PackageSupplyChainService>(),
            packageLimits,
            provider.GetRequiredService<ServerDiagnostics>(),
            provider.GetRequiredService<TimeProvider>()));
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
                officialExtensions.VulnerabilityCatalog,
                provider.GetRequiredService<TransactionalStateStore>(),
                officialExtensions,
                provider.GetRequiredService<KernelOutboundHttpClient>(),
                packageLimits,
                provider.GetRequiredService<TimeProvider>())
            {
                StagedPublication = () =>
                    provider.GetRequiredService<StagedPublicationCoordinator>()
            }));
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

            // Interrupted staged publications are finished or failed closed before the
            // host serves a request, and expired staged leases are reclaimed.
            var stagedPublication = app.Services
                .GetRequiredService<StagedPublicationCoordinator>();
            stagedPublication.RecoverAsync(CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            stagedPublication.Content.ReclaimExpiredAsync(CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();

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

    private static TransactionalStateStore CreateExtensionStateStore(
        string? storageDirectory,
        ServerComposition composition)
    {
        var participants = CreateStateParticipants(composition);
        if (storageDirectory is null)
        {
            return new TransactionalStateStore(root: null, participants);
        }

        var legacyVulnerabilities = new LegacyStateFileSetRegistration(
            Path.Combine(storageDirectory, "vulnerabilities"),
            MaximumFileBytes: 32L * 1024 * 1024,
            MaximumTotalBytes: 512L * 1024 * 1024,
            MaximumFileCount: 64).Validate();
        return new TransactionalStateStore(
            Path.Combine(storageDirectory, "extension-state"),
            participants,
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

    /// <summary>
    /// Registers every state schema the active extensions declare in their manifests,
    /// on top of the built-in participants. Registration is generic: the kernel reads
    /// the declaration, never an extension name.
    /// </summary>
    internal static ImmutableArray<StateParticipantDescriptor> CreateStateParticipants(
        ServerComposition composition)
    {
        var active = composition.ExtensionGraph.Extensions
            .Select(extension => extension.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declared = composition.Modules
            .Select(module => module.Contribution.Manifest)
            .Where(manifest =>
                manifest.State is not null &&
                active.Contains(manifest.Identity.Id) &&
                !KernelStateParticipants.BuiltIn.Any(participant => string.Equals(
                    participant.ExtensionId,
                    manifest.Identity.Id,
                    StringComparison.Ordinal)))
            .DistinctBy(manifest => manifest.Identity.Id, StringComparer.Ordinal)
            .Select(manifest => new StateParticipantDescriptor(
                manifest.Identity.Id,
                manifest.Identity.Version,
                manifest.State!.SchemaName,
                manifest.State.SchemaVersion,
                manifest.State.Required).Validate());
        return [.. KernelStateParticipants.BuiltIn, .. declared];
    }

    internal static ImmutableArray<OwnerIdentityMigration> CreateOwnerIdentityMigrations(
        ServerComposition composition) =>
        OwnerIdentityMigrationResolver.Resolve(
            composition.Modules,
            composition.ExtensionGraph.Extensions.Select(extension => extension.Id),
            composition.Profile.OwnerIdentityMigrationAuthorizations.IsDefault
                ? []
                : composition.Profile.OwnerIdentityMigrationAuthorizations);

    private static void MapMiddleware(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var diagnostics = context.RequestServices.GetRequiredService<ServerDiagnostics>();
            var logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("NuExtVault.Requests");
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
