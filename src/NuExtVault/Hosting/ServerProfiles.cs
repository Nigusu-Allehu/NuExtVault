using System.Collections.Immutable;
using NuExtVault.Authentication;
using NuExtVault.Extensions;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Extensions.Official;
using NuExtVault.Extensions.Vulnerabilities;
using NuExtVault.Packages;
using NuExtVault.Extensions.SupplyChain;

namespace NuExtVault.Hosting;

internal static class ServerProfiles
{
    private static readonly ExtensionSelection Protocol = Extension(
        BuiltInExtensionIds.Protocol,
        Required(BuiltInCapabilityNames.PackagesIdentityRead),
        Required(BuiltInCapabilityNames.PackagesMetadataRead));
    private static readonly ExtensionSelection ServiceIndex =
        Extension(BuiltInExtensionIds.ServiceIndex);
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
    private static readonly ExtensionSelection SupplyChain = Extension(
        BuiltInExtensionIds.SupplyChain,
        Required(BuiltInCapabilityNames.ModerationRead),
        Required(BuiltInCapabilityNames.ModerationDecide));
    private static readonly ExtensionSelection SupplyChainPolicy = Extension(
        BuiltInExtensionIds.SupplyChainPolicy,
        Required(BuiltInCapabilityNames.SupplyChainSignatureInspect),
        Required(BuiltInCapabilityNames.SupplyChainPackageScan));
    private static readonly ImmutableArray<ProfilePolicyRequirement> SupplyChainRequirements =
    [
        new(
            SupplyChainPolicyPoints.Admission,
            [
                SupplyChainPolicyParticipantIds.Ownership,
                SupplyChainPolicyParticipantIds.Namespace,
                SupplyChainPolicyParticipantIds.Quota
            ],
            MinimumAuthoritativeParticipants: 3),
        new(
            SupplyChainPolicyPoints.Validation,
            [
                SupplyChainPolicyParticipantIds.Signature,
                SupplyChainPolicyParticipantIds.Scanner
            ],
            MinimumAuthoritativeParticipants: 2)
    ];
    public static ServerProfile Embedded { get; } = new(
        "embedded",
        ServerProfileKind.Embedded,
        [
            Protocol,
            ServiceIndex,
            EmbeddedVulnerabilities,
            TestControl,
            SupplyChain,
            SupplyChainPolicy,
            .. OfficialExtensionModules.Selections
        ],
        Grants(
            BuiltInCapabilityNames.PackagesIdentityRead,
            BuiltInCapabilityNames.PackagesMetadataRead,
            BuiltInCapabilityNames.PackagesMetadataWrite,
            BuiltInCapabilityNames.PackagesContentRead,
            BuiltInCapabilityNames.PackagesSymbolsRead,
            BuiltInCapabilityNames.PackagesSearchQuery,
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
            BuiltInCapabilityNames.ControlInstrumentationManage,
            BuiltInCapabilityNames.SupplyChainSignatureInspect,
            BuiltInCapabilityNames.SupplyChainPackageScan),
        SupplyChainRequirements);

    public static ServerProfile Standard { get; } = new(
        "standard",
        ServerProfileKind.Standard,
        [
            Protocol,
            ServiceIndex,
            DurableVulnerabilities,
            TestControl,
            DurableStorage,
            SupplyChain,
            SupplyChainPolicy,
            .. OfficialExtensionModules.Selections
        ],
        Grants(
            BuiltInCapabilityNames.PackagesIdentityRead,
            BuiltInCapabilityNames.PackagesMetadataRead,
            BuiltInCapabilityNames.PackagesMetadataWrite,
            BuiltInCapabilityNames.PackagesContentRead,
            BuiltInCapabilityNames.PackagesSymbolsRead,
            BuiltInCapabilityNames.PackagesSearchQuery,
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
            BuiltInCapabilityNames.OutboundHttp,
            BuiltInCapabilityNames.SupplyChainSignatureInspect,
            BuiltInCapabilityNames.SupplyChainPackageScan),
        SupplyChainRequirements);

    public static ServerProfile Production { get; } = new(
        "production",
        ServerProfileKind.Production,
        [
            Protocol,
            ServiceIndex,
            DurableVulnerabilities,
            DurableStorage,
            SupplyChain,
            SupplyChainPolicy,
            .. OfficialExtensionModules.Selections
        ],
        Grants(
            BuiltInCapabilityNames.PackagesIdentityRead,
            BuiltInCapabilityNames.PackagesMetadataRead,
            BuiltInCapabilityNames.PackagesContentRead,
            BuiltInCapabilityNames.PackagesSymbolsRead,
            BuiltInCapabilityNames.PackagesSearchQuery,
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
            BuiltInCapabilityNames.OutboundHttp,
            BuiltInCapabilityNames.SupplyChainSignatureInspect,
            BuiltInCapabilityNames.SupplyChainPackageScan),
        SupplyChainRequirements);

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
    ImmutableArray<IExtensionModule> Modules,
    ExternalExtensionRuntime ExternalExtensions)
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
        ImmutableArray<IExtensionModule> modules = default,
        ExternalExtensionConfiguration? externalExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        authentication ??= AuthenticationConfiguration.Anonymous;
        vulnerabilities ??= new VulnerabilitySnapshotProvider(EmbeddedVulnerabilitySnapshot.Load());
        runtimeState ??= new RuntimeStateConfiguration();
        packageLimits = (packageLimits ?? PackageTransferLimits.Default).Validate();
        var externalRuntime = ExternalExtensionPackageLoader.Load(
            externalExtensions ?? ExternalExtensionConfiguration.Disabled);
        if (externalRuntime.Diagnostics.Results.Any(result => !result.Succeeded))
        {
            externalRuntime.Dispose();
            var failure = externalRuntime.Diagnostics.Results.First(result => !result.Succeeded);
            throw new ServerHostingConfigurationException(
                $"{failure.FailureCode}: {failure.RedactedMessage}");
        }
        try
        {
            modules = ExtensionModules.Validate(
                [
                    new SupplyChainExtension(),
                .. (modules.IsDefault ? [] : modules),
                .. externalRuntime.Modules
                ]);
            if (!externalRuntime.Modules.IsEmpty)
            {
                profile = profile with
                {
                    Extensions =
                    [
                        .. profile.Extensions,
                    .. externalRuntime.Modules.Select(module => module.Contribution.Selection)
                    ]
                };
            }

            var catalog = modules.IsEmpty
                ? BuiltInExtensionCatalog.Instance
                : BuiltInExtensionCatalog.CreateWith(modules);
            var extensionGraph = catalog.Resolve(
                profile,
                authentication.Profile == AuthenticationProfile.Production,
                ExtensionModules.CreateContractIndex(modules));
            ValidateProfile(profile, storageDirectory, authentication, supplyChain, modules);
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
                modules,
                externalRuntime);
        }
        catch
        {
            externalRuntime.Dispose();
            throw;
        }
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
        ImmutableArray<IExtensionModule> modules = default,
        ExternalExtensionConfiguration? externalExtensions = null)
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
                modules,
                externalExtensions);
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
        SupplyChainOptions? supplyChain,
        ImmutableArray<IExtensionModule> modules)
    {
        ValidatePolicyRequirements(profile, modules);
        if (profile.Kind != ServerProfileKind.Production) return;

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

    private static void ValidatePolicyRequirements(
        ServerProfile profile,
        ImmutableArray<IExtensionModule> modules)
    {
        var selected = profile.Extensions
            .Select(extension => extension.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var participants = modules
            .Where(module => selected.Contains(module.Contribution.Manifest.Id))
            .SelectMany(module => module.Contribution.PolicyParticipants)
            .ToArray();
        foreach (var requirement in profile.PolicyRequirements.IsDefault
                     ? []
                     : profile.PolicyRequirements)
        {
            var authoritative = participants
                .Where(participant =>
                    participant.IsAuthoritative &&
                    string.Equals(
                        participant.PolicyPoint,
                        requirement.PolicyPoint,
                        StringComparison.Ordinal))
                .Select(participant => participant.ParticipantId)
                .ToHashSet(StringComparer.Ordinal);
            var missing = requirement.RequiredAuthoritativeParticipants
                .Where(id => !authoritative.Contains(id))
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (authoritative.Count < requirement.MinimumAuthoritativeParticipants ||
                missing.Length > 0)
            {
                throw new ServerHostingConfigurationException(
                    $"catalog.missing-authoritative-policy-participant: Policy point " +
                    $"'{requirement.PolicyPoint}' requires at least " +
                    $"{requirement.MinimumAuthoritativeParticipants} authoritative participants. " +
                    $"Missing: {string.Join(", ", missing)}.");
            }
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
