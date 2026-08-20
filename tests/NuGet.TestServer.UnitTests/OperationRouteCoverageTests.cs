using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;

namespace NuGet.TestServer.UnitTests;

public sealed class OperationRouteCoverageTests
{
    [Fact]
    public void Every_mapped_route_declares_the_operations_it_dispatches()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);

        foreach (var route in DescribeRoutes(host.Application))
        {
            Assert.NotEmpty(route.OperationIds);
            foreach (var operationId in route.OperationIds)
            {
                var registration = host.Registry.Find(operationId);
                Assert.NotNull(registration);
                var owner = host.Graph.Operations.Single(
                    operation => operation.OperationId == operationId);
                var routeOwner = host.Graph.Routes.Single(
                    resolved => resolved.Method == route.Method && resolved.Path == route.Path);
                Assert.Equal(routeOwner.ExtensionId, owner.ExtensionId);
                Assert.Equal(owner.ExtensionId, registration!.ExtensionId);
            }
        }
    }

    [Fact]
    public void Mapped_routes_match_the_resolved_extension_graph()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);

        var mapped = DescribeRoutes(host.Application)
            .Select(route => $"{route.Method} {route.Path}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var declared = host.Graph.Routes
            .Select(route => $"{route.Method} {route.Path}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, mapped);
    }

    [Fact]
    public void Every_active_operation_is_routed_or_declared_as_a_non_routed_operation()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);

        var routed = DescribeRoutes(host.Application)
            .SelectMany(route => route.OperationIds)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var active = host.Graph.Operations
            .Select(operation => operation.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        var nonRouted = KernelOperationRoutes.NonRoutedOperations.ToHashSet(StringComparer.Ordinal);
        var productionOnly = KernelOperationRoutes.ProductionOnlyRoutedOperations
            .ToHashSet(StringComparer.Ordinal);

        Assert.Empty(routed.Except(active, StringComparer.Ordinal));
        Assert.Empty(
            active
                .Except(routed, StringComparer.Ordinal)
                .Except(nonRouted, StringComparer.Ordinal)
                .Except(productionOnly, StringComparer.Ordinal));
        Assert.Empty(routed.Intersect(nonRouted, StringComparer.Ordinal));
        Assert.All(
            KernelOperationRoutes.NonRoutedOperations.Concat(
                KernelOperationRoutes.ProductionOnlyRoutedOperations),
            operationId => Assert.Contains(
                OperationContracts.All,
                contract => contract.Id.Value == operationId));
    }

    [Fact]
    public void Production_hosts_do_not_map_test_control_routes_or_owners()
    {
        using var host = TestServerApplication.BuildProduction();

        var routes = DescribeRoutes(host.Application);

        Assert.DoesNotContain(
            routes,
            route => route.Path.StartsWith("/__test/packages", StringComparison.Ordinal));
        Assert.DoesNotContain(
            routes.SelectMany(route => route.OperationIds),
            operationId => operationId.StartsWith("NuTest.Control.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            host.Registry.Registrations,
            registration => registration.Id.Value.StartsWith("NuTest.Control.", StringComparison.Ordinal));
        Assert.Contains(
            routes,
            route => route.Method == "DELETE" && route.Path == "/package/{id}/{version}/hard");
    }

    private static IReadOnlyList<RouteDescription> DescribeRoutes(WebApplication application) =>
        [
            .. ((IEndpointRouteBuilder)application).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .SelectMany(endpoint =>
                    (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                    .Select(method => new RouteDescription(
                        method,
                        endpoint.RoutePattern.RawText ?? string.Empty,
                        endpoint.Metadata.GetMetadata<OperationRouteMetadata>()?.OperationIds ?? [])))
        ];

    private sealed record RouteDescription(
        string Method,
        string Path,
        IReadOnlyList<string> OperationIds);
}
