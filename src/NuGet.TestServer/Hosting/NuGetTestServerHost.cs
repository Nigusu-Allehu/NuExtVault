using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Faults;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Requests;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.Hosting;

public sealed class NuGetTestServerHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private NuGetTestServerHost(
        WebApplication application,
        Uri baseUrl,
        ServerComposition composition)
    {
        _application = application;
        Composition = composition;
        BaseUrl = baseUrl;
        ServiceIndexUrl = new Uri(baseUrl, "/v3/index.json");
        ControlUrl = new Uri(baseUrl, "/__test");
        HttpClient = new HttpClient { BaseAddress = baseUrl };
        Packages = new PackageControlClient(
            application.Services.GetRequiredService<IPackageStore>(),
            application.Services.GetRequiredService<PackageSupplyChainService>());
        Faults = new FaultControlClient(
            application.Services.GetRequiredService<FaultRuleStore>());
        Requests = new RequestControlClient(
            application.Services.GetRequiredService<RequestRecorder>());
    }

    public Uri BaseUrl { get; }
    public Uri ServiceIndexUrl { get; }
    public Uri ControlUrl { get; }
    public int Port => BaseUrl.Port;
    public HttpClient HttpClient { get; }
    public PackageControlClient Packages { get; }
    public FaultControlClient Faults { get; }
    public RequestControlClient Requests { get; }
    public IReadOnlyList<SecurityAuditEvent> SecurityAudits =>
        _application.Services.GetRequiredService<ISecurityAuditSink>().GetAll();
    internal ServerComposition Composition { get; }

    public static async Task<NuGetTestServerHost> StartAsync(
        CancellationToken token = default)
    {
        return await StartAsync(AuthenticationConfiguration.Anonymous, token);
    }

    public static async Task<NuGetTestServerHost> StartProductionAsync(
        ProductionSecurityConfiguration security,
        int maximumAuthenticationFailures = 5,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(security);
        var composition = ServerComposition.CreateProductionWithTemporaryStorage(
            authentication: AuthenticationConfiguration.CreateProduction(security),
            vulnerabilities: new VulnerabilitySnapshotProvider(
                EmbeddedVulnerabilitySnapshot.Load()),
            trustedProxies: new TrustedProxyOptions(["127.0.0.1"]),
            maximumAuthenticationFailures: maximumAuthenticationFailures,
            supplyChain: new SupplyChainOptions());
        return await StartCompositionAsync(composition, token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        RuntimeStateConfiguration runtimeState,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeState);
        return await StartAsync(
            AuthenticationConfiguration.Anonymous,
            EmbeddedVulnerabilitySnapshot.Load(),
            PackageTransferLimits.Default,
            runtimeState,
            token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        AuthenticationConfiguration authentication,
        CancellationToken token = default)
    {
        return await StartAsync(
            ServerMode.Test,
            authentication,
            EmbeddedVulnerabilitySnapshot.Load(),
            PackageTransferLimits.Default,
            new RuntimeStateConfiguration(),
            token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        PackageTransferLimits packageLimits,
        CancellationToken token = default)
    {
        return await StartAsync(
            ServerMode.Test,
            AuthenticationConfiguration.Anonymous,
            EmbeddedVulnerabilitySnapshot.Load(),
            packageLimits,
            new RuntimeStateConfiguration(),
            token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        SupplyChainOptions supplyChain,
        IPackagePolicyScanner? scanner = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(supplyChain);
        var composition = ServerComposition.Create(
            ServerProfiles.Embedded,
            authentication: AuthenticationConfiguration.Anonymous,
            vulnerabilities: new VulnerabilitySnapshotProvider(EmbeddedVulnerabilitySnapshot.Load()),
            packageLimits: PackageTransferLimits.Default,
            supplyChain: supplyChain,
            packageScanner: scanner);
        return await StartCompositionAsync(composition, token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        string storageDirectory,
        PackageTransferLimits packageLimits,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentNullException.ThrowIfNull(packageLimits);
        var composition = ServerComposition.Create(
            ServerProfiles.Embedded,
            storageDirectory: storageDirectory,
            authentication: AuthenticationConfiguration.Anonymous,
            vulnerabilities: new VulnerabilitySnapshotProvider(EmbeddedVulnerabilitySnapshot.Load()),
            packageLimits: packageLimits);
        return await StartCompositionAsync(composition, token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        VulnerabilitySnapshot vulnerabilities,
        CancellationToken token = default)
    {
        return await StartAsync(
            ServerMode.Test,
            AuthenticationConfiguration.Anonymous,
            vulnerabilities,
            PackageTransferLimits.Default,
            new RuntimeStateConfiguration(),
            token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        AuthenticationConfiguration authentication,
        VulnerabilitySnapshot vulnerabilities,
        CancellationToken token = default)
    {
        return await StartAsync(
            ServerMode.Test,
            authentication,
            vulnerabilities,
            PackageTransferLimits.Default,
            new RuntimeStateConfiguration(),
            token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        AuthenticationConfiguration authentication,
        VulnerabilitySnapshot vulnerabilities,
        PackageTransferLimits packageLimits,
        RuntimeStateConfiguration runtimeState,
        CancellationToken token = default)
    {
        return await StartAsync(
            ServerMode.Test,
            authentication,
            vulnerabilities,
            packageLimits,
            runtimeState,
            token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        AuthenticationConfiguration authentication,
        VulnerabilitySnapshot vulnerabilities,
        PackageTransferLimits packageLimits,
        CancellationToken token = default)
    {
        return await StartAsync(
            ServerMode.Test,
            authentication,
            vulnerabilities,
            packageLimits,
            new RuntimeStateConfiguration(),
            token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        ServerMode mode,
        AuthenticationConfiguration authentication,
        CancellationToken token = default)
    {
        return await StartAsync(mode, authentication, EmbeddedVulnerabilitySnapshot.Load(), token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        ServerMode mode,
        AuthenticationConfiguration authentication,
        string storageDirectory,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        var profile = mode == ServerMode.Production
            ? ServerProfiles.Production
            : ServerProfiles.Embedded;
        var composition = ServerComposition.Create(
            profile,
            storageDirectory: storageDirectory,
            authentication: authentication,
            supplyChain: mode == ServerMode.Production ? new SupplyChainOptions() : null);
        return await StartCompositionAsync(composition, token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        ServerMode mode,
        AuthenticationConfiguration authentication,
        VulnerabilitySnapshot vulnerabilities,
        CancellationToken token = default)
    {
        return await StartAsync(
            mode,
            authentication,
            vulnerabilities,
            PackageTransferLimits.Default,
            new RuntimeStateConfiguration(),
            token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        ServerMode mode,
        AuthenticationConfiguration authentication,
        VulnerabilitySnapshot vulnerabilities,
        PackageTransferLimits packageLimits,
        CancellationToken token = default)
    {
        return await StartAsync(
            mode,
            authentication,
            vulnerabilities,
            packageLimits,
            new RuntimeStateConfiguration(),
            token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        ServerMode mode,
        AuthenticationConfiguration authentication,
        VulnerabilitySnapshot vulnerabilities,
        PackageTransferLimits packageLimits,
        RuntimeStateConfiguration runtimeState,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(vulnerabilities);
        ArgumentNullException.ThrowIfNull(packageLimits);
        ArgumentNullException.ThrowIfNull(runtimeState);
        var profile = mode == ServerMode.Production
            ? ServerProfiles.Production
            : ServerProfiles.Embedded;
        var composition = mode == ServerMode.Production
            ? ServerComposition.CreateProductionWithTemporaryStorage(
                authentication: authentication,
                vulnerabilities: new VulnerabilitySnapshotProvider(vulnerabilities),
                runtimeState: runtimeState,
                packageLimits: packageLimits,
                supplyChain: new SupplyChainOptions())
            : ServerComposition.Create(
                profile,
                authentication: authentication,
                vulnerabilities: new VulnerabilitySnapshotProvider(vulnerabilities),
                runtimeState: runtimeState,
                packageLimits: packageLimits);
        return await StartCompositionAsync(composition, token);
    }

    private static async Task<NuGetTestServerHost> StartCompositionAsync(
        ServerComposition composition,
        CancellationToken token)
    {
        var application = ServerApplication.Build(composition);
        try
        {
            await application.StartAsync(token);
            var address = application.Services
                .GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?
                .Addresses.SingleOrDefault();
            if (address is null)
            {
                throw new InvalidOperationException("Kestrel did not publish a listening address.");
            }

            return new NuGetTestServerHost(
                application,
                new Uri(address),
                composition);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
    }

    public async Task ResetAsync(CancellationToken token = default)
    {
        await Packages.ResetAsync(token);
        Faults.Reset();
        Requests.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        HttpClient.Dispose();
        await _application.StopAsync();
        await _application.DisposeAsync();
    }
}

public sealed class PackageControlClient(
    IPackageStore store,
    PackageSupplyChainService supplyChain)
{
    public ValueTask AddAsync(TestPackage package, CancellationToken token = default) =>
        supplyChain.AddAsync(package, token);

    public ValueTask<TestPackage?> FindAsync(
        string id,
        string version,
        CancellationToken token = default) =>
        store.FindAsync(id, version, token);

    public ValueTask<byte[]?> FindSymbolAsync(
        string id,
        string version,
        CancellationToken token = default) =>
        store.FindSymbolAsync(id, version, token);

    public ValueTask ResetAsync(CancellationToken token = default) =>
        supplyChain.ResetAsync(token);
}

public sealed class FaultControlClient(FaultRuleStore store)
{
    public ValueTask AddAsync(FaultRule rule, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        store.Add(rule);
        return ValueTask.CompletedTask;
    }

    public void Reset() => store.Reset();
}

public sealed class RequestControlClient(RequestRecorder recorder)
{
    public ValueTask<IReadOnlyList<RequestRecord>> GetAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(recorder.GetAll());
    }

    public void Reset() => recorder.Reset();
}
