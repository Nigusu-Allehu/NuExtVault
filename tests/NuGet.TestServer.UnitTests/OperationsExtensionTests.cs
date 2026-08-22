using Microsoft.Extensions.DependencyInjection;
using NuGet.TestServer.Extensions;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Extensions.Official;

namespace NuGet.TestServer.UnitTests;

public sealed class OperationsExtensionTests
{
    private static readonly string[] OperationIds =
    [
        "NuTest.Backup.Create",
        "NuTest.Diagnostics.Get",
        "NuTest.Health.GetLiveness",
        "NuTest.Health.GetReadiness",
        "NuTest.Health.GetStorage",
        "NuTest.Restore.Execute"
    ];

    [Fact]
    public void Operations_are_contributed_only_through_the_generic_official_module_seam()
    {
        var module = Assert.Single(
            OfficialExtensionModules.All,
            candidate => candidate.Contribution.Manifest.Id == BuiltInExtensionIds.Operations);

        Assert.Equal(OperationIds, module.Contribution.Manifest.OwnedOperations.Order().ToArray());
        Assert.Equal(
            ["health.live", "health.live-legacy", "health.ready", "health.storage"],
            module.Contribution.Manifest.Endpoints
                .Select(endpoint => endpoint.Name)
                .Order()
                .ToArray());

        var ownerComposition = File.ReadAllText(Path.Combine(
            ExtensionModuleFitnessTests.RepositoryRoot,
            "src",
            "NuGet.TestServer",
            "Hosting",
            "BuiltInOperationOwners.cs"));
        var catalog = File.ReadAllText(Path.Combine(
            ExtensionModuleFitnessTests.RepositoryRoot,
            "src",
            "NuGet.TestServer.Kernel",
            "Hosting",
            "ExtensionCatalog.cs"));

        Assert.DoesNotContain("ServerOperationsOperations", ownerComposition, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Manifest(\n            BuiltInExtensionIds.Operations",
            catalog.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("standard")]
    [InlineData("production")]
    public void Every_profile_has_exactly_one_operations_owner_and_generated_route_set(
        string profileName)
    {
        var profile = profileName switch
        {
            "embedded" => ServerProfiles.Embedded,
            "standard" => ServerProfiles.Standard,
            "production" => ServerProfiles.Production,
            _ => throw new InvalidOperationException()
        };
        var graph = BuiltInExtensionCatalog.Instance.Resolve(
            profile,
            hasProductionIdentity: profile == ServerProfiles.Production);

        Assert.All(
            OperationIds,
            operationId =>
            {
                var operation = Assert.Single(
                    graph.Operations,
                    candidate => candidate.OperationId == operationId);
                Assert.Equal(BuiltInExtensionIds.Operations, operation.ExtensionId);
            });
        Assert.Equal(
            [
                "GET /__test/health",
                "GET /health/live",
                "GET /health/ready",
                "GET /health/storage"
            ],
            graph.Routes
                .Where(route => route.ExtensionId == BuiltInExtensionIds.Operations)
                .Select(route => $"{route.Method} {route.Path}")
                .Order()
                .ToArray());
    }

    [Fact]
    public async Task Operational_calls_are_attributed_to_the_extracted_owner_and_preserve_cancellation()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        await host.Services.GetRequiredService<OperationDispatcher>()
            .DispatchAsync<GetLivenessRequest, GetLivenessResponse>(
                new OperationId("NuTest.Health.GetLiveness"),
                new GetLivenessRequest(),
                new OperationExecutionContext("operations-audit"),
                CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var query = host.Services.GetRequiredService<CapabilityBroker>()
            .ForOwner(BuiltInExtensionIds.Operations)
            .GetRequired<IOperationsQueryCapability>(BuiltInCapabilityNames.OperationsQuery);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => query.GetReadinessAsync(cancellation.Token).AsTask());

        var audit = host.Services.GetRequiredService<CapabilityAuditLog>().Entries;
        Assert.DoesNotContain(
            audit,
            entry => entry.OwnerId == BuiltInExtensionIds.Operations &&
                     entry.OperationId == "NuTest.Health.GetLiveness" &&
                     entry.CapabilityName == BuiltInCapabilityNames.OperationsQuery &&
                     entry.Action == "liveness");
        Assert.Contains(
            audit,
            entry => entry.OwnerId == BuiltInExtensionIds.Operations &&
                     entry.CapabilityName == BuiltInCapabilityNames.OperationsQuery &&
                     entry.Action == "readiness" &&
                     entry.Outcome == CapabilityCallOutcome.Cancelled);
    }
}
