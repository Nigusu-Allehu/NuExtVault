using System.Collections.Immutable;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.Hosting;

internal static class BuiltInExtensionIds
{
    public const string Protocol = "builtin.protocol";
    public const string Publication = "builtin.publication";
    public const string Vulnerabilities = "builtin.vulnerabilities";
    public const string TestControl = "builtin.test-control";
    public const string DurableStorage = "builtin.durable-storage";
    public const string Operations = "builtin.operations";
    public const string SupplyChain = "builtin.supply-chain";
    public const string VulnerabilityRefresh = "builtin.vulnerability-refresh";
}

internal static class BuiltInCapabilityNames
{
    public const string PackagesRead = "packages.read";
    public const string PackagesWrite = "packages.write";
    public const string DurableStorage = "storage.durable";
    public const string Operations = "operations.execute";
    public const string SupplyChainPolicy = "packages.supply-chain-policy";
    public const string TestInstrumentation = "test.instrumentation";
    public const string OutboundHttp = "network.outbound-http";
    public const string SidecarExecution = "extensions.sidecar-execution";
}

internal sealed record CapabilityRequest(string Name, bool IsRequired);

internal sealed record CapabilityGrant(string Name);

internal sealed record ExtensionSelection(
    string Id,
    ImmutableArray<CapabilityRequest> RequestedCapabilities);

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
        Required(BuiltInCapabilityNames.PackagesRead));
    private static readonly ExtensionSelection Publication = Extension(
        BuiltInExtensionIds.Publication,
        Required(BuiltInCapabilityNames.PackagesWrite));
    private static readonly ExtensionSelection Vulnerabilities =
        Extension(BuiltInExtensionIds.Vulnerabilities);
    private static readonly ExtensionSelection TestControl = Extension(
        BuiltInExtensionIds.TestControl,
        Required(BuiltInCapabilityNames.TestInstrumentation));
    private static readonly ExtensionSelection DurableStorage = Extension(
        BuiltInExtensionIds.DurableStorage,
        Required(BuiltInCapabilityNames.DurableStorage));
    private static readonly ExtensionSelection Operations = Extension(
        BuiltInExtensionIds.Operations,
        Required(BuiltInCapabilityNames.Operations));
    private static readonly ExtensionSelection SupplyChain = Extension(
        BuiltInExtensionIds.SupplyChain,
        Required(BuiltInCapabilityNames.SupplyChainPolicy));
    private static readonly ExtensionSelection VulnerabilityRefresh = Extension(
        BuiltInExtensionIds.VulnerabilityRefresh,
        Required(BuiltInCapabilityNames.OutboundHttp));

    public static ServerProfile Embedded { get; } = new(
        "embedded",
        ServerProfileKind.Embedded,
        [Protocol, Publication, Vulnerabilities, TestControl, Operations, SupplyChain],
        Grants(
            BuiltInCapabilityNames.PackagesRead,
            BuiltInCapabilityNames.PackagesWrite,
            BuiltInCapabilityNames.Operations,
            BuiltInCapabilityNames.SupplyChainPolicy,
            BuiltInCapabilityNames.TestInstrumentation));

    public static ServerProfile Standard { get; } = new(
        "standard",
        ServerProfileKind.Standard,
        [
            Protocol,
            Publication,
            Vulnerabilities,
            TestControl,
            DurableStorage,
            Operations,
            SupplyChain,
            VulnerabilityRefresh
        ],
        Grants(
            BuiltInCapabilityNames.PackagesRead,
            BuiltInCapabilityNames.PackagesWrite,
            BuiltInCapabilityNames.DurableStorage,
            BuiltInCapabilityNames.Operations,
            BuiltInCapabilityNames.SupplyChainPolicy,
            BuiltInCapabilityNames.TestInstrumentation,
            BuiltInCapabilityNames.OutboundHttp));

    public static ServerProfile Production { get; } = new(
        "production",
        ServerProfileKind.Production,
        [
            Protocol,
            Publication,
            Vulnerabilities,
            DurableStorage,
            Operations,
            SupplyChain,
            VulnerabilityRefresh
        ],
        Grants(
            BuiltInCapabilityNames.PackagesRead,
            BuiltInCapabilityNames.PackagesWrite,
            BuiltInCapabilityNames.DurableStorage,
            BuiltInCapabilityNames.Operations,
            BuiltInCapabilityNames.SupplyChainPolicy,
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
    ServerHostingOptions Hosting,
    string? StorageDirectory,
    AuthenticationConfiguration Authentication,
    VulnerabilitySnapshotProvider Vulnerabilities,
    RuntimeStateConfiguration RuntimeState,
    PackageTransferLimits PackageLimits,
    int MaximumAuthenticationFailures,
    SupplyChainOptions? SupplyChain,
    IPackagePolicyScanner? PackageScanner,
    TemporaryStorageLease? StorageLease)
{
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
        TemporaryStorageLease? storageLease = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        authentication ??= AuthenticationConfiguration.Anonymous;
        vulnerabilities ??= new VulnerabilitySnapshotProvider(EmbeddedVulnerabilitySnapshot.Load());
        runtimeState ??= new RuntimeStateConfiguration();
        packageLimits = (packageLimits ?? PackageTransferLimits.Default).Validate();

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
            hosting,
            storageDirectory,
            authentication,
            vulnerabilities,
            runtimeState,
            packageLimits,
            maximumAuthenticationFailures,
            supplyChain,
            packageScanner,
            storageLease);
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
        IPackagePolicyScanner? packageScanner = null)
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
                lease);
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
        var grants = profile.Grants.Select(grant => grant.Name).ToHashSet(StringComparer.Ordinal);
        var missingGrant = profile.Extensions
            .SelectMany(extension => extension.RequestedCapabilities)
            .FirstOrDefault(request => request.IsRequired && !grants.Contains(request.Name));
        if (missingGrant is not null)
        {
            throw new ServerHostingConfigurationException(
                $"Profile '{profile.Name}' does not grant required capability '{missingGrant.Name}'.");
        }

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
