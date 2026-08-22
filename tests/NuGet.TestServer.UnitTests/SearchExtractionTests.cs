using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Packages;
using NuGet.TestServer.Extensions.Official;

namespace NuGet.TestServer.UnitTests;

/// <summary>
/// Step 15 extraction gates. Search is contributed through the generic module seam,
/// while indexed querying and authoritative visibility remain kernel capabilities.
/// </summary>
public sealed class SearchExtractionTests
{
    internal const string SearchExtensionId = "builtin.search";
    private const string RegistrationExtensionId = "builtin.registration";
    private const string ProtocolExtensionId = "builtin.protocol";
    private const string SearchCapability = "packages.search.query";

    [Fact]
    public void Search_operation_route_and_resources_have_exactly_one_search_owner()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);

        var operation = Assert.Single(
            host.Graph.Operations,
            candidate => candidate.OperationId == OperationIds.SearchQuery);
        var registration = host.Registry.Find(OperationIds.SearchQuery);
        var routes = host.Graph.Routes.Where(route => route.Path == "/query").ToArray();
        var resources = host.Graph.Resources
            .Where(resource => resource.Contribution.ResourceType == "SearchQueryService")
            .OrderBy(resource => resource.Contribution.Version, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(SearchExtensionId, operation.ExtensionId);
        Assert.NotNull(registration);
        Assert.Equal(SearchExtensionId, registration!.ExtensionId);
        Assert.Equal(2, routes.Length);
        Assert.All(routes, route => Assert.Equal(SearchExtensionId, route.ExtensionId));
        Assert.Equal(["3.0.0-beta", "3.5.0"], resources
            .Select(resource => resource.Contribution.Version));
        Assert.All(resources, resource => Assert.Equal(SearchExtensionId, resource.ExtensionId));

        Assert.All(
            new[]
            {
                OperationIds.RegistrationGetIndex,
                OperationIds.RegistrationGetPage,
                OperationIds.RegistrationGetLeaf
            },
            operationId => Assert.Equal(
                RegistrationExtensionId,
                host.Registry.Find(operationId)!.ExtensionId));
    }

    [Fact]
    public void Search_module_preserves_typed_body_free_route_and_resource_dependencies()
    {
        var manifest = Assert.Single(
            BuiltInExtensionCatalog.Manifests,
            candidate => candidate.Id == SearchExtensionId);
        var endpoint = Assert.Single(manifest.Endpoints);

        Assert.Equal([OperationIds.SearchQuery], manifest.Operations.ToArray());
        Assert.Equal("search.query", endpoint.Name);
        Assert.Equal("/query", endpoint.PathTemplate);
        Assert.Equal(["GET", "HEAD"], endpoint.Methods.ToArray());
        Assert.Equal(EndpointHeadPolicy.MirrorsGet, endpoint.Head);
        Assert.Equal(EndpointBodyBinding.None, endpoint.Body);
        Assert.Equal(EndpointAccessKind.Read, endpoint.Access.Default);
        Assert.Equal(EndpointLimits.BodyFree, endpoint.Limits);
        Assert.Equal(
            ["packageType", "prerelease", "q", "skip", "take"],
            endpoint.QueryParameters.Select(parameter => parameter.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                ("SearchQueryService", "3.0.0-beta", 30),
                ("SearchQueryService", "3.5.0", 40)
            ],
            manifest.Resources
                .Select(resource => (
                    resource.ResourceType,
                    resource.Version,
                    resource.Order)));
        Assert.All(
            manifest.Resources,
            resource => Assert.Equal(
                ["PackageBaseAddress/3.0.0", "RegistrationsBaseUrl/3.6.0"],
                resource.RequiresResourceTypes.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void Search_requests_only_the_indexed_query_capability()
    {
        var search = Assert.Single(
            BuiltInExtensionCatalog.Manifests,
            candidate => candidate.Id == SearchExtensionId);
        var protocol = Assert.Single(
            BuiltInExtensionCatalog.Manifests,
            candidate => candidate.Id == ProtocolExtensionId);

        var request = Assert.Single(search.RequestedCapabilities);
        Assert.Equal(SearchCapability, request.Name);
        Assert.True(request.IsRequired);
        Assert.DoesNotContain(
            protocol.RequestedCapabilities,
            capability => capability.Name == SearchCapability);
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("standard")]
    [InlineData("production")]
    public void Every_profile_selects_search_and_grants_its_capability(string profileName)
    {
        var profile = profileName switch
        {
            "embedded" => ServerProfiles.Embedded,
            "standard" => ServerProfiles.Standard,
            _ => ServerProfiles.Production
        };

        Assert.Contains(profile.Extensions, extension => extension.Id == SearchExtensionId);
        Assert.Contains(profile.Grants, grant => grant.Name == SearchCapability);
    }

    [Fact]
    public void Kernel_composition_never_names_the_search_extension()
    {
        var extensionRoot = Path.Combine(
            ExtensionModuleFitnessTests.RepositoryRoot,
            "src",
            "NuGet.TestServer.Extensions.Official",
            "Search");
        var moduleList = Path.Combine(
            ExtensionModuleFitnessTests.RepositoryRoot,
            "src",
            "NuGet.TestServer.Extensions.Official",
            "OfficialExtensionModules.cs");
        var pattern = new Regex(
            Regex.Escape(SearchExtensionId) + "|SearchModule|SearchOperations",
            RegexOptions.CultureInvariant);

        var offenders = Directory
            .EnumerateFiles(
                Path.Combine(ExtensionModuleFitnessTests.RepositoryRoot, "src"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(file =>
                !file.StartsWith(extensionRoot, StringComparison.OrdinalIgnoreCase) &&
                !file.Equals(moduleList, StringComparison.OrdinalIgnoreCase) &&
                !file.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) &&
                !file.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            .Where(file => pattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(ExtensionModuleFitnessTests.RepositoryRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
        Assert.True(Directory.Exists(extensionRoot));
    }

    [Fact]
    public async Task Search_queries_are_audited_against_the_extracted_owner()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var store = host.Services.GetRequiredService<IPackageStore>();
        await store.AddAsync(TestPackageBuilder.Create("Audited.Search", "1.0.0").Build());

        await host.Services.GetRequiredService<OperationDispatcher>()
            .DispatchAsync<SearchRequest, SearchResponse>(
                new OperationId(OperationIds.SearchQuery),
                new SearchRequest("audited", 0, 20, false, null),
                new OperationExecutionContext("search-extraction-test"),
                CancellationToken.None);

        Assert.Contains(
            host.Services.GetRequiredService<CapabilityAuditLog>().Entries,
            entry => entry.OwnerId == SearchExtensionId &&
                     entry.OperationId == OperationIds.SearchQuery &&
                     entry.CapabilityName == SearchCapability &&
                     entry.Action == "query" &&
                     entry.Outcome == CapabilityCallOutcome.Succeeded);
    }

    [Fact]
    public async Task Search_queries_honor_cancellation_before_touching_package_state()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.Services.GetRequiredService<OperationDispatcher>()
                .DispatchAsync<SearchRequest, SearchResponse>(
                    new OperationId(OperationIds.SearchQuery),
                    new SearchRequest(string.Empty, 0, 20, false, null),
                    new OperationExecutionContext("search-extraction-test"),
                    cancellation.Token)
                .AsTask());
    }
}
