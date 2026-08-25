using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NuExtVault.Authentication;
using NuExtVault.Extensions.Sdk;
using NuExtVault.ExternalExtensionTestKit;
using NuExtVault.Hosting;
using NuExtVault.Kernel.Capabilities;

namespace NuExtVault.FunctionalTests;

/// <summary>
/// Step 20 ("Add trusted third-party in-process loading") functional coverage. These
/// tests boot a real Kestrel-backed
/// <see cref="NuExtVaultHost"/> whose profile enables the
/// <c>externalExtensions</c> parameter of <see cref="ServerComposition.Create"/>,
/// loading the real, separately packaged <c>Contoso.NuExtVault.Flavors</c>
/// fixture (tests/NuExtVault.SdkFixture, packed by
/// <see cref="ExternalExtensionPackageBuilder.BuildContosoFlavorsAssetsAsync"/>)
/// from a signed, trusted, on-disk `.nupkg` — not from an in-process
/// <c>modules:</c> reference — the way a real administrator-installed package
/// would be discovered.
///
/// </summary>
[Collection(nameof(ExternalExtensionFunctionalAssetsCollection))]
public sealed class ExternalExtensionFunctionalTests(ExternalExtensionFunctionalAssetsFixture fixture)
{
    [Fact]
    public async Task The_real_kestrel_host_serves_the_trusted_external_packages_route_with_get_and_head()
    {
        await using var server = await StartHostWithTrustedFlavorsPackageAsync();

        using var index = await server.HttpClient.GetAsync("/flavors/index.json");
        using var head = await server.HttpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, "/flavors/index.json"));

        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        using var document = JsonDocument.Parse(await index.Content.ReadAsStringAsync());
        Assert.Equal(
            ["vanilla"],
            document.RootElement.GetProperty("flavors")
                .EnumerateArray()
                .Select(flavor => flavor.GetString() ?? string.Empty)
                .ToArray());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_external_packages_required_host_clock_capability_is_fulfilled_through_the_broker()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        await using var server = await StartHostWithTrustedFlavorsPackageAsync();

        using var response = await server.HttpClient.GetAsync("/flavors/index.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var generatedAt = document.RootElement.GetProperty("generatedAt").GetDateTimeOffset();
        Assert.InRange(generatedAt, before, DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task External_capability_context_is_bound_to_host_owner_manifest_and_staged_content()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(fixture.FlavorsAssets, key));
        var composition = ServerComposition.Create(
            HostProfile(),
            authentication: AuthenticationConfiguration.Anonymous,
            externalExtensions: new ExternalExtensionConfiguration(
                [.. roots.Roots],
                [trustRoot],
                TimeProvider.System));
        await using var application = ServerApplication.Build(composition);
        await application.StartAsync();

        var context = application.Services.GetRequiredService<CapabilityBroker>()
            .ForOwner("Contoso.Flavors");

        Assert.Equal(composition.InstanceId, context.HostInstanceId);
        Assert.Equal("Contoso.Flavors", context.OwnerId);
        Assert.Matches("^[0-9a-f]{64}$", context.ManifestDigest);
        Assert.Matches("^[0-9a-f]{64}$", context.StagedContentDigest);
        var handle = context.GetRequired<IHostClockCapability>(
            BuiltInCapabilityNames.HostClockRead);
        var identity = Assert.IsAssignableFrom<ICapabilityHandleIdentity>(handle);
        Assert.Equal(context.ManifestDigest, identity.ManifestDigest);
        Assert.Equal(context.StagedContentDigest, identity.StagedContentDigest);
    }

    [Fact]
    public async Task The_service_index_advertises_the_external_packages_resource_with_a_projected_url()
    {
        await using var server = await StartHostWithTrustedFlavorsPackageAsync();

        using var response = await server.HttpClient.GetAsync("/v3/index.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var resource = document.RootElement.GetProperty("resources")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("@type").GetString() == "Contoso.Flavors.ServiceIndex/1");
        var id = resource.GetProperty("@id").GetString();

        Assert.Equal(new Uri(server.BaseUrl, "/flavors/index.json").AbsoluteUri, id);

        // The advertised URL is a real, servable route through the real gateway.
        using var followed = await server.HttpClient.GetAsync(id);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
    }

    [Fact]
    public async Task Startup_diagnostics_report_the_successfully_loaded_trusted_package()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(fixture.FlavorsAssets, key));
        var configuration = new ExternalExtensionConfiguration(
            [.. roots.Roots],
            [trustRoot],
            TimeProvider.System);

        var composition = ServerComposition.Create(
            HostProfile(),
            authentication: AuthenticationConfiguration.Anonymous,
            externalExtensions: configuration);
        await using var server = await NuExtVaultHost.StartCompositionAsync(
            composition,
            CancellationToken.None);

        var diagnostics = server.ExternalExtensionDiagnostics;
        var result = Assert.Single(diagnostics.Results);
        Assert.True(result.Succeeded);
        Assert.Equal(fixture.FlavorsAssets.Id, result.PackageId);
    }

    [Fact]
    public async Task A_host_that_never_configures_externalExtensions_never_sees_the_route()
    {
        // Proves the mechanism is purely additive and opt-in: a completely
        // vanilla profile/composition call — with no `externalExtensions`
        // argument at all, and therefore no kernel source change of any kind —
        // never serves a route that only a trusted external package contributes.
        await using var server = await NuExtVaultHost.StartCompositionAsync(
            ServerComposition.Create(
                HostProfile(),
                authentication: AuthenticationConfiguration.Anonymous),
            CancellationToken.None);

        using var response = await server.HttpClient.GetAsync("/flavors/index.json");
        using var index = await server.HttpClient.GetAsync("/v3/index.json");
        using var document = JsonDocument.Parse(await index.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(
            document.RootElement.GetProperty("resources").EnumerateArray(),
            entry => entry.GetProperty("@type").GetString()?.StartsWith(
                "Contoso.Flavors",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Disposing_the_direct_server_application_disposes_the_external_runtime()
    {
        var runtime = await BuildDisposeAndReleaseApplicationAsync();

        Assert.Empty(runtime.Modules);
    }

    private async Task<NuExtVaultHost> StartHostWithTrustedFlavorsPackageAsync()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(fixture.FlavorsAssets, key));
        var configuration = new ExternalExtensionConfiguration(
            [.. roots.Roots],
            [trustRoot],
            TimeProvider.System);

        var composition = ServerComposition.Create(
            HostProfile(),
            authentication: AuthenticationConfiguration.Anonymous,
            externalExtensions: configuration);
        return await NuExtVaultHost.StartCompositionAsync(composition, CancellationToken.None);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<ExternalExtensionRuntime> BuildDisposeAndReleaseApplicationAsync()
    {
        var (key, trustRoot) = ConformanceAttestationFixture.CreateTrustedKey();
        using var roots = ExternalExtensionRootFixture.CreateRoots();
        roots.WritePackage(
            "flavors.nupkg",
            ExternalExtensionPackageBuilder.BuildValidPackage(fixture.FlavorsAssets, key));
        var composition = ServerComposition.Create(
            HostProfile(),
            authentication: AuthenticationConfiguration.Anonymous,
            externalExtensions: new ExternalExtensionConfiguration(
                [.. roots.Roots],
                [trustRoot],
                TimeProvider.System));
        var runtime = composition.ExternalExtensions;
        await using (var application = ServerApplication.Build(composition))
        {
            await application.StartAsync();
            await application.StopAsync();
        }

        return runtime;
    }

    private static ServerProfile HostProfile() =>
        ServerProfiles.Embedded with
        {
            Grants =
            [
                .. ServerProfiles.Embedded.Grants,
                new CapabilityGrant(BuiltInCapabilityNames.HostClockRead)
            ]
        };
}

/// <summary>Caches the one expensive `dotnet pack` of the real Contoso Flavors
/// fixture across every test in
/// <see cref="ExternalExtensionFunctionalAssetsCollection"/>.</summary>
public sealed class ExternalExtensionFunctionalAssetsFixture : IAsyncLifetime
{
    public ContosoFlavorsAssets FlavorsAssets { get; private set; } = null!;

    public async Task InitializeAsync() =>
        FlavorsAssets = await ExternalExtensionPackageBuilder.BuildContosoFlavorsAssetsAsync();

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition(nameof(ExternalExtensionFunctionalAssetsCollection))]
public sealed class ExternalExtensionFunctionalAssetsCollection :
    ICollectionFixture<ExternalExtensionFunctionalAssetsFixture>;
