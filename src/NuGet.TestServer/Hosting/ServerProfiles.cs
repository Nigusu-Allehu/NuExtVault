using System.Collections.Immutable;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.Hosting;

internal static class BuiltInExtensionIds
{
    public const string Protocol = "builtin.protocol";
    public const string ServiceIndex = "builtin.service-index";
    public const string Publication = "builtin.publication";
    public const string Vulnerabilities = "builtin.vulnerabilities";
    public const string TestControl = "builtin.test-control";
    public const string DurableStorage = "builtin.durable-storage";
    public const string Operations = "builtin.operations";
    public const string SupplyChain = "builtin.supply-chain";
}

internal static class BuiltInCapabilityNames
{
    public const string PackagesIdentityRead = "packages.identity.read";
    public const string PackagesMetadataRead = "packages.metadata.read";
    public const string PackagesMetadataWrite = "packages.metadata.write";
    public const string PackagesContentRead = "packages.content.read";
    public const string PackagesContentWrite = "packages.content.write-staged";
    public const string PackagesPublish = "packages.publish";
    public const string PackagesUnlist = "packages.unlist";
    public const string PackagesRelist = "packages.relist";
    public const string PackagesDelete = "packages.delete";
    public const string ModerationRead = "moderation.read";
    public const string ModerationDecide = "moderation.decide";
    public const string VulnerabilityStateRead = "extension-state.vulnerabilities.read";
    public const string ExtensionStateRead = "extension-state.read";
    public const string ExtensionStateWrite = "extension-state.write";
    public const string EventsPublish = "events.publish";
    public const string BackupContribute = "backup.contribute";
    public const string BackupInvoke = "operations.backup.invoke";
    public const string RestoreInvoke = "operations.restore.invoke";
    public const string OperationsQuery = "operations.query";
    public const string ControlFaultsInject = "control.faults.inject";
    public const string ControlRequestsRead = "control.requests.read";
    public const string ControlPackagesManage = "control.packages.manage";
    public const string ControlInstrumentationManage = "control.instrumentation.manage";
    public const string DurableStorage = "storage.durable";
    public const string OutboundHttp = "network.outbound-http";
    public const string SecretsResolveReference = "secrets.resolve-reference";
    public const string SidecarExecution = "extensions.sidecar-execution";

    /// <summary>
    /// The narrow, read-only host clock any separately compiled module may request.
    /// The canonical name lives in the extension abstractions.
    /// </summary>
    public const string HostClockRead = KernelCapabilityNames.HostClockRead;
}

internal sealed record CapabilityGrant(string Name);

internal enum ServerProfileKind
{
    Embedded,
    Standard,
    Production
}

internal sealed record ServerProfile(
    string Name,
    ServerProfileKind Kind,
    ImmutableArray<ExtensionSelection> Extensions,
    ImmutableArray<CapabilityGrant> Grants);

internal static class ServerProfiles
{
    private static readonly ExtensionSelection Protocol = Extension(
        BuiltInExtensionIds.Protocol,
        Required(BuiltInCapabilityNames.PackagesIdentityRead),
        Required(BuiltInCapabilityNames.PackagesMetadataRead),
        Required(BuiltInCapabilityNames.PackagesContentRead),
        Required(BuiltInCapabilityNames.VulnerabilityStateRead));
    private static readonly ExtensionSelection ServiceIndex =
        Extension(BuiltInExtensionIds.ServiceIndex);
    private static readonly ExtensionSelection Publication = Extension(
        BuiltInExtensionIds.Publication,
        Required(BuiltInCapabilityNames.PackagesMetadataRead),
        Required(BuiltInCapabilityNames.PackagesContentWrite),
        Required(BuiltInCapabilityNames.PackagesPublish),
        Required(BuiltInCapabilityNames.PackagesUnlist),
        Required(BuiltInCapabilityNames.PackagesRelist),
        Required(BuiltInCapabilityNames.PackagesDelete),
        Required(BuiltInCapabilityNames.EventsPublish));
    private static readonly ExtensionSelection EmbeddedVulnerabilities = Extension(
        BuiltInExtensionIds.Vulnerabilities,
        Required(BuiltInCapabilityNames.VulnerabilityStateRead));
    private static readonly ExtensionSelection DurableVulnerabilities = Extension(
        BuiltInExtensionIds.Vulnerabilities,
        Required(BuiltInCapabilityNames.VulnerabilityStateRead),
        Required(BuiltInCapabilityNames.ExtensionStateRead),
        Required(BuiltInCapabilityNames.ExtensionStateWrite),
        Required(BuiltInCapabilityNames.OutboundHttp));
    private static readonly ExtensionSelection TestControl = Extension(
        BuiltInExtensionIds.TestControl,
        Required(BuiltInCapabilityNames.ControlPackagesManage),
        Required(BuiltInCapabilityNames.ControlInstrumentationManage));
    private static readonly ExtensionSelection DurableStorage = Extension(
        BuiltInExtensionIds.DurableStorage,
        Required(BuiltInCapabilityNames.DurableStorage));
    private static readonly ExtensionSelection Operations = Extension(
        BuiltInExtensionIds.Operations,
        Required(BuiltInCapabilityNames.OperationsQuery),
        Required(BuiltInCapabilityNames.BackupInvoke),
        Required(BuiltInCapabilityNames.RestoreInvoke));
    private static readonly ExtensionSelection SupplyChain = Extension(
        BuiltInExtensionIds.SupplyChain,
        Required(BuiltInCapabilityNames.ModerationRead),
        Required(BuiltInCapabilityNames.ModerationDecide));
    public static ServerProfile Embedded { get; } = new(
        "embedded",
        ServerProfileKind.Embedded,
        [
            Protocol,
            ServiceIndex,
            Publication,
            EmbeddedVulnerabilities,
            TestControl,
            Operations,
            SupplyChain
        ],
        Grants(
            BuiltInCapabilityNames.PackagesIdentityRead,
            BuiltInCapabilityNames.PackagesMetadataRead,
            BuiltInCapabilityNames.PackagesMetadataWrite,
            BuiltInCapabilityNames.PackagesContentRead,
            BuiltInCapabilityNames.PackagesContentWrite,
            BuiltInCapabilityNames.PackagesPublish,
            BuiltInCapabilityNames.PackagesUnlist,
            BuiltInCapabilityNames.PackagesRelist,
            BuiltInCapabilityNames.PackagesDelete,
            BuiltInCapabilityNames.ModerationRead,
            BuiltInCapabilityNames.ModerationDecide,
            BuiltInCapabilityNames.VulnerabilityStateRead,
            BuiltInCapabilityNames.EventsPublish,
            BuiltInCapabilityNames.BackupInvoke,
            BuiltInCapabilityNames.RestoreInvoke,
            BuiltInCapabilityNames.OperationsQuery,
            BuiltInCapabilityNames.ControlPackagesManage,
            BuiltInCapabilityNames.ControlInstrumentationManage));

    public static ServerProfile Standard { get; } = new(
        "standard",
        ServerProfileKind.Standard,
        [
            Protocol,
            ServiceIndex,
            Publication,
            DurableVulnerabilities,
            TestControl,
            DurableStorage,
            Operations,
            SupplyChain
        ],
        Grants(
            BuiltInCapabilityNames.PackagesIdentityRead,
            BuiltInCapabilityNames.PackagesMetadataRead,
            BuiltInCapabilityNames.PackagesMetadataWrite,
            BuiltInCapabilityNames.PackagesContentRead,
            BuiltInCapabilityNames.PackagesContentWrite,
            BuiltInCapabilityNames.PackagesPublish,
            BuiltInCapabilityNames.PackagesUnlist,
            BuiltInCapabilityNames.PackagesRelist,
            BuiltInCapabilityNames.PackagesDelete,
            BuiltInCapabilityNames.ModerationRead,
            BuiltInCapabilityNames.ModerationDecide,
            BuiltInCapabilityNames.VulnerabilityStateRead,
            BuiltInCapabilityNames.EventsPublish,
            BuiltInCapabilityNames.BackupContribute,
            BuiltInCapabilityNames.BackupInvoke,
            BuiltInCapabilityNames.RestoreInvoke,
            BuiltInCapabilityNames.OperationsQuery,
            BuiltInCapabilityNames.ControlPackagesManage,
            BuiltInCapabilityNames.ControlInstrumentationManage,
            BuiltInCapabilityNames.DurableStorage,
            BuiltInCapabilityNames.ExtensionStateRead,
            BuiltInCapabilityNames.ExtensionStateWrite,
            BuiltInCapabilityNames.OutboundHttp));

    public static ServerProfile Production { get; } = new(
        "production",
        ServerProfileKind.Production,
        [
            Protocol,
            ServiceIndex,
            Publication,
            DurableVulnerabilities,
            DurableStorage,
            Operations,
            SupplyChain
        ],
        Grants(
            BuiltInCapabilityNames.PackagesIdentityRead,
            BuiltInCapabilityNames.PackagesMetadataRead,
            BuiltInCapabilityNames.PackagesContentRead,
            BuiltInCapabilityNames.PackagesContentWrite,
            BuiltInCapabilityNames.PackagesPublish,
            BuiltInCapabilityNames.PackagesUnlist,
            BuiltInCapabilityNames.PackagesRelist,
            BuiltInCapabilityNames.PackagesDelete,
            BuiltInCapabilityNames.ModerationRead,
            BuiltInCapabilityNames.ModerationDecide,
            BuiltInCapabilityNames.VulnerabilityStateRead,
            BuiltInCapabilityNames.EventsPublish,
            BuiltInCapabilityNames.BackupContribute,
            BuiltInCapabilityNames.BackupInvoke,
            BuiltInCapabilityNames.RestoreInvoke,
            BuiltInCapabilityNames.OperationsQuery,
            BuiltInCapabilityNames.DurableStorage,
            BuiltInCapabilityNames.ExtensionStateRead,
            BuiltInCapabilityNames.ExtensionStateWrite,
            BuiltInCapabilityNames.OutboundHttp));

    private static ExtensionSelection Extension(
        string id,
        params CapabilityRequest[] requests) =>
        new(id, [.. requests]);

    private static CapabilityRequest Required(string name) => new(name, IsRequired: true);

    private static ImmutableArray<CapabilityGrant> Grants(params string[] names) =>
        [.. names.Select(name => new CapabilityGrant(name))];
}

internal sealed record ServerComposition(
    ServerProfile Profile,
    ResolvedExtensionGraph ExtensionGraph,
    ServerHostingOptions Hosting,
    string? StorageDirectory,
    AuthenticationConfiguration Authentication,
    VulnerabilitySnapshotProvider Vulnerabilities,
    RuntimeStateConfiguration RuntimeState,
    PackageTransferLimits PackageLimits,
    int MaximumAuthenticationFailures,
    SupplyChainOptions? SupplyChain,
    IPackagePolicyScanner? PackageScanner,
    TemporaryStorageLease? StorageLease,
    bool EnableVulnerabilityPersistence,
    ImmutableArray<IExtensionModule> Modules)
{
    /// <summary>
    /// Identifies this host instance. Kernel content handles, registries, routes, and
    /// diagnostics are scoped to it.
    /// </summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// True when the host authenticates production identities. Routes and access
    /// policies that require a production identity resolve against this flag.
    /// </summary>
    public bool HasProductionIdentity =>
        Authentication.Profile == AuthenticationProfile.Production;

    public static ServerComposition Create(
        ServerProfile profile,
        string? url = null,
        string? storageDirectory = null,
        AuthenticationConfiguration? authentication = null,
        VulnerabilitySnapshotProvider? vulnerabilities = null,
        RuntimeStateConfiguration? runtimeState = null,
        PackageTransferLimits? packageLimits = null,
        TrustedProxyOptions? trustedProxies = null,
        int maximumAuthenticationFailures = 5,
        SupplyChainOptions? supplyChain = null,
        IPackagePolicyScanner? packageScanner = null,
        TemporaryStorageLease? storageLease = null,
        bool enableVulnerabilityPersistence = false,
        ImmutableArray<IExtensionModule> modules = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        authentication ??= AuthenticationConfiguration.Anonymous;
        vulnerabilities ??= new VulnerabilitySnapshotProvider(EmbeddedVulnerabilitySnapshot.Load());
        runtimeState ??= new RuntimeStateConfiguration();
        packageLimits = (packageLimits ?? PackageTransferLimits.Default).Validate();
        modules = ExtensionModules.Validate(modules.IsDefault ? [] : modules);

        var catalog = modules.IsEmpty
            ? BuiltInExtensionCatalog.Instance
            : BuiltInExtensionCatalog.CreateWith(modules);
        var extensionGraph = catalog.Resolve(
            profile,
            authentication.Profile == AuthenticationProfile.Production,
            ExtensionModules.CreateContractIndex(modules));
        ValidateProfile(profile, storageDirectory, authentication, supplyChain);
        var mode = profile.Kind == ServerProfileKind.Production
            ? ServerMode.Production
            : ServerMode.Test;
        var hosting = ServerHostingOptions.Create(
            mode,
            url ?? "http://127.0.0.1:0",
            authentication,
            trustedProxies);

        return new ServerComposition(
            profile,
            extensionGraph,
            hosting,
            storageDirectory,
            authentication,
            vulnerabilities,
            runtimeState,
            packageLimits,
            maximumAuthenticationFailures,
            supplyChain,
            packageScanner,
            storageLease,
            enableVulnerabilityPersistence,
            modules);
    }

    public static ServerComposition CreateProductionWithTemporaryStorage(
        string? url = null,
        AuthenticationConfiguration? authentication = null,
        VulnerabilitySnapshotProvider? vulnerabilities = null,
        RuntimeStateConfiguration? runtimeState = null,
        PackageTransferLimits? packageLimits = null,
        TrustedProxyOptions? trustedProxies = null,
        int maximumAuthenticationFailures = 5,
        SupplyChainOptions? supplyChain = null,
        IPackagePolicyScanner? packageScanner = null,
        bool enableVulnerabilityPersistence = false,
        ImmutableArray<IExtensionModule> modules = default)
    {
        var lease = TemporaryStorageLease.Create();
        try
        {
            return Create(
                ServerProfiles.Production,
                url,
                lease.Path,
                authentication,
                vulnerabilities,
                runtimeState,
                packageLimits,
                trustedProxies,
                maximumAuthenticationFailures,
                supplyChain,
                packageScanner,
                lease,
                enableVulnerabilityPersistence,
                modules);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void ValidateProfile(
        ServerProfile profile,
        string? storageDirectory,
        AuthenticationConfiguration authentication,
        SupplyChainOptions? supplyChain)
    {
        if (profile.Kind != ServerProfileKind.Production)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(storageDirectory))
        {
            throw new ServerHostingConfigurationException(
                "Production profile requires durable storage.");
        }

        if (authentication.Profile == AuthenticationProfile.Anonymous)
        {
            throw new ServerHostingConfigurationException(
                "Production profile requires authentication and configured security.");
        }

        RequireExtension(profile, BuiltInExtensionIds.Operations, "operations");
        RequireExtension(profile, BuiltInExtensionIds.SupplyChain, "supply-chain policy");
        if (supplyChain is null)
        {
            throw new ServerHostingConfigurationException(
                "Production profile requires a supply-chain policy.");
        }
    }

    private static void RequireExtension(ServerProfile profile, string id, string description)
    {
        if (!profile.Extensions.Any(extension => extension.Id == id))
        {
            throw new ServerHostingConfigurationException(
                $"Production profile requires {description}.");
        }
    }
}

internal sealed class TemporaryStorageLease : IDisposable
{
    private int _disposed;

    private TemporaryStorageLease(string path) => Path = path;

    public string Path { get; }

    public static TemporaryStorageLease Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "NuGet.TestServer",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TemporaryStorageLease(path);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
