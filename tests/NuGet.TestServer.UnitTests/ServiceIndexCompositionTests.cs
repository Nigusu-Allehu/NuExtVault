using System.Collections.Immutable;
using System.Text.Json;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Routing;
using NuGet.TestServer.Kernel;

namespace NuGet.TestServer.UnitTests;

public sealed class ServiceIndexCompositionTests
{
    [Fact]
    public void Built_in_profiles_select_the_exact_compatible_resource_set()
    {
        var expected =
            new[]
            {
                "PackageBaseAddress/3.0.0",
                "RegistrationsBaseUrl/3.6.0",
                "SearchQueryService/3.0.0-beta",
                "SearchQueryService/3.5.0",
                "PackagePublish/2.0.0",
                "SymbolPackagePublish/4.9.0",
                "VulnerabilityInfo/6.7.0"
            };

        foreach (var profile in new[]
                 {
                     ServerProfiles.Embedded,
                     ServerProfiles.Standard,
                     ServerProfiles.Production
                 })
        {
            var graph = BuiltInExtensionCatalog.Instance.Resolve(profile);

            Assert.Equal(expected, graph.Resources
                .Select(resource => resource.Contribution.AdvertisedType)
                .ToArray());
            Assert.All(
                graph.Resources,
                resource => Assert.Equal(
                    ServiceResourceReadiness.Ready,
                    resource.Contribution.Readiness));
        }
    }

    [Fact]
    public void Service_index_is_owned_by_its_official_feature_owner()
    {
        var graph = BuiltInExtensionCatalog.Instance.Resolve(ServerProfiles.Embedded);

        var operation = Assert.Single(
            graph.Operations,
            operation => operation.OperationId == OperationIds.ServiceIndexGet);
        Assert.Equal(BuiltInExtensionIds.ServiceIndex, operation.ExtensionId);
        Assert.DoesNotContain(
            graph.Resources,
            resource => resource.ExtensionId == BuiltInExtensionIds.ServiceIndex);
    }

    [Fact]
    public void Typed_contributions_expose_only_route_references_and_known_fields()
    {
        var registry = CreateRegistry(
            Contribution(
                "NuTest.Synthetic",
                "1.0.0",
                "NuTest.Synthetic.Get",
                "/synthetic/",
                "/synthetic/{id}",
                comment: "Synthetic test resource."));

        var resource = Assert.Single(registry.Resources);

        Assert.Equal(RouteReference.Base("NuTest.Synthetic.Get"), resource.Route);
        Assert.Equal("NuTest.Synthetic/1.0.0", resource.ResourceType);
        Assert.Equal("Synthetic test resource.", resource.Comment);
        Assert.DoesNotContain(
            typeof(ServiceResourceDescriptor).GetProperties(),
            property => property.PropertyType == typeof(object) ||
                        property.PropertyType == typeof(JsonElement) ||
                        typeof(System.Collections.IDictionary).IsAssignableFrom(property.PropertyType));
    }

    [Fact]
    public void Projection_order_is_deterministic_and_does_not_follow_registration_order()
    {
        var first = CreateRegistry(
            Contribution("NuTest.Second", "1.0.0", "NuTest.Second.Get", "/second", "/second", order: 20),
            Contribution("NuTest.First", "1.0.0", "NuTest.First.Get", "/first", "/first", order: 10));
        var second = CreateRegistry(
            Contribution("NuTest.First", "1.0.0", "NuTest.First.Get", "/first", "/first", order: 10),
            Contribution("NuTest.Second", "1.0.0", "NuTest.Second.Get", "/second", "/second", order: 20));

        Assert.Equal(
            ["NuTest.First/1.0.0", "NuTest.Second/1.0.0"],
            first.Resources.Select(resource => resource.ResourceType));
        Assert.Equal(
            first.Resources.ToArray(),
            second.Resources.ToArray());
    }

    [Fact]
    public void Synthetic_internal_resource_is_added_without_service_index_implementation_changes()
    {
        var graph = Catalog(
                Manifest(
                    "builtin.synthetic",
                    operations: ["NuTest.Synthetic.Get"],
                    endpoints: [Endpoint("synthetic.route", "/synthetic/{id}", "NuTest.Synthetic.Get")],
                    resources:
                    [
                        Contribution(
                            "NuTest.Synthetic",
                            "1.0.0",
                            "NuTest.Synthetic.Get",
                            "/synthetic/",
                            "/synthetic/{id}")
                    ]))
            .ResolveWith(Profile("builtin.synthetic"));

        var projected = ServiceIndexResourceRegistry.Create(graph).Resources;

        Assert.Equal("NuTest.Synthetic/1.0.0", Assert.Single(projected).ResourceType);
    }

    [Fact]
    public void Duplicate_single_owner_resource_types_fail_before_listening()
    {
        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            Catalog(
                    Manifest(
                        "extension.a",
                        operations: ["NuTest.A.Get"],
                        endpoints: [Endpoint("a.route", "/a", "NuTest.A.Get")],
                        resources:
                        [
                            Contribution("NuTest.Shared", "1.0.0", "NuTest.A.Get", "/a", "/a")
                        ]),
                    Manifest(
                        "extension.b",
                        operations: ["NuTest.B.Get"],
                        endpoints: [Endpoint("b.route", "/b", "NuTest.B.Get")],
                        resources:
                        [
                            Contribution("NuTest.Shared", "1.0.0", "NuTest.B.Get", "/b", "/b")
                        ]))
                .ResolveWith(Profile("extension.a", "extension.b")));

        Assert.StartsWith("catalog.resource-owner-conflict:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_well_known_resource_versions_fail_before_listening()
    {
        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            CreateRegistry(
                Contribution(
                    "PackageBaseAddress",
                    "9.0.0",
                    "NuGet.FlatContainer.GetVersions",
                    "/flatcontainer/",
                    "/flatcontainer/{id}/index.json")));

        Assert.StartsWith(
            "catalog.unsupported-resource-version:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_operation_owners_fail_before_listening()
    {
        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            Catalog(
                    Manifest(
                        "extension.resource",
                        operations: ["NuTest.Resource.Get"],
                        endpoints: [Endpoint("resource.route", "/resource", "NuTest.Resource.Get")],
                        resources:
                        [
                            Contribution(
                                "NuTest.Resource",
                                "1.0.0",
                                "NuTest.Missing.Get",
                                "/resource",
                                "/resource")
                        ]))
                .ResolveWith(Profile("extension.resource")));

        Assert.StartsWith(
            "catalog.missing-resource-operation:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_operation_owner_mismatches_fail_before_listening()
    {
        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            Catalog(
                    Manifest("extension.owner", operations: ["NuTest.Resource.Get"]),
                    Manifest(
                        "extension.resource",
                        operations: ["NuTest.Other.Get"],
                        endpoints: [Endpoint("resource.route", "/resource", "NuTest.Other.Get")],
                        resources:
                        [
                            Contribution(
                                "NuTest.Resource",
                                "1.0.0",
                                "NuTest.Resource.Get",
                                "/resource",
                                "/resource")
                        ]))
                .ResolveWith(Profile("extension.owner", "extension.resource")));

        Assert.StartsWith(
            "catalog.resource-operation-owner-mismatch:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_route_mismatches_fail_before_listening()
    {
        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            Catalog(
                    Manifest(
                        "extension.resource",
                        operations: ["NuTest.Resource.Get"],
                        endpoints: [Endpoint("resource.route", "/resource", "NuTest.Resource.Get")],
                        resources:
                        [
                            Contribution(
                                "NuTest.Resource",
                                "1.0.0",
                                "NuTest.Resource.Get",
                                "/different-route",
                                "/resource")
                        ]))
                .ResolveWith(Profile("extension.resource")));

        Assert.StartsWith("catalog.missing-resource-route:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dangling_linked_resource_url_production_fails_before_listening()
    {
        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            CreateRegistry(
                Contribution(
                    "NuTest.Resource",
                    "1.0.0",
                    "NuTest.Resource.Get",
                    "/resource",
                    "/resource",
                    producesUrlsFor: ["NuTest.Missing/1.0.0"])));

        Assert.StartsWith(
            "catalog.missing-produced-resource:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Advertised_resource_owner_must_be_ready_before_listening()
    {
        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            CreateRegistry(
                Contribution(
                    "NuTest.Resource",
                    "1.0.0",
                    "NuTest.Resource.Get",
                    "/resource",
                    "/resource",
                    readiness: ServiceResourceReadiness.NotReady)));

        Assert.StartsWith("catalog.resource-not-ready:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_selected_resource_owner_must_also_be_ready_before_listening()
    {
        var resource = Contribution(
            "NuTest.Resource",
            "1.0.0",
            "NuTest.Resource.Get",
            "/resource",
            "/resource",
            readiness: ServiceResourceReadiness.NotReady) with
        {
            Visibility = ServiceResourceVisibility.Hidden
        };

        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            CreateRegistry(resource));

        Assert.StartsWith("catalog.resource-not-ready:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resource_access_policy_mismatches_fail_before_listening()
    {
        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            CreateRegistry(
                Contribution(
                    "PackageBaseAddress",
                    "3.0.0",
                    OperationIds.FlatContainerGetVersions,
                    "/flatcontainer/",
                    "/flatcontainer/{id}/index.json",
                    requiredAccess: ServiceResourceAccess.Write)));

        Assert.StartsWith("catalog.resource-access-mismatch:", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceIndexResourceRegistry CreateRegistry(
        params ServiceResourceContribution[] contributions)
    {
        var manifests = contributions.Select((contribution, index) =>
            Manifest(
                $"extension.{index}",
                operations: [contribution.OperationId.Value],
                endpoints: [Endpoint(
                    contribution.OperationId.Value,
                    RoutePathFor(contribution),
                    contribution.OperationId.Value)],
                resources: [contribution]));
        var catalog = Catalog([.. manifests]);
        return ServiceIndexResourceRegistry.Create(
            catalog.ResolveWith(Profile(manifests.Select(manifest => manifest.Id).ToArray())));
    }

    private static ServiceResourceContribution Contribution(
        string resourceType,
        string version,
        string operationId,
        string routeName,
        string owningRoutePath,
        ServiceResourceAccess requiredAccess = ServiceResourceAccess.Read,
        ImmutableArray<string> producesUrlsFor = default,
        ImmutableArray<string> requiresResourceTypes = default,
        string? comment = null,
        int order = 10,
        ServiceResourceReadiness readiness = ServiceResourceReadiness.Ready) =>
        new(
            resourceType,
            version,
            new OperationId(operationId),
            routeName,
            ServiceResourceVisibility.Advertised,
            requiredAccess,
            producesUrlsFor.IsDefault ? [] : producesUrlsFor,
            requiresResourceTypes.IsDefault ? [] : requiresResourceTypes,
            comment,
            order,
            readiness);

    private static ExtensionCatalog Catalog(params ExtensionManifest[] manifests) => new(manifests);

    private static EndpointDescriptor Endpoint(string name, string path, string operationId) =>
        TestEndpointDescriptors.Endpoint(name, "GET", path, operationId);

    private static string RoutePathFor(ServiceResourceContribution contribution) =>
        contribution.RouteName.EndsWith('/')
            ? $"{contribution.RouteName}{{id}}/index.json"
            : contribution.RouteName;

    private static ServerProfile Profile(params string[] extensionIds) => new(
        "test",
        ServerProfileKind.Embedded,
        [.. extensionIds.Select(id => new ExtensionSelection(id, []))],
        []);

    private static ExtensionManifest Manifest(
        string id,
        ImmutableArray<string> operations = default,
        ImmutableArray<EndpointDescriptor> endpoints = default,
        ImmutableArray<ServiceResourceContribution> resources = default) =>
        new(
            1,
            id,
            new ExtensionVersion(1, 0, 0),
            ExtensionVersionRange.Major(1),
            [],
            operations.IsDefault ? [] : operations,
            endpoints.IsDefault ? [] : endpoints,
            resources.IsDefault ? [] : resources,
            []);
}
