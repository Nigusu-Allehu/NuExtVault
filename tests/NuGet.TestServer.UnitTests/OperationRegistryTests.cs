using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;

namespace NuGet.TestServer.UnitTests;

public sealed class OperationRegistryTests
{
    [Fact]
    public void Every_active_operation_has_exactly_one_owner()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var graph = host.Graph;
        var registry = host.Registry;

        Assert.Equal(
            graph.Operations.Select(operation => operation.OperationId).ToArray(),
            registry.Registrations.Select(registration => registration.Id.Value).ToArray());
        Assert.Equal(
            graph.Operations.Select(operation => operation.ExtensionId).ToArray(),
            registry.Registrations.Select(registration => registration.ExtensionId).ToArray());
        Assert.Equal(
            registry.Registrations.Length,
            registry.Registrations.Select(registration => registration.Id.Value).Distinct().Count());
    }

    [Fact]
    public void Every_registration_matches_the_declared_operation_contract()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);

        foreach (var registration in host.Registry.Registrations)
        {
            var binding = OperationContracts.Bindings.Single(
                candidate => candidate.Contract.Id == registration.Id);
            Assert.Equal(binding.RequestType, registration.RequestType);
            Assert.Equal(binding.ResponseType, registration.ResponseType);
        }
    }

    [Fact]
    public void Registration_order_is_stable_regardless_of_registration_sequence()
    {
        var graph = Graph(
            ("NuGet.Search.Query", BuiltInExtensionIds.Protocol),
            ("NuGet.ServiceIndex.Get", BuiltInExtensionIds.Protocol),
            ("NuGet.FlatContainer.GetHash", BuiltInExtensionIds.Protocol));

        var forward = new OperationRegistryBuilder()
            .Register(BuiltInExtensionIds.Protocol, SearchOwner())
            .Register(BuiltInExtensionIds.Protocol, ServiceIndexOwner())
            .Register(BuiltInExtensionIds.Protocol, HashOwner())
            .Build(graph);
        var reverse = new OperationRegistryBuilder()
            .Register(BuiltInExtensionIds.Protocol, HashOwner())
            .Register(BuiltInExtensionIds.Protocol, ServiceIndexOwner())
            .Register(BuiltInExtensionIds.Protocol, SearchOwner())
            .Build(graph);

        Assert.Equal(
            ["NuGet.FlatContainer.GetHash", "NuGet.Search.Query", "NuGet.ServiceIndex.Get"],
            forward.Registrations.Select(registration => registration.Id.Value).ToArray());
        Assert.Equal(
            forward.Registrations.Select(registration => registration.Id.Value).ToArray(),
            reverse.Registrations.Select(registration => registration.Id.Value).ToArray());
        Assert.Equal(forward.Diagnostics, reverse.Diagnostics);
    }

    [Fact]
    public void Duplicate_ownership_fails_before_listening()
    {
        var graph = Graph(("NuGet.Search.Query", BuiltInExtensionIds.Protocol));

        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            new OperationRegistryBuilder()
                .Register(BuiltInExtensionIds.Protocol, SearchOwner())
                .Register(BuiltInExtensionIds.Protocol, SearchOwner())
                .Build(graph));

        Assert.StartsWith("registry.duplicate-owner:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("NuGet.Search.Query", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_owner_fails_before_listening()
    {
        var graph = Graph(
            ("NuGet.Search.Query", BuiltInExtensionIds.Protocol),
            ("NuGet.ServiceIndex.Get", BuiltInExtensionIds.Protocol));

        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            new OperationRegistryBuilder()
                .Register(BuiltInExtensionIds.Protocol, SearchOwner())
                .Build(graph));

        Assert.StartsWith("registry.missing-owner:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("NuGet.ServiceIndex.Get", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_operation_identifiers_fail_before_listening()
    {
        var graph = Graph(("NuGet.Search.Query", BuiltInExtensionIds.Protocol));
        var owner = new DelegateOperationOwner<SearchRequest, SearchResponse>(
            "NuGet.Search.Unknown",
            (_, _, _) => ValueTask.FromResult(
                OperationResponse<SearchResponse>.Success(new SearchResponse(0, []))));

        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            new OperationRegistryBuilder()
                .Register(BuiltInExtensionIds.Protocol, owner)
                .Build(graph));

        Assert.StartsWith("registry.unknown-operation:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("NuGet.Search.Unknown", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_mismatches_fail_before_listening()
    {
        var graph = Graph(("NuGet.Search.Query", BuiltInExtensionIds.Protocol));
        var owner = new DelegateOperationOwner<SearchRequest, GetPackageHashResponse>(
            "NuGet.Search.Query",
            (_, _, _) => ValueTask.FromResult(
                OperationResponse<GetPackageHashResponse>.Success(
                    new GetPackageHashResponse("hash"))));

        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            new OperationRegistryBuilder()
                .Register(BuiltInExtensionIds.Protocol, owner)
                .Build(graph));

        Assert.StartsWith("registry.contract-mismatch:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("NuGet.Search.Query", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ownership_claimed_by_a_different_extension_fails_before_listening()
    {
        var graph = Graph(("NuGet.Search.Query", BuiltInExtensionIds.Protocol));

        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            new OperationRegistryBuilder()
                .Register(BuiltInExtensionIds.TestControl, SearchOwner())
                .Build(graph));

        Assert.StartsWith("registry.owner-mismatch:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Operations_outside_the_resolved_graph_fail_before_listening()
    {
        var graph = Graph(("NuGet.Search.Query", BuiltInExtensionIds.Protocol));

        var exception = Assert.Throws<ServerHostingConfigurationException>(() =>
            new OperationRegistryBuilder()
                .Register(BuiltInExtensionIds.Protocol, SearchOwner())
                .Register(BuiltInExtensionIds.Protocol, ServiceIndexOwner())
                .Build(graph));

        Assert.StartsWith("registry.inactive-operation:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("NuGet.ServiceIndex.Get", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Registries_are_isolated_per_host_instance_and_profile()
    {
        using var embedded = TestServerApplication.Build(ServerProfiles.Embedded);
        using var second = TestServerApplication.Build(ServerProfiles.Embedded);
        using var production = TestServerApplication.BuildProduction();

        Assert.NotSame(embedded.Registry, second.Registry);
        Assert.Equal(
            embedded.Registry.Registrations.Select(registration => registration.Id.Value).ToArray(),
            second.Registry.Registrations.Select(registration => registration.Id.Value).ToArray());
        Assert.Contains(
            embedded.Registry.Registrations,
            registration => registration.Id.Value == "NuTest.Control.GetState");
        Assert.DoesNotContain(
            production.Registry.Registrations,
            registration => registration.Id.Value == "NuTest.Control.GetState");
        Assert.Contains(
            production.Registry.Registrations,
            registration => registration.Id.Value == "NuGet.PackageManagement.Push");
    }

    private static ResolvedExtensionGraph Graph(params (string OperationId, string ExtensionId)[] operations) =>
        new(
            "test",
            [],
            [
                .. operations
                    .Select(operation => new ResolvedOperation(operation.OperationId, operation.ExtensionId))
                    .OrderBy(operation => operation.OperationId, StringComparer.Ordinal)
            ],
            [],
            [],
            [],
            [],
            "profile=test\n");

    private static DelegateOperationOwner<SearchRequest, SearchResponse> SearchOwner() =>
        new(
            "NuGet.Search.Query",
            (_, _, _) => ValueTask.FromResult(
                OperationResponse<SearchResponse>.Success(new SearchResponse(0, []))));

    private static DelegateOperationOwner<GetServiceIndexRequest, GetServiceIndexResponse>
        ServiceIndexOwner() =>
        new(
            "NuGet.ServiceIndex.Get",
            (_, _, _) => ValueTask.FromResult(
                OperationResponse<GetServiceIndexResponse>.Success(
                    new GetServiceIndexResponse("3.0.0", ImmutableArray<ServiceResourceDescriptor>.Empty))));

    private static DelegateOperationOwner<GetPackageHashRequest, GetPackageHashResponse> HashOwner() =>
        new(
            "NuGet.FlatContainer.GetHash",
            (_, _, _) => ValueTask.FromResult(
                OperationResponse<GetPackageHashResponse>.Success(new GetPackageHashResponse("hash"))));
}
