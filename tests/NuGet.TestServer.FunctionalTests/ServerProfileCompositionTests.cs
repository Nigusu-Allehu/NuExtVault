using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Cli;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Vulnerabilities;

namespace NuGet.TestServer.FunctionalTests;

public sealed class ServerProfileCompositionTests
{
    [Fact]
    public async Task Every_programmatic_overload_uses_an_equivalent_profile_composition()
    {
        using var storage = TemporaryDirectory.Create();
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");
        var snapshot = EmbeddedVulnerabilitySnapshot.Load();
        var limits = new PackageTransferLimits
        {
            MaxRequestBodyBytes = 8 * 1024 * 1024,
            MaxPackageBytes = 4 * 1024 * 1024
        };
        var runtimeState = new RuntimeStateConfiguration(17, 3);
        var embeddedFactories = new Func<Task<NuGetTestServerHost>>[]
        {
            () => NuGetTestServerHost.StartAsync(),
            () => NuGetTestServerHost.StartAsync(runtimeState),
            () => NuGetTestServerHost.StartAsync(authentication),
            () => NuGetTestServerHost.StartAsync(limits),
            () => NuGetTestServerHost.StartAsync(new SupplyChainOptions()),
            () => NuGetTestServerHost.StartAsync(storage.Path, limits),
            () => NuGetTestServerHost.StartAsync(snapshot),
            () => NuGetTestServerHost.StartAsync(authentication, snapshot),
            () => NuGetTestServerHost.StartAsync(authentication, snapshot, limits),
            () => NuGetTestServerHost.StartAsync(
                authentication,
                snapshot,
                limits,
                runtimeState),
            () => NuGetTestServerHost.StartAsync(ServerMode.Test, authentication),
            () => NuGetTestServerHost.StartAsync(ServerMode.Test, authentication, storage.Path),
            () => NuGetTestServerHost.StartAsync(ServerMode.Test, authentication, snapshot),
            () => NuGetTestServerHost.StartAsync(
                ServerMode.Test,
                authentication,
                snapshot,
                limits),
            () => NuGetTestServerHost.StartAsync(
                ServerMode.Test,
                authentication,
                snapshot,
                limits,
                runtimeState)
        };

        foreach (var factory in embeddedFactories)
        {
            await using var server = await factory();
            Assert.Same(ServerProfiles.Embedded, server.Composition.Profile);
            Assert.Equal(ServerMode.Test, server.Composition.Hosting.Mode);
        }

        using var productionStorage = TemporaryDirectory.Create();
        await using var productionWithStorage = await NuGetTestServerHost.StartAsync(
            ServerMode.Production,
            authentication,
            productionStorage.Path);
        await using var productionWithoutStorage = await NuGetTestServerHost.StartAsync(
            ServerMode.Production,
            authentication);
        var productionSecurity = ProductionSecurityConfiguration.Create(
        [
            new("publisher", ["publish-key"], [SecurityScope.Publish], ["*"])
        ]);
        await using var productionIdentity = await NuGetTestServerHost.StartProductionAsync(
            productionSecurity);

        Assert.Same(ServerProfiles.Production, productionWithStorage.Composition.Profile);
        Assert.Equal(productionStorage.Path, productionWithStorage.Composition.StorageDirectory);
        Assert.Same(ServerProfiles.Production, productionWithoutStorage.Composition.Profile);
        Assert.NotNull(productionWithoutStorage.Composition.StorageDirectory);
        Assert.Same(ServerProfiles.Production, productionIdentity.Composition.Profile);
        Assert.NotNull(productionIdentity.Composition.StorageDirectory);
        Assert.Contains(
            productionIdentity.Composition.ExtensionGraph.Routes,
            route => route is
            {
                Method: "DELETE",
                Path: "/package/{id}/{version}/hard"
            });
    }

    [Fact]
    public void Cli_options_map_to_equivalent_standard_or_production_composition()
    {
        using var standardStorage = TemporaryDirectory.Create();
        using var productionStorage = TemporaryDirectory.Create();
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");
        var standard = CliServerProfileFactory.Create(
            production: false,
            url: "http://127.0.0.1:0",
            storageDirectory: standardStorage.Path,
            authentication,
            PackageTransferLimits.Default,
            trustedProxies: null);
        var production = CliServerProfileFactory.Create(
            production: true,
            url: "http://127.0.0.1:0",
            storageDirectory: productionStorage.Path,
            authentication,
            PackageTransferLimits.Default,
            new TrustedProxyOptions(["127.0.0.1"]));

        Assert.Same(ServerProfiles.Standard, standard.Profile);
        Assert.Equal(ServerMode.Test, standard.Hosting.Mode);
        Assert.Same(authentication, standard.Authentication);
        Assert.Equal(
            EmbeddedVulnerabilitySnapshot.Load().Id,
            standard.Vulnerabilities.Active.Id);
        Assert.Same(ServerProfiles.Production, production.Profile);
        Assert.Equal(ServerMode.Production, production.Hosting.Mode);
        Assert.Equal(productionStorage.Path, production.StorageDirectory);
    }

    [Fact]
    public async Task Parallel_hosts_keep_profile_configuration_and_state_isolated()
    {
        using var productionStorage = TemporaryDirectory.Create();
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");
        var embeddedTask = NuGetTestServerHost.StartAsync(
            new RuntimeStateConfiguration(requestHistoryCapacity: 1, faultRuleCapacity: 1));
        var productionTask = NuGetTestServerHost.StartAsync(
            ServerMode.Production,
            authentication,
            productionStorage.Path);
        await using var embedded = await embeddedTask;
        await using var production = await productionTask;

        await embedded.Packages.AddAsync(
            TestPackageBuilder.Create("Profile.Isolation", "1.0.0").Build());

        Assert.Same(ServerProfiles.Embedded, embedded.Composition.Profile);
        Assert.Equal(1, embedded.Composition.RuntimeState.RequestHistoryCapacity);
        Assert.Same(ServerProfiles.Production, production.Composition.Profile);
        Assert.Equal(
            RuntimeStateConfiguration.DefaultRequestHistoryCapacity,
            production.Composition.RuntimeState.RequestHistoryCapacity);
        Assert.Null(await production.Packages.FindAsync("Profile.Isolation", "1.0.0"));

        using var embeddedHealthResponse = await embedded.HttpClient.GetAsync("/__test/health");
        using var productionControlResponse = await production.HttpClient.GetAsync("/__test/state");
        var embeddedHealth = await embeddedHealthResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("test", embeddedHealth.GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.NotFound, productionControlResponse.StatusCode);
    }

    [Fact]
    public async Task One_hundred_parallel_embedded_hosts_keep_mutable_state_isolated()
    {
        var starts = Enumerable.Range(0, 100)
            .Select(_ => NuGetTestServerHost.StartAsync())
            .ToArray();
        var hosts = await Task.WhenAll(starts);
        try
        {
            await hosts[0].Packages.AddAsync(
                TestPackageBuilder.Create("Hundred.Host.Isolation", "1.0.0").Build());

            var reads = hosts.Select(host =>
                host.Packages.FindAsync("Hundred.Host.Isolation", "1.0.0").AsTask());
            var packages = await Task.WhenAll(reads);

            Assert.NotNull(packages[0]);
            Assert.All(packages[1..], Assert.Null);
            Assert.Equal(100, hosts.Select(host => host.Port).Distinct().Count());
        }
        finally
        {
            await Task.WhenAll(hosts.Select(host => host.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public void Composition_resolves_the_extension_graph_before_an_application_can_listen()
    {
        var composition = ServerComposition.Create(ServerProfiles.Embedded);

        Assert.Equal(
            ServerProfiles.Embedded.Extensions.Length,
            composition.ExtensionGraph.Extensions.Length);
        Assert.StartsWith("profile=embedded\n", composition.ExtensionGraph.Diagnostics);

        var invalidProfile = ServerProfiles.Embedded with
        {
            Grants = ServerProfiles.Embedded.Grants.RemoveAll(
                grant => grant.Name == BuiltInCapabilityNames.PackagesMetadataRead)
        };
        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => ServerComposition.Create(invalidProfile));

        Assert.Contains("catalog.missing-capability-grant", exception.Message);
    }

    [Fact]
    public async Task Public_production_builder_preserves_storage_optional_compatibility()
    {
        var authentication = AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");
        var app = ServerApplication.Build(
            mode: ServerMode.Production,
            authentication: authentication);
        var composition = app.Services.GetRequiredService<ServerComposition>();
        var storageDirectory = composition.StorageDirectory;

        Assert.Same(ServerProfiles.Production, composition.Profile);
        Assert.NotNull(storageDirectory);
        Assert.True(Directory.Exists(storageDirectory));

        await app.DisposeAsync();

        Assert.False(Directory.Exists(storageDirectory));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NuGet.TestServer.FunctionalTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
