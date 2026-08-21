using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Routing;

namespace NuGet.TestServer.UnitTests;

public sealed class ExtensionCatalogTests
{
    [Fact]
    public void Every_built_in_profile_resolves_one_valid_complete_graph()
    {
        foreach (var profile in new[]
                 {
                     ServerProfiles.Embedded,
                     ServerProfiles.Standard,
                     ServerProfiles.Production
                 })
        {
            var graph = BuiltInExtensionCatalog.Instance.Resolve(profile);

            Assert.Equal(profile.Extensions.Length, graph.Extensions.Length);
            Assert.Equal(
                profile.Extensions.Select(extension => extension.Id).Order(StringComparer.Ordinal),
                graph.Extensions.Select(extension => extension.Id).Order(StringComparer.Ordinal));
            Assert.Equal(
                graph.Operations.Length,
                graph.Operations.Select(operation => operation.OperationId)
                    .Distinct(StringComparer.Ordinal)
                    .Count());
            var expectedContracts = profile.Extensions.Any(
                extension => extension.Id == BuiltInExtensionIds.TestControl)
                ? OperationContracts.All
                : OperationContracts.All
                    .Where(contract => contract.Family != OperationFamily.TestControl)
                    .ToArray();
            Assert.All(
                expectedContracts,
                contract => Assert.Single(
                    graph.Operations,
                    operation => operation.OperationId == contract.Id.Value));
            Assert.Contains(
                graph.Routes,
                route => route is { Method: "GET", Path: "/__test/health" });
        }

        var apiKeyProduction = BuiltInExtensionCatalog.Instance.Resolve(ServerProfiles.Production);
        var identityProduction = BuiltInExtensionCatalog.Instance.Resolve(
            ServerProfiles.Production,
            hasProductionIdentity: true);
        Assert.DoesNotContain(
            apiKeyProduction.Routes,
            route => route.Path == "/package/{id}/{version}/hard");
        Assert.Contains(
            identityProduction.Routes,
            route => route is { Method: "DELETE", Path: "/package/{id}/{version}/hard" });
    }

    [Fact]
    public void Duplicate_operation_owners_fail_deterministically()
    {
        var catalog = Catalog(
            Manifest("extension.b", operations: ["operation.shared"]),
            Manifest("extension.a", operations: ["operation.shared"]));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => catalog.ResolveWithTestContracts(Profile("extension.b", "extension.a")));

        Assert.Equal(
            "catalog.operation-owner-conflict: Operation 'operation.shared' is owned by " +
            "'extension.a' and 'extension.b'.",
            exception.Message);
    }

    [Fact]
    public void Duplicate_concrete_routes_fail_deterministically()
    {
        var catalog = Catalog(
            Manifest(
                "extension.b",
                operations: [TestEndpointDescriptors.OperationB],
                endpoints:
                [
                    TestEndpointDescriptors.Endpoint(
                        "b.route",
                        "GET",
                        "/packages/{id}",
                        TestEndpointDescriptors.OperationB)
                ]),
            Manifest(
                "extension.a",
                operations: [TestEndpointDescriptors.OperationA],
                endpoints:
                [
                    TestEndpointDescriptors.Endpoint(
                        "a.route",
                        "get",
                        "/packages/{id}",
                        TestEndpointDescriptors.OperationA)
                ]));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => catalog.ResolveWithTestContracts(Profile("extension.b", "extension.a")));

        Assert.Equal(
            "catalog.route-conflict: Route 'GET /packages/{id}' is owned by " +
            "'extension.a' and 'extension.b'.",
            exception.Message);
    }

    [Fact]
    public void Missing_dependencies_fail()
    {
        var catalog = Catalog(
            Manifest(
                "extension.consumer",
                dependencies: [new("extension.missing", ExtensionVersionRange.Major(1))]));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => catalog.ResolveWithTestContracts(Profile("extension.consumer")));

        Assert.Equal(
            "catalog.missing-dependency: Extension 'extension.consumer' requires missing " +
            "extension 'extension.missing' in range '[1.0.0,2.0.0)'.",
            exception.Message);
    }

    [Fact]
    public void Dependency_cycles_fail_with_stable_ordinal_path()
    {
        var catalog = Catalog(
            Manifest(
                "extension.c",
                dependencies: [new("extension.a", ExtensionVersionRange.Major(1))]),
            Manifest(
                "extension.b",
                dependencies: [new("extension.c", ExtensionVersionRange.Major(1))]),
            Manifest(
                "extension.a",
                dependencies: [new("extension.b", ExtensionVersionRange.Major(1))]));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => catalog.ResolveWithTestContracts(Profile("extension.c", "extension.b", "extension.a")));

        Assert.Equal(
            "catalog.dependency-cycle: Dependency cycle detected: " +
            "extension.a -> extension.b -> extension.c -> extension.a.",
            exception.Message);
    }

    [Fact]
    public void Incompatible_dependency_versions_fail()
    {
        var catalog = Catalog(
            Manifest(
                "extension.consumer",
                dependencies: [new("extension.provider", ExtensionVersionRange.Major(2))]),
            Manifest("extension.provider", version: new(1, 5, 0)));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => catalog.ResolveWithTestContracts(Profile("extension.provider", "extension.consumer")));

        Assert.Equal(
            "catalog.incompatible-dependency: Extension 'extension.consumer' requires " +
            "'extension.provider' in range '[2.0.0,3.0.0)', but version '1.5.0' is selected.",
            exception.Message);
    }

    [Fact]
    public void Dangling_service_index_resource_links_fail()
    {
        var catalog = Catalog(
            Manifest(
                "extension.consumer",
                operations: [TestEndpointDescriptors.OperationA],
                endpoints: [SearchEndpoint],
                resources:
                [
                    new ServiceResourceContribution(
                        "SearchQueryService",
                        "3.5.0",
                        new OperationId(TestEndpointDescriptors.OperationA),
                        "/query",
                        ServiceResourceVisibility.Advertised,
                        ServiceResourceAccess.Read,
                        [],
                        ["RegistrationsBaseUrl/3.6.0"],
                        null,
                        10,
                        ServiceResourceReadiness.Ready)
                ]));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => catalog.ResolveWithTestContracts(Profile("extension.consumer")));

        Assert.Equal(
            "catalog.missing-linked-resource: Resource 'SearchQueryService/3.5.0' from " +
            "'extension.consumer' requires missing resource 'RegistrationsBaseUrl/3.6.0'.",
            exception.Message);
    }

    [Fact]
    public void Ungranted_required_capabilities_fail()
    {
        var catalog = Catalog(
            Manifest(
                "extension.consumer",
                capabilities: [new("packages.read", IsRequired: true)]));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => catalog.ResolveWithTestContracts(Profile("extension.consumer")));

        Assert.Equal(
            "catalog.missing-capability-grant: Extension 'extension.consumer' requires " +
            "ungranted capability 'packages.read'.",
            exception.Message);
    }

    [Fact]
    public void Ungranted_optional_capabilities_are_omitted_and_reported()
    {
        var catalog = Catalog(
            Manifest(
                "extension.consumer",
                capabilities:
                [
                    new("packages.metadata.read", IsRequired: true),
                    new("http.outbound", IsRequired: false)
                ]));
        var profile = new ServerProfile(
            "test",
            ServerProfileKind.Embedded,
            [new ExtensionSelection("extension.consumer", [])],
            [new CapabilityGrant("packages.metadata.read")]);

        var graph = catalog.ResolveWithTestContracts(profile);

        var extension = Assert.Single(graph.Extensions);
        Assert.Equal(["packages.metadata.read"], graph.Capabilities
            .Where(capability => capability.ExtensionId == extension.Id && capability.IsGranted)
            .Select(capability => capability.Name));
        Assert.Equal(["http.outbound"], graph.Capabilities
            .Where(capability => capability.ExtensionId == extension.Id && !capability.IsGranted)
            .Select(capability => capability.Name));
        Assert.Contains(
            "omitted-optional=http.outbound",
            graph.Diagnostics,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resolution_order_and_diagnostics_are_stable_and_use_platform_neutral_newlines()
    {
        var manifests = new[]
        {
            Manifest(
                "extension.c",
                dependencies: [new("extension.a", ExtensionVersionRange.Major(1))],
                operations: [TestEndpointDescriptors.OperationC],
                endpoints:
                [
                    TestEndpointDescriptors.Endpoint(
                        "c.route",
                        "GET",
                        "/c",
                        TestEndpointDescriptors.OperationC)
                ]),
            Manifest(
                "extension.b",
                operations: [TestEndpointDescriptors.OperationB],
                endpoints:
                [
                    TestEndpointDescriptors.Endpoint(
                        "b.route",
                        "POST",
                        "/b",
                        TestEndpointDescriptors.OperationB)
                ]),
            Manifest(
                "extension.a",
                operations: [TestEndpointDescriptors.OperationA],
                endpoints:
                [
                    TestEndpointDescriptors.Endpoint(
                        "a.route",
                        "GET",
                        "/a",
                        TestEndpointDescriptors.OperationA)
                ])
        };
        var profile = Profile("extension.c", "extension.b", "extension.a");

        var first = Catalog(manifests).ResolveWithTestContracts(profile);
        var second = Catalog(manifests.Reverse().ToArray()).ResolveWithTestContracts(profile);

        Assert.Equal(["extension.a", "extension.b", "extension.c"], first.Extensions.Select(x => x.Id));
        Assert.Equal(
            first.Extensions.Select(extension => extension.Id),
            second.Extensions.Select(extension => extension.Id));
        Assert.Equal(first.Operations.ToArray(), second.Operations.ToArray());
        Assert.Equal(first.Routes.ToArray(), second.Routes.ToArray());
        Assert.Equal(first.Resources.ToArray(), second.Resources.ToArray());
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        Assert.DoesNotContain('\r', first.Diagnostics);
        Assert.Equal(
            "profile=test\n" +
            "extension=extension.a version=1.0.0 capabilities=- routes=GET /a resources=- " +
            "omitted-optional=-\n" +
            "extension=extension.b version=1.0.0 capabilities=- routes=POST /b resources=- " +
            "omitted-optional=-\n" +
            "extension=extension.c version=1.0.0 capabilities=- routes=GET /c resources=- " +
            "omitted-optional=-\n",
            first.Diagnostics);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(50)]
    [InlineData(200)]
    public void Catalog_resolution_remains_deterministic_at_scale(int count)
    {
        var manifests = Enumerable.Range(0, count)
            .Select(index =>
            {
                var operation = $"scale.operation.{index:D3}";
                return Manifest(
                    $"scale.extension.{index:D3}",
                    operations: [operation],
                    endpoints:
                    [
                        TestEndpointDescriptors.Endpoint(
                            $"scale.route.{index:D3}",
                            "GET",
                            $"/scale/{index:D3}",
                            operation)
                    ]);
            })
            .ToArray();
        var profile = Profile(manifests.Select(manifest => manifest.Id).Reverse().ToArray());

        var ascending = Catalog(manifests).ResolveWith(profile);
        var descending = Catalog(manifests.Reverse().ToArray()).ResolveWith(profile);

        Assert.Equal(count, ascending.Extensions.Length);
        Assert.Equal(count, ascending.Routes.Length);
        Assert.Equal(
            ascending.Extensions.Select(manifest => manifest.Id),
            descending.Extensions.Select(manifest => manifest.Id));
        Assert.Equal(ascending.Operations.ToArray(), descending.Operations.ToArray());
        Assert.Equal(ascending.Routes.ToArray(), descending.Routes.ToArray());
        Assert.Equal(ascending.Diagnostics, descending.Diagnostics);
    }

    private static ExtensionCatalog Catalog(params ExtensionManifest[] manifests) => new(manifests);

    private static readonly EndpointDescriptor SearchEndpoint =
        TestEndpointDescriptors.Endpoint(
            "consumer.query",
            "GET",
            "/query",
            TestEndpointDescriptors.OperationA);

    private static ServerProfile Profile(params string[] extensionIds) => new(
        "test",
        ServerProfileKind.Embedded,
        [.. extensionIds.Select(id => new ExtensionSelection(id, []))],
        []);

    private static ExtensionManifest Manifest(
        string id,
        ExtensionVersion? version = null,
        ImmutableArray<ExtensionDependency> dependencies = default,
        ImmutableArray<string> operations = default,
        ImmutableArray<EndpointDescriptor> endpoints = default,
        ImmutableArray<ServiceResourceContribution> resources = default,
        ImmutableArray<CapabilityRequest> capabilities = default) =>
        new(
            SchemaVersion: 1,
            Id: id,
            Version: version ?? new ExtensionVersion(1, 0, 0),
            HostCompatibility: ExtensionVersionRange.Major(1),
            Dependencies: dependencies.IsDefault ? [] : dependencies,
            Operations: operations.IsDefault ? [] : operations,
            Endpoints: endpoints.IsDefault ? [] : endpoints,
            Resources: resources.IsDefault ? [] : resources,
            RequestedCapabilities: capabilities.IsDefault ? [] : capabilities);
}
