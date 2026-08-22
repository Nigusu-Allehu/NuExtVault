using NuGet.TestServer.Authentication;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;
using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Cli;

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
        ImmutableArray<ConformanceTrustRoot> extensionTrustRoots = default)
    {
        return ServerComposition.Create(
            production ? ServerProfiles.Production : ServerProfiles.Standard,
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
