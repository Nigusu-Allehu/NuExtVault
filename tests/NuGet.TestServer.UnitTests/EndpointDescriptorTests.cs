using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Routing;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

/// <summary>
/// Step 11A: the kernel generates every active route from validated, transport-neutral
/// endpoint descriptors and freezes the resulting table before the host listens.
/// </summary>
public sealed class EndpointDescriptorTests
{
    private const string OperationA = TestEndpointDescriptors.OperationA;
    private const string OperationB = TestEndpointDescriptors.OperationB;

    [Fact]
    public void Duplicate_method_and_path_fails_deterministically()
    {
        var catalog = Catalog(
            Manifest("extension.b", Endpoint("b.packages", "GET", "/packages/{id}", OperationB)),
            Manifest("extension.a", Endpoint("a.packages", "get", "/packages/{id}", OperationA)));

        var first = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.b", "extension.a"));
        var second = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a", "extension.b"));

        Assert.Equal(
            "catalog.route-conflict: Route 'GET /packages/{id}' is owned by " +
            "'extension.a' and 'extension.b'.",
            first.Message);
        Assert.Equal(first.Message, second.Message);
    }

    [Fact]
    public void Equivalent_parameterized_templates_collide_deterministically()
    {
        var catalog = Catalog(
            Manifest(
                "extension.b",
                Endpoint("b.leaf", "GET", "/packages/{id}/leaf.json", OperationB)),
            Manifest(
                "extension.a",
                Endpoint("a.leaf", "GET", "/packages/{name}/leaf.json", OperationA)));

        var first = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.b", "extension.a"));
        var second = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a", "extension.b"));

        Assert.Equal(
            "catalog.endpoint-collision: Route 'GET /packages/{name}/leaf.json' owned by " +
            "'extension.a' semantically collides with 'GET /packages/{id}/leaf.json' owned by " +
            "'extension.b'.",
            first.Message);
        Assert.Equal(first.Message, second.Message);
    }

    [Fact]
    public void Equivalent_parameterized_templates_collide_without_regard_to_literal_casing()
    {
        var catalog = Catalog(
            Manifest(
                "extension.b",
                Endpoint("b.leaf", "GET", "/Packages/{id}/leaf.json", OperationB)),
            Manifest(
                "extension.a",
                Endpoint("a.leaf", "GET", "/packages/{name}/leaf.json", OperationA)));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a", "extension.b"));

        Assert.StartsWith(
            "catalog.endpoint-collision:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Literal_segments_take_deterministic_precedence_over_parameters()
    {
        var graph = BuiltInExtensionCatalog.Instance.Resolve(ServerProfiles.Embedded);

        Assert.Contains(
            graph.Endpoints,
            endpoint => endpoint.Descriptor.PathTemplate == "/registration/{id}/index.json");
        Assert.Contains(
            graph.Endpoints,
            endpoint => endpoint.Descriptor.PathTemplate == "/registration/{id}/{version}.json");
    }

    [Theory]
    [InlineData("packages/{id}")]
    [InlineData("/packages//{id}")]
    [InlineData("/packages/{id")]
    [InlineData("/packages/{id}/{id}")]
    [InlineData("/packages/{*rest}")]
    [InlineData("/packages/{}")]
    public void Malformed_path_templates_fail(string template)
    {
        var catalog = Catalog(Manifest("extension.a", Endpoint("a.route", "GET", template)));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a"));

        Assert.StartsWith("catalog.invalid-endpoint:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reserved_path_prefixes_fail()
    {
        var catalog = Catalog(
            Manifest("extension.a", Endpoint("a.route", "GET", "/__kernel/routes")));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a"));

        Assert.StartsWith("catalog.reserved-endpoint:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reserved_route_names_fail()
    {
        var catalog = Catalog(
            Manifest("extension.a", Endpoint("kernel.routes", "GET", "/packages/{id}")));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a"));

        Assert.StartsWith("catalog.reserved-endpoint:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_route_names_fail()
    {
        var catalog = Catalog(
            Manifest(
                "extension.a",
                Endpoint("a.route", "GET", "/first/{id}"),
                Endpoint("a.route", "GET", "/second/{id}")));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a"));

        Assert.StartsWith(
            "catalog.duplicate-endpoint-name:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Declared_route_parameters_must_match_the_template()
    {
        var catalog = Catalog(
            Manifest(
                "extension.a",
                Endpoint("a.route", "GET", "/packages/{id}") with
                {
                    RouteParameters = [new EndpointParameter("version")]
                }));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a"));

        Assert.StartsWith("catalog.invalid-endpoint:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_access_policy_fails()
    {
        var catalog = Catalog(
            Manifest(
                "extension.a",
                Endpoint("a.route", "GET", "/packages/{id}") with
                {
                    Access = EndpointAccessPolicy.Unspecified
                }));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a"));

        Assert.StartsWith(
            "catalog.endpoint-access-policy:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_limits_fail()
    {
        var catalog = Catalog(
            Manifest(
                "extension.a",
                Endpoint("a.route", "GET", "/packages/{id}") with
                {
                    Limits = new EndpointLimits(0, 0, 0, TimeSpan.Zero)
                }));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a"));

        Assert.StartsWith("catalog.endpoint-limits:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Head_policy_must_match_the_declared_methods()
    {
        var missingHead = Catalog(
            Manifest(
                "extension.a",
                Endpoint("a.route", "GET", "/packages/{id}") with
                {
                    Head = EndpointHeadPolicy.MirrorsGet
                }));
        var missingGet = Catalog(
            Manifest("extension.a", Endpoint("a.route", "HEAD", "/packages/{id}")));

        var withoutHead = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(missingHead, "extension.a"));
        var withoutGet = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(missingGet, "extension.a"));

        Assert.StartsWith(
            "catalog.endpoint-head-policy:",
            withoutHead.Message,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "catalog.endpoint-head-policy:",
            withoutGet.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Body_binding_must_be_complete_for_the_declared_methods()
    {
        var readWithBody = Catalog(
            Manifest(
                "extension.a",
                Endpoint("a.route", "GET", "/packages/{id}") with
                {
                    Body = EndpointBodyBinding.Stream
                }));
        var writeWithoutBody = Catalog(
            Manifest("extension.a", Endpoint("a.route", "PUT", "/packages/{id}")));

        var withBody = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(readWithBody, "extension.a"));
        var withoutBody = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(writeWithoutBody, "extension.a"));

        Assert.StartsWith("catalog.endpoint-binding:", withBody.Message, StringComparison.Ordinal);
        Assert.StartsWith(
            "catalog.endpoint-binding:",
            withoutBody.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_operations_fail()
    {
        var catalog = Catalog(
            Manifest(
                "extension.a",
                Endpoint("a.route", "GET", "/packages/{id}", "test.missing")));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a"));

        Assert.StartsWith(
            "catalog.unknown-endpoint-operation:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Inactive_operations_fail()
    {
        var catalog = Catalog(
            Manifest("extension.a", Endpoint("a.route", "GET", "/packages/{id}")) with
            {
                Operations = []
            });

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a"));

        Assert.StartsWith(
            "catalog.inactive-endpoint-operation:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_owner_mismatch_fails()
    {
        var catalog = Catalog(
            Manifest("extension.a", Endpoint("a.route", "GET", "/packages/{id}", OperationB)) with
            {
                Operations = []
            },
            Manifest("extension.owner") with { Operations = [OperationB] });

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a", "extension.owner"));

        Assert.StartsWith(
            "catalog.endpoint-owner-mismatch:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_contract_mismatch_fails()
    {
        var catalog = Catalog(
            Manifest(
                "extension.a",
                Endpoint("a.route", "GET", "/packages/{id}") with
                {
                    Operations =
                    [
                        EndpointDescriptor.Operation<EmptyResponse, EmptyRequest>(OperationA)
                    ]
                }));

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a"));

        Assert.StartsWith(
            "catalog.endpoint-contract-mismatch:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resolved_endpoints_are_ordered_independently_of_declaration_order()
    {
        var forward = Catalog(
            Manifest("extension.a", Endpoint("a.route", "GET", "/a/{id}", OperationA)),
            Manifest("extension.b", Endpoint("b.route", "GET", "/b/{id}", OperationB)));
        var reversed = Catalog(
            Manifest("extension.b", Endpoint("b.route", "GET", "/b/{id}", OperationB)),
            Manifest("extension.a", Endpoint("a.route", "GET", "/a/{id}", OperationA)));

        var first = Resolve(forward, "extension.b", "extension.a");
        var second = Resolve(reversed, "extension.a", "extension.b");

        Assert.Equal(
            first.Endpoints.Select(endpoint => endpoint.Descriptor.Name),
            second.Endpoints.Select(endpoint => endpoint.Descriptor.Name));
        Assert.Equal(
            ["a.route", "b.route"],
            first.Endpoints.Select(endpoint => endpoint.Descriptor.Name).ToArray());
    }

    [Fact]
    public void Built_in_route_table_is_frozen_and_matches_the_resolved_graph()
    {
        var graph = BuiltInExtensionCatalog.Instance.Resolve(ServerProfiles.Embedded);

        var table = KernelRouteTable.Create(
            graph,
            PackageTransferLimits.Default.Validate(),
            hasProductionIdentity: false);

        Assert.True(table.IsFrozen);
        Assert.Equal(
            graph.Endpoints.Select(endpoint => endpoint.Descriptor.Name),
            table.Endpoints.Select(endpoint => endpoint.Descriptor.Name));
        Assert.All(table.Endpoints, endpoint => Assert.NotNull(endpoint.Access));
        Assert.All(
            table.Endpoints,
            endpoint => Assert.True(endpoint.Limits.MaxRequestBytes >= 0));
    }

    [Fact]
    public void Route_table_resolves_production_access_policies_and_routes()
    {
        var graph = BuiltInExtensionCatalog.Instance.Resolve(
            ServerProfiles.Production,
            hasProductionIdentity: true);

        var table = KernelRouteTable.Create(
            graph,
            PackageTransferLimits.Default.Validate(),
            hasProductionIdentity: true);

        var push = table.Endpoints.Single(
            endpoint => endpoint.Descriptor.PathTemplate == "/package");
        var unlist = table.Endpoints.Single(
            endpoint => endpoint.Descriptor.PathTemplate == "/package/{id}/{version}");
        Assert.Equal(Authentication.NuGetAccessKind.Publish, push.Access.Kind);
        Assert.Equal(Authentication.NuGetAccessKind.Unlist, unlist.Access.Kind);
        Assert.Contains(
            table.Endpoints,
            endpoint => endpoint.Descriptor.PathTemplate == "/package/{id}/{version}/hard");
    }

    [Fact]
    public void Test_profiles_keep_pre_production_access_policies()
    {
        var graph = BuiltInExtensionCatalog.Instance.Resolve(ServerProfiles.Embedded);

        var table = KernelRouteTable.Create(
            graph,
            PackageTransferLimits.Default.Validate(),
            hasProductionIdentity: false);

        var push = table.Endpoints.Single(
            endpoint => endpoint.Descriptor.PathTemplate == "/package");
        var unlist = table.Endpoints.Single(
            endpoint => endpoint.Descriptor.PathTemplate == "/package/{id}/{version}");
        Assert.Equal(Authentication.NuGetAccessKind.Write, push.Access.Kind);
        Assert.Equal(Authentication.NuGetAccessKind.Write, unlist.Access.Kind);
        Assert.DoesNotContain(
            table.Endpoints,
            endpoint => endpoint.Descriptor.PathTemplate == "/package/{id}/{version}/hard");
    }

    [Fact]
    public void Service_index_resources_must_resolve_to_generated_routes()
    {
        var catalog = Catalog(
            Manifest("extension.a", Endpoint("a.route", "GET", "/packages/{id}")) with
            {
                Resources =
                [
                    new ServiceResourceContribution(
                        "PackageBaseAddress",
                        "3.0.0",
                        new OperationId(OperationA),
                        "/missing/",
                        ServiceResourceVisibility.Advertised,
                        ServiceResourceAccess.Read,
                        [],
                        [],
                        null,
                        10,
                        ServiceResourceReadiness.Ready)
                ]
            });

        var exception = Assert.Throws<ServerHostingConfigurationException>(
            () => Resolve(catalog, "extension.a"));

        Assert.StartsWith(
            "catalog.missing-resource-route:",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Built_in_routes_match_the_documented_compatibility_inventory()
    {
        var embedded = BuiltInExtensionCatalog.Instance.Resolve(ServerProfiles.Embedded);

        Assert.Equal(
            ExpectedEmbeddedRoutes,
            embedded.Routes.Select(route => $"{route.Method} {route.Path}").ToArray());
    }

    [Fact]
    public void Production_routes_match_the_documented_compatibility_inventory()
    {
        var production = BuiltInExtensionCatalog.Instance.Resolve(
            ServerProfiles.Production,
            hasProductionIdentity: true);

        var routes = production.Routes.Select(route => $"{route.Method} {route.Path}").ToArray();

        Assert.DoesNotContain(
            routes,
            route => route.Contains("/__test/packages", StringComparison.Ordinal));
        Assert.Contains("DELETE /package/{id}/{version}/hard", routes);
        Assert.Contains("GET /__test/health", routes);
        Assert.Contains("GET /v3/index.json", routes);
    }

    private static readonly string[] ExpectedEmbeddedRoutes =
    [
        "DELETE /__test/faults",
        "DELETE /__test/packages/{id}/{version}",
        "DELETE /__test/requests",
        "DELETE /package/{id}/{version}",
        "GET /__admin/packages/{id}/{version}/validations",
        "GET /__admin/supply-chain/audit",
        "GET /__test/faults",
        "GET /__test/health",
        "GET /__test/packages",
        "GET /__test/requests",
        "GET /__test/state",
        "GET /flatcontainer/{id}/index.json",
        "GET /flatcontainer/{id}/{version}/{fileName}",
        "GET /health/live",
        "GET /health/ready",
        "GET /health/storage",
        "GET /query",
        "GET /registration/{id}/index.json",
        "GET /registration/{id}/page/{lower}/{upper}.json",
        "GET /registration/{id}/{version}.json",
        "GET /v3/index.json",
        "GET /v3/vulnerabilities/index.json",
        "GET /v3/vulnerabilities/{snapshotId}/{pageName}.json",
        "HEAD /flatcontainer/{id}/index.json",
        "HEAD /flatcontainer/{id}/{version}/{fileName}",
        "HEAD /query",
        "HEAD /registration/{id}/index.json",
        "HEAD /registration/{id}/page/{lower}/{upper}.json",
        "HEAD /registration/{id}/{version}.json",
        "HEAD /v3/index.json",
        "HEAD /v3/vulnerabilities/index.json",
        "HEAD /v3/vulnerabilities/{snapshotId}/{pageName}.json",
        "POST /__admin/packages/{id}/{version}/{action}",
        "POST /__test/faults",
        "POST /__test/packages",
        "POST /__test/packages/{id}/{version}/list",
        "POST /__test/packages/{id}/{version}/unlist",
        "POST /__test/reset",
        "PUT /__test/packages/{id}/{version}/metadata",
        "PUT /package",
        "PUT /symbolpackage"
    ];

    private static ResolvedExtensionGraph Resolve(ExtensionCatalog catalog, params string[] ids) =>
        catalog.ResolveWithTestContracts(
            new ServerProfile(
                "test",
                ServerProfileKind.Embedded,
                [.. ids.Select(id => new ExtensionSelection(id, []))],
                []));

    private static ExtensionCatalog Catalog(params ExtensionManifest[] manifests) => new(manifests);

    private static ExtensionManifest Manifest(
        string id,
        params EndpointDescriptor[] endpoints) =>
        new(
            1,
            id,
            new ExtensionVersion(1, 0, 0),
            ExtensionVersionRange.Major(1),
            [],
            [
                .. endpoints
                    .SelectMany(endpoint => endpoint.Operations)
                    .Select(operation => operation.OperationId)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
            ],
            [.. endpoints],
            [],
            []);

    private static EndpointDescriptor Endpoint(
        string name,
        string method,
        string path,
        string operationId = OperationA) =>
        TestEndpointDescriptors.Endpoint(name, method, path, operationId);
}