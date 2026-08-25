using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NuExtVault.Extensions;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Hosting;
using NuExtVault.Kernel;
using NuExtVault.Kernel.Capabilities;
using NuExtVault.Packages;
using NuExtVault.Extensions.Official;

namespace NuExtVault.UnitTests;

/// <summary>
/// Step 17 extraction gates. Package-management workflows are module-owned while the
/// kernel remains the only component that can authorize and commit package mutations.
/// </summary>
public sealed class PackageManagementExtractionTests
{
    private const string ExtensionId = "builtin.package-management";

    private static readonly string[] Operations =
    [
        OperationIds.PackageManagementDelete,
        OperationIds.PackageManagementList,
        OperationIds.PackageManagementPush,
        OperationIds.PackageManagementPushSymbols,
        OperationIds.PackageManagementRelist,
        OperationIds.PackageManagementUnlist
    ];

    [Fact]
    public void Package_management_operations_have_exactly_one_official_module_owner()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);

        Assert.All(
            Operations,
            operationId =>
            {
                Assert.Equal(
                    ExtensionId,
                    Assert.Single(
                        host.Graph.Operations,
                        operation => operation.OperationId == operationId).ExtensionId);
                Assert.Equal(ExtensionId, host.Registry.Find(operationId)!.ExtensionId);
            });
    }

    [Fact]
    public void Package_management_routes_and_resources_move_with_the_module_verbatim()
    {
        using var host = TestServerApplication.BuildProduction();
        var manifest = Assert.Single(
            OfficialExtensionModules.Manifests,
            candidate => candidate.Id == ExtensionId);

        Assert.Equal(Operations, manifest.OwnedOperations.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["publication.delete", "publication.push", "publication.push-symbols", "publication.unlist"],
            manifest.Endpoints.Select(endpoint => endpoint.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "DELETE /package/{id}/{version}",
                "DELETE /package/{id}/{version}/hard",
                "PUT /package",
                "PUT /symbolpackage"
            ],
            host.Graph.Routes
                .Where(route =>
                    route.Path.StartsWith("/package", StringComparison.Ordinal) ||
                    route.Path == "/symbolpackage")
                .Select(route => $"{route.Method} {route.Path}")
                .Order(StringComparer.Ordinal));
        Assert.All(
            host.Graph.Routes.Where(route =>
                route.Path.StartsWith("/package", StringComparison.Ordinal) ||
                route.Path == "/symbolpackage"),
            route => Assert.Equal(ExtensionId, route.ExtensionId));
        Assert.Equal(
            [("PackagePublish", "2.0.0", "/package"), ("SymbolPackagePublish", "4.9.0", "/symbolpackage")],
            manifest.Resources.Select(resource => (
                resource.ResourceType,
                resource.Version,
                resource.RouteName)));

        Assert.DoesNotContain(
            BuiltInExtensionCatalog.Manifests,
            candidate => candidate.Id == "builtin.publication");
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("standard")]
    [InlineData("production")]
    public void Every_profile_selects_package_management_and_grants_only_action_capabilities(
        string profileName)
    {
        var profile = profileName switch
        {
            "embedded" => ServerProfiles.Embedded,
            "standard" => ServerProfiles.Standard,
            _ => ServerProfiles.Production
        };
        var module = Assert.Single(
            OfficialExtensionModules.All,
            candidate => candidate.Contribution.Manifest.Id == ExtensionId);
        var requested = module.Contribution.Manifest.RequestedCapabilities;

        Assert.Contains(profile.Extensions, extension => extension.Id == ExtensionId);
        Assert.Equal(
            [
                "packages.content.write-staged",
                "packages.delete",
                "packages.metadata.read",
                "packages.publish",
                "packages.relist",
                "packages.unlist"
            ],
            requested.Select(capability => capability.Name).Order(StringComparer.Ordinal));
        Assert.All(requested, capability => Assert.True(capability.IsRequired));
        Assert.All(
            requested,
            capability => Assert.Contains(profile.Grants, grant => grant.Name == capability.Name));
        Assert.DoesNotContain(
            requested,
            capability => capability.Name == BuiltInCapabilityNames.EventsPublish);
    }

    [Fact]
    public void Kernel_composition_has_no_package_management_specific_owner_branch()
    {
        var extensionRoot = Path.Combine(
            ExtensionModuleFitnessTests.RepositoryRoot,
            "src",
            "NuExtVault.Extensions.Official",
            "PackageManagement");
        var moduleList = Path.Combine(
            ExtensionModuleFitnessTests.RepositoryRoot,
            "src",
            "NuExtVault.Extensions.Official",
            "OfficialExtensionModules.cs");
        var pattern = new Regex(
            Regex.Escape(ExtensionId) +
            "|PackageManagementModule|PackageManagementOperations|PublicationOperations|" +
            "BuiltInExtensionIds\\.Publication",
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
    public void Publication_outcome_numeric_values_preserve_the_wire_contract()
    {
        Assert.Equal(0, (int)PublicationOutcome.Published);
        Assert.Equal(1, (int)PublicationOutcome.Quarantined);
        Assert.Equal(2, (int)PublicationOutcome.Rejected);
        Assert.Equal(3, (int)PublicationOutcome.Duplicate);
        Assert.Equal(4, (int)PublicationOutcome.Conflict);
        Assert.Equal(5, (int)PublicationOutcome.Unauthorized);
        Assert.Equal(6, (int)PublicationOutcome.QuotaExceeded);
    }

    [Fact]
    public void Package_management_capabilities_do_not_mirror_operation_contracts()
    {
        Type[] capabilities =
        [
            typeof(IPackagePushCapability),
            typeof(IPackageSymbolsPushCapability),
            typeof(IPackageManagementListCapability),
            typeof(IPackageUnlistCapability),
            typeof(IPackageRelistCapability),
            typeof(IPackageDeleteCapability)
        ];
        Type[] operationContracts =
        [
            typeof(PushPackageRequest),
            typeof(PushPackageResponse),
            typeof(PushSymbolsRequest),
            typeof(PushSymbolsResponse),
            typeof(ListPackagesRequest),
            typeof(ListPackagesResponse),
            typeof(UnlistPackageRequest),
            typeof(UnlistPackageResponse),
            typeof(RelistPackageRequest),
            typeof(RelistPackageResponse),
            typeof(DeletePackageRequest),
            typeof(DeletePackageResponse)
        ];

        Assert.All(
            capabilities.SelectMany(capability => capability.GetMethods()),
            method =>
            {
                Assert.DoesNotContain(
                    method.GetParameters(),
                    parameter => operationContracts.Contains(parameter.ParameterType));
                Assert.DoesNotContain(
                    operationContracts,
                    contract => ContainsType(method.ReturnType, contract));
            });
    }

    [Fact]
    public void Package_management_operation_contracts_do_not_expose_kernel_authority_facts()
    {
        Assert.Equal(
            [nameof(PushPackageRequest.Content)],
            typeof(PushPackageRequest).GetProperties().Select(property => property.Name));
        Assert.Equal(
            [nameof(UnlistPackageRequest.Package)],
            typeof(UnlistPackageRequest).GetProperties().Select(property => property.Name));
        Assert.Equal(
            [nameof(RelistPackageRequest.Package)],
            typeof(RelistPackageRequest).GetProperties().Select(property => property.Name));
        Assert.Equal(
            [nameof(DeletePackageRequest.Package), nameof(DeletePackageRequest.Reason)],
            typeof(DeletePackageRequest).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task Authoritative_mutations_are_audited_against_the_extracted_owner()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var package = new PackageIdentity("Audited.Management", "1.0.0");

        await host.Services.GetRequiredService<OperationDispatcher>()
            .DispatchAsync<UnlistPackageRequest, UnlistPackageResponse>(
                new OperationId(OperationIds.PackageManagementUnlist),
                new UnlistPackageRequest(package),
                new OperationExecutionContext("package-management-extraction-test"),
                CancellationToken.None);

        Assert.Contains(
            host.Services.GetRequiredService<CapabilityAuditLog>().Entries,
            entry => entry.OwnerId == ExtensionId &&
                     entry.OperationId == OperationIds.PackageManagementUnlist &&
                     entry.CapabilityName == BuiltInCapabilityNames.PackagesUnlist);
    }

    [Fact]
    public async Task Listing_and_delete_mutations_are_immediately_consistent_across_all_read_modules()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var identity = new PackageIdentity("Consistent.Management", "1.0.0");
        await host.Services.GetRequiredService<PackageSupplyChainService>()
            .AddAsync(TestPackageBuilder.Create(identity.Id, identity.Version).Build());

        await DispatchAsync<UnlistPackageRequest, UnlistPackageResponse>(
            host,
            OperationIds.PackageManagementUnlist,
            new UnlistPackageRequest(identity));

        var versions = await DispatchAsync<GetPackageVersionsRequest, GetPackageVersionsResponse>(
            host,
            OperationIds.FlatContainerGetVersions,
            new GetPackageVersionsRequest(identity.Id));
        var registration =
            await DispatchAsync<GetRegistrationLeafRequest, GetRegistrationLeafResponse>(
                host,
                OperationIds.RegistrationGetLeaf,
                new GetRegistrationLeafRequest(identity));
        var hiddenSearch = await DispatchAsync<SearchRequest, SearchResponse>(
            host,
            OperationIds.SearchQuery,
            new SearchRequest(identity.Id, 0, 20, false, null));

        Assert.Equal([identity.Version], versions.Value!.Versions.ToArray());
        Assert.False(registration.Value!.Leaf.Listed);
        Assert.Empty(hiddenSearch.Value!.Data);

        await DispatchAsync<RelistPackageRequest, RelistPackageResponse>(
            host,
            OperationIds.PackageManagementRelist,
            new RelistPackageRequest(identity));
        var visibleSearch = await DispatchAsync<SearchRequest, SearchResponse>(
            host,
            OperationIds.SearchQuery,
            new SearchRequest(identity.Id, 0, 20, false, null));
        Assert.Single(visibleSearch.Value!.Data);

        await DispatchAsync<DeletePackageRequest, DeletePackageResponse>(
            host,
            OperationIds.PackageManagementDelete,
            new DeletePackageRequest(identity, "test cleanup"));

        versions = await DispatchAsync<GetPackageVersionsRequest, GetPackageVersionsResponse>(
            host,
            OperationIds.FlatContainerGetVersions,
            new GetPackageVersionsRequest(identity.Id));
        registration = await DispatchAsync<GetRegistrationLeafRequest, GetRegistrationLeafResponse>(
            host,
            OperationIds.RegistrationGetLeaf,
            new GetRegistrationLeafRequest(identity));
        var deletedSearch = await DispatchAsync<SearchRequest, SearchResponse>(
            host,
            OperationIds.SearchQuery,
            new SearchRequest(identity.Id, 0, 20, false, null));

        Assert.Equal(OperationErrorKind.NotFound, versions.Error!.Kind);
        Assert.Equal(OperationErrorKind.NotFound, registration.Error!.Kind);
        Assert.Empty(deletedSearch.Value!.Data);
    }

    private static ValueTask<OperationResponse<TResponse>> DispatchAsync<TRequest, TResponse>(
        TestServerApplication host,
        string operationId,
        TRequest request) =>
        host.Services.GetRequiredService<OperationDispatcher>()
            .DispatchAsync<TRequest, TResponse>(
                new OperationId(operationId),
                request,
                new OperationExecutionContext("package-management-extraction-test"),
                CancellationToken.None);

    private static bool ContainsType(Type candidate, Type expected) =>
        candidate == expected ||
        (candidate.IsGenericType &&
         candidate.GetGenericArguments().Any(argument => ContainsType(argument, expected)));
}
