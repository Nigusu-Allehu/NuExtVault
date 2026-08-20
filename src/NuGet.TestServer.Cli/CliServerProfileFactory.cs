using NuGet.TestServer.Authentication;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.Cli;

internal static class CliServerProfileFactory
{
    public static ServerComposition Create(
        bool production,
        string url,
        string storageDirectory,
        AuthenticationConfiguration authentication,
        VulnerabilitySnapshotProvider vulnerabilities,
        PackageTransferLimits packageLimits,
        TrustedProxyOptions? trustedProxies)
    {
        return ServerComposition.Create(
            production ? ServerProfiles.Production : ServerProfiles.Standard,
            url,
            storageDirectory,
            authentication,
            vulnerabilities,
            packageLimits: packageLimits,
            trustedProxies: trustedProxies,
            supplyChain: new SupplyChainOptions());
    }
}
