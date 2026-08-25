using System.Text.RegularExpressions;
using NuExtVault.Extensions;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Hosting;
using NuExtVault.Kernel;
using NuExtVault.Extensions.Official;

namespace NuExtVault.UnitTests;

public sealed class RegistrationExtractionTests
{
    private const string ExtensionId = "builtin.registration";

    private static readonly string[] Operations =
    [
        "NuGet.Registration.GetIndex",
        "NuGet.Registration.GetLeaf",
        "NuGet.Registration.GetPage"
    ];

    [Fact]
    public void Registration_has_exactly_one_official_module_owner()
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
    public void Registration_routes_resources_and_dependencies_move_with_the_module()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var manifest = Assert.Single(
            OfficialExtensionModules.Manifests,
            candidate => candidate.Id == ExtensionId);

        Assert.Equal(Operations, manifest.OwnedOperations.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["registration.index", "registration.leaf", "registration.page"],
            manifest.Endpoints.Select(endpoint => endpoint.Name).Order(StringComparer.Ordinal));
        Assert.All(
            host.Graph.Routes.Where(route => route.Path.StartsWith(
                "/registration/",
                StringComparison.Ordinal)),
            route => Assert.Equal(ExtensionId, route.ExtensionId));
        var resource = Assert.Single(
            host.Graph.Resources,
            item => item.Contribution.AdvertisedType == "RegistrationsBaseUrl/3.6.0");
        Assert.Equal(ExtensionId, resource.ExtensionId);
        Assert.Equal("/registration/", resource.Contribution.RouteName);
        Assert.Equal(
            ["PackageBaseAddress/3.0.0"],
            resource.Contribution.ProducesUrlsFor.ToArray());
        Assert.Equal(
            ["PackageBaseAddress/3.0.0"],
            resource.Contribution.RequiresResourceTypes.ToArray());

        var protocol = Assert.Single(
            BuiltInExtensionCatalog.Manifests,
            candidate => candidate.Id == BuiltInExtensionIds.Protocol);
        Assert.DoesNotContain(
            protocol.OwnedOperations,
            operation => operation.StartsWith("NuGet.Registration.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            protocol.Endpoints,
            endpoint => endpoint.PathTemplate.StartsWith(
                "/registration/",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            protocol.Resources,
            item => item.ResourceType == "RegistrationsBaseUrl");
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("standard")]
    [InlineData("production")]
    public void Every_profile_selects_registration_and_grants_only_its_narrow_capabilities(
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
            ["extension-state.vulnerabilities.read", "packages.metadata.read"],
            requested.Select(capability => capability.Name).Order(StringComparer.Ordinal));
        Assert.All(requested, capability => Assert.True(capability.IsRequired));
        Assert.All(
            requested,
            capability => Assert.Contains(
                profile.Grants,
                grant => grant.Name == capability.Name));
    }

    [Fact]
    public void Kernel_composition_has_no_registration_specific_module_branch()
    {
        var extensionRoot = Path.Combine(
            ExtensionModuleFitnessTests.RepositoryRoot,
            "src",
            "NuExtVault.Extensions.Official",
            "Registration");
        var moduleList = Path.Combine(
            ExtensionModuleFitnessTests.RepositoryRoot,
            "src",
            "NuExtVault.Extensions.Official",
            "OfficialExtensionModules.cs");
        var pattern = new Regex(
            Regex.Escape(ExtensionId) + "|RegistrationModule|RegistrationOperations",
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
    }
}
