using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class ServerProfileTests
{
    [Fact]
    public void Built_in_profiles_capture_current_feature_sets()
    {
        Assert.Equal("embedded", ServerProfiles.Embedded.Name);
        Assert.Equal("standard", ServerProfiles.Standard.Name);
        Assert.Equal("production", ServerProfiles.Production.Name);

        Assert.Contains(
            ServerProfiles.Embedded.Extensions,
            extension => extension.Id == BuiltInExtensionIds.TestControl);
        Assert.DoesNotContain(
            ServerProfiles.Standard.Extensions,
            extension => extension.Id == "builtin.vulnerability-refresh");
        Assert.Single(
            ServerProfiles.Standard.Extensions,
            extension => extension.Id == BuiltInExtensionIds.Vulnerabilities);
        Assert.DoesNotContain(
            ServerProfiles.Production.Extensions,
            extension => extension.Id == BuiltInExtensionIds.TestControl);
        Assert.Contains(
            ServerProfiles.Production.Extensions,
            extension => extension.Id == BuiltInExtensionIds.Operations);
    }

    [Fact]
    public void Production_profile_rejects_test_control_selection()
    {
        using var storage = TemporaryDirectory.Create();
        var testControl = Assert.Single(
            ServerProfiles.Standard.Extensions,
            extension => extension.Id == BuiltInExtensionIds.TestControl);
        var invalid = ServerProfiles.Production with
        {
            Extensions = ServerProfiles.Production.Extensions.Add(testControl),
            Grants = ServerProfiles.Production.Grants
                .Add(new CapabilityGrant(BuiltInCapabilityNames.ControlPackagesManage))
                .Add(new CapabilityGrant(BuiltInCapabilityNames.ControlInstrumentationManage))
        };

        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                invalid,
                storageDirectory: storage.Path,
                authentication: CreateProductionAuthentication(),
                supplyChain: new SupplyChainOptions()));

        Assert.Contains("production", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("control", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Embedded_profile_denies_outbound_network_and_sidecars()
    {
        Assert.DoesNotContain(
            ServerProfiles.Embedded.Grants,
            grant => grant.Name == BuiltInCapabilityNames.OutboundHttp);
        Assert.DoesNotContain(
            ServerProfiles.Embedded.Grants,
            grant => grant.Name == BuiltInCapabilityNames.SidecarExecution);
    }

    [Fact]
    public void Capability_requests_and_grants_are_immutable()
    {
        var extension = Assert.Single(
            ServerProfiles.Standard.Extensions,
            extension => extension.Id == BuiltInExtensionIds.Vulnerabilities);
        var originalRequests = extension.RequestedCapabilities;
        var originalGrants = ServerProfiles.Standard.Grants;

        var changedRequests = originalRequests.Add(
            new CapabilityRequest("test.request", IsRequired: false));
        var changedGrants = originalGrants.Add(new CapabilityGrant("test.grant"));

        Assert.DoesNotContain(originalRequests, request => request.Name == "test.request");
        Assert.DoesNotContain(originalGrants, grant => grant.Name == "test.grant");
        Assert.NotEqual(originalRequests, changedRequests);
        Assert.NotEqual(originalGrants, changedGrants);
    }

    [Fact]
    public void Vulnerability_owner_requests_persistence_and_network_only_in_cli_profiles()
    {
        var embedded = Assert.Single(
            ServerProfiles.Embedded.Extensions,
            extension => extension.Id == BuiltInExtensionIds.Vulnerabilities);
        var standard = Assert.Single(
            ServerProfiles.Standard.Extensions,
            extension => extension.Id == BuiltInExtensionIds.Vulnerabilities);
        var production = Assert.Single(
            ServerProfiles.Production.Extensions,
            extension => extension.Id == BuiltInExtensionIds.Vulnerabilities);

        Assert.Equal(
            [BuiltInCapabilityNames.VulnerabilityStateRead],
            embedded.RequestedCapabilities.Select(request => request.Name));
        Assert.Equal(
            [
                BuiltInCapabilityNames.VulnerabilityStateRead,
                BuiltInCapabilityNames.ExtensionStateRead,
                BuiltInCapabilityNames.ExtensionStateWrite,
                BuiltInCapabilityNames.OutboundHttp
            ],
            standard.RequestedCapabilities.Select(request => request.Name));
        Assert.Equal(
            standard.RequestedCapabilities,
            production.RequestedCapabilities);
        Assert.All(standard.RequestedCapabilities, request => Assert.True(request.IsRequired));
    }

    [Fact]
    public void Production_profile_requires_durable_storage()
    {
        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                ServerProfiles.Production,
                authentication: CreateProductionAuthentication(),
                supplyChain: new SupplyChainOptions()));

        Assert.Contains("durable storage", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Production_profile_requires_security_operations_and_supply_chain_policy()
    {
        using var storage = TemporaryDirectory.Create();

        Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                ServerProfiles.Production,
                storageDirectory: storage.Path,
                supplyChain: new SupplyChainOptions()));

        var missingOperations = ServerProfiles.Production with
        {
            Extensions = ServerProfiles.Production.Extensions.RemoveAll(
                extension => extension.Id == BuiltInExtensionIds.Operations)
        };
        Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                missingOperations,
                storageDirectory: storage.Path,
                authentication: CreateProductionAuthentication(),
                supplyChain: new SupplyChainOptions()));

        Assert.Throws<ServerHostingConfigurationException>(() =>
            ServerComposition.Create(
                ServerProfiles.Production,
                storageDirectory: storage.Path,
                authentication: CreateProductionAuthentication()));
    }

    private static AuthenticationConfiguration CreateProductionAuthentication() =>
        AuthenticationConfiguration.Create(
            username: null,
            password: null,
            apiKey: "publish-key");

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NuGet.TestServer.UnitTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
