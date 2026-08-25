using NuExtVault.Authentication;
using NuExtVault.Hosting;
using NuExtVault.Packages;
using System.Collections.Immutable;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Cli;

internal static class CliServerProfileFactory
{
    public static ServerComposition Create(
        bool production,
        string url,
        string storageDirectory,
        AuthenticationConfiguration authentication,
        PackageTransferLimits packageLimits,
        TrustedProxyOptions? trustedProxies,
        ImmutableArray<string> extensionRoots = default,
        ImmutableArray<ConformanceTrustRoot> extensionTrustRoots = default,
        ImmutableArray<string> extensionGrants = default,
        ImmutableArray<OwnerIdentityMigrationAuthorization>
            ownerIdentityMigrationAuthorizations = default)
    {
        var profile = production ? ServerProfiles.Production : ServerProfiles.Standard;
        if (!extensionGrants.IsDefaultOrEmpty)
        {
            // Capabilities stay denied by default: an administrator grants exactly the
            // ones an installed extension may use, and an ungranted required capability
            // still fails startup.
            var granted = profile.Grants
                .Select(grant => grant.Name)
                .ToHashSet(StringComparer.Ordinal);
            profile = profile with
            {
                Grants =
                [
                    .. profile.Grants,
                    .. extensionGrants
                        .Where(granted.Add)
                        .Select(name => new CapabilityGrant(name))
                ]
            };
        }
        if (!ownerIdentityMigrationAuthorizations.IsDefaultOrEmpty)
        {
            profile = profile with
            {
                OwnerIdentityMigrationAuthorizations = ownerIdentityMigrationAuthorizations
            };
        }

        return ServerComposition.Create(
            profile,
            url,
            storageDirectory,
            authentication,
            packageLimits: packageLimits,
            trustedProxies: trustedProxies,
            supplyChain: new SupplyChainOptions(),
            enableVulnerabilityPersistence: true,
            externalExtensions: extensionRoots.IsDefaultOrEmpty
                ? ExternalExtensionConfiguration.Disabled
                : new ExternalExtensionConfiguration(
                    extensionRoots,
                    extensionTrustRoots.IsDefault ? [] : extensionTrustRoots,
                    TimeProvider.System));
    }
}
