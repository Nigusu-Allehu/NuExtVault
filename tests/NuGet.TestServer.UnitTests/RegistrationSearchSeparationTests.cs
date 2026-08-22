using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;

namespace NuGet.TestServer.UnitTests;

public sealed class RegistrationSearchSeparationTests
{
    [Fact]
    public void Registration_and_search_have_independent_production_surfaces()
    {
        var registrationRoot = Path.Combine(
            RepositoryRoot,
            "src",
            "NuGet.TestServer.Extensions.Official",
            "Registration");
        var searchRoot = Path.Combine(
            RepositoryRoot,
            "src",
            "NuGet.TestServer.Extensions.Official",
            "Search");
        Assert.True(Directory.Exists(registrationRoot));
        Assert.True(Directory.Exists(searchRoot));
        Assert.DoesNotContain(
            EnumerateSource(registrationRoot),
            file => File.ReadAllText(file).Contains(
                "NuGet.TestServer.Kernel.Owners.Search",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            EnumerateSource(searchRoot),
            file => File.ReadAllText(file).Contains(
                "NuGet.TestServer.Kernel.Owners.Registration",
                StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(
            RepositoryRoot,
            "src",
            "NuGet.TestServer.Kernel",
            "Kernel",
            "Owners",
            "RegistrationSearchOperations.cs")));
    }

    [Fact]
    public void Contracts_and_endpoints_are_split_without_feature_owned_shared_contracts()
    {
        var abstractions = Path.Combine(
            RepositoryRoot,
            "src",
            "NuGet.TestServer.Extensions.Abstractions");
        var registration = Path.Combine(
            RepositoryRoot,
            "src",
            "NuGet.TestServer.Extensions.Official",
            "Registration");
        var searchRoot = Path.Combine(
            RepositoryRoot,
            "src",
            "NuGet.TestServer.Extensions.Official",
            "Search");
        var searchEndpoints = Path.Combine(searchRoot, "SearchEndpoints.cs");
        var neutralContracts = File.ReadAllText(
            Path.Combine(abstractions, "PackageMetadataContracts.cs"));
        var legacyContracts = File.ReadAllText(
            Path.Combine(abstractions, "ProtocolContracts.cs"));
        var legacyEndpoints = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "src",
                "NuGet.TestServer.Extensions.Official",
                "ServiceIndex",
                "ProtocolEndpoints.cs"));

        Assert.True(File.Exists(Path.Combine(abstractions, "RegistrationContracts.cs")));
        Assert.True(File.Exists(Path.Combine(abstractions, "SearchContracts.cs")));
        Assert.True(File.Exists(Path.Combine(registration, "RegistrationEndpoints.cs")));
        Assert.True(File.Exists(searchEndpoints));
        Assert.DoesNotContain("Registration", neutralContracts, StringComparison.Ordinal);
        Assert.DoesNotContain("Search", neutralContracts, StringComparison.Ordinal);
        Assert.DoesNotContain("GetRegistration", legacyContracts, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchRequest", legacyContracts, StringComparison.Ordinal);
        Assert.DoesNotContain("registration.", legacyEndpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("search.", legacyEndpoints, StringComparison.Ordinal);
    }

    [Fact]
    public void Registration_and_search_are_owned_by_their_official_modules()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        string[] registrationOperationIds =
        [
            OperationIds.RegistrationGetIndex,
            OperationIds.RegistrationGetPage,
            OperationIds.RegistrationGetLeaf
        ];
        string[] registrationRoutePaths =
        [
            "/registration/{id}/index.json",
            "/registration/{id}/page/{lower}/{upper}.json",
            "/registration/{id}/{version}.json"
        ];

        Assert.All(
            registrationOperationIds,
            operationId => Assert.Equal(
                "builtin.registration",
                host.Graph.Operations.Single(
                    operation => operation.OperationId == operationId).ExtensionId));
        Assert.Equal(
            SearchExtractionTests.SearchExtensionId,
            host.Graph.Operations.Single(
                operation => operation.OperationId == OperationIds.SearchQuery).ExtensionId);
        Assert.All(
            registrationRoutePaths,
            routePath => Assert.All(
                host.Graph.Routes.Where(route => route.Path == routePath),
                route => Assert.Equal("builtin.registration", route.ExtensionId)));
        Assert.All(
            host.Graph.Routes.Where(route => route.Path == "/query"),
            route => Assert.Equal(SearchExtractionTests.SearchExtensionId, route.ExtensionId));
    }

    private static IEnumerable<string> EnumerateSource(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories);

    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "NuGet.TestServer.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new InvalidOperationException("The repository root was not found.");
    }
}
