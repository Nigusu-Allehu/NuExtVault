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

    private NuGetTestServerHost(WebApplication application, Uri baseUrl)
    {
        _application = application;
        BaseUrl = baseUrl;
        ServiceIndexUrl = new Uri(baseUrl, "/v3/index.json");
        ControlUrl = new Uri(baseUrl, "/__test");
        HttpClient = new HttpClient { BaseAddress = baseUrl };
        Packages = new PackageControlClient(
            application.Services.GetRequiredService<InMemoryPackageStore>());
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

    public static async Task<NuGetTestServerHost> StartAsync(
        CancellationToken token = default)
    {
        return await StartAsync(AuthenticationConfiguration.Anonymous, token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        AuthenticationConfiguration authentication,
        CancellationToken token = default)
    {
        return await StartAsync(
            authentication,
            EmbeddedVulnerabilitySnapshot.Load(),
            token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        VulnerabilitySnapshot vulnerabilities,
        CancellationToken token = default)
    {
        return await StartAsync(
            AuthenticationConfiguration.Anonymous,
            vulnerabilities,
            token);
    }

    public static async Task<NuGetTestServerHost> StartAsync(
        AuthenticationConfiguration authentication,
        VulnerabilitySnapshot vulnerabilities,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        ArgumentNullException.ThrowIfNull(vulnerabilities);
        var application = ServerApplication.Build(
            authentication: authentication,
            vulnerabilities: new VulnerabilitySnapshotProvider(vulnerabilities));
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

            return new NuGetTestServerHost(application, new Uri(address));
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

public sealed class PackageControlClient(InMemoryPackageStore store)
{
    public ValueTask AddAsync(TestPackage package, CancellationToken token = default) =>
        store.AddAsync(package, token);

    public ValueTask<TestPackage?> FindAsync(
        string id,
        string version,
        CancellationToken token = default) =>
        store.FindAsync(id, version, token);

    public ValueTask ResetAsync(CancellationToken token = default) => store.ResetAsync(token);
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
