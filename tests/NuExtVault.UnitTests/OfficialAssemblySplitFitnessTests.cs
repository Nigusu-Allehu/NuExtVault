using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NuExtVault.Hosting;

namespace NuExtVault.UnitTests;

/// <summary>
/// Step 18 architecture fitness. The acceptance gate is the compiled assembly graph, not
/// a namespace convention: the official extensions ship in their own assembly, the kernel
/// has no compile-time knowledge of them, and only the outer bootstrap assembly is allowed
/// to reference both sides.
/// </summary>
public sealed class OfficialAssemblySplitFitnessTests
{
    private const string SdkAssembly = "NuExtVault.Extensions.Sdk";
    private const string KernelAssembly = "NuExtVault.Kernel";
    private const string OfficialAssembly = "NuExtVault.Extensions.Official";
    private const string BootstrapAssembly = "NuExtVault";
    private const string FixtureAssembly = "NuExtVault.RouteFixture";

    /// <summary>
    /// Assemblies an extension assembly may reference. Everything else is either the
    /// kernel, the hosting stack, or storage, and none of those may cross the boundary.
    /// </summary>
    private static readonly ImmutableHashSet<string> NeutralExtensionReferences =
    [
        "netstandard",
        "mscorlib",
        "System",
        SdkAssembly,
        "NuGet.Versioning"
    ];

    private static readonly ImmutableArray<string> ForbiddenExtensionReferencePrefixes =
    [
        "Microsoft.AspNetCore",
        "Microsoft.Extensions",
        "Microsoft.Data",
        "NuGet.Packaging",
        "NuGet.Protocol",
        "SQLitePCLRaw",
        KernelAssembly,
        "NuExtVault.Cli"
    ];

    /// <summary>
    /// Every official feature and the type that owns it. The split must move all of them,
    /// and it must not silently drop or rename one.
    /// </summary>
    private static readonly ImmutableArray<(string ExtensionId, string OwnerType)> OfficialOwners =
    [
        ("builtin.flat-container",
            "NuExtVault.Extensions.FlatContainer.FlatContainerModule"),
        ("builtin.operations",
            "NuExtVault.Extensions.Operations.OperationsModule"),
        ("builtin.package-management",
            "NuExtVault.Extensions.PackageManagement.PackageManagementModule"),
        ("builtin.registration",
            "NuExtVault.Extensions.Registration.RegistrationModule"),
        ("builtin.search",
            "NuExtVault.Extensions.Search.SearchModule"),
        ("NuExtVault.SupplyChain",
            "NuExtVault.Extensions.SupplyChain.SupplyChainExtension"),
        ("builtin.service-index",
            "NuExtVault.Extensions.ServiceIndex.ServiceIndexOperations"),
        ("builtin.test-control",
            "NuExtVault.Extensions.Control.ControlOperations"),
        ("builtin.vulnerabilities",
            "NuExtVault.Extensions.Vulnerabilities.VulnerabilityOperations")
    ];

    [Fact]
    public void The_official_extensions_ship_in_their_own_assembly()
    {
        var official = Load(OfficialAssembly);
        var missing = OfficialOwners
            .Where(owner => official.GetType(owner.OwnerType, throwOnError: false) is null)
            .Select(owner => owner.OwnerType)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void The_official_assembly_references_only_abstractions_and_neutral_packages()
    {
        AssertExtensionAssemblyReferences(Load(OfficialAssembly));
    }

    [Fact]
    public void The_conformance_fixture_obeys_the_same_compiled_constraints()
    {
        AssertExtensionAssemblyReferences(Load(FixtureAssembly));
    }

    [Fact]
    public void The_kernel_never_references_the_official_extensions()
    {
        var kernel = Load(KernelAssembly);
        var references = ReferencedAssemblyNames(kernel);

        Assert.DoesNotContain(OfficialAssembly, references);
        Assert.DoesNotContain(FixtureAssembly, references);
        Assert.DoesNotContain(BootstrapAssembly, references);
        Assert.Contains(SdkAssembly, references);
    }

    [Fact]
    public void The_kernel_declares_no_official_feature_owner()
    {
        var kernel = Load(KernelAssembly);
        var offenders = OfficialOwners
            .Where(owner => kernel.GetType(owner.OwnerType, throwOnError: false) is not null)
            .Select(owner => owner.OwnerType)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);

        var featureNamespaces = kernel.GetTypes()
            .Select(type => type.Namespace ?? string.Empty)
            .Where(name =>
                name.StartsWith("NuExtVault.Extensions.", StringComparison.Ordinal) &&
                name != "NuExtVault.Extensions.Sdk")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(featureNamespaces);
    }

    [Fact]
    public void Only_the_bootstrap_assembly_references_both_sides()
    {
        var bootstrap = Load(BootstrapAssembly);
        var bootstrapReferences = ReferencedAssemblyNames(bootstrap);

        Assert.Contains(KernelAssembly, bootstrapReferences);
        Assert.Contains(OfficialAssembly, bootstrapReferences);

        string[] productAssemblies =
            [SdkAssembly, KernelAssembly, OfficialAssembly, BootstrapAssembly];
        var referencingBoth = productAssemblies
            .Select(Load)
            .Where(assembly =>
            {
                var references = ReferencedAssemblyNames(assembly);
                return references.Contains(KernelAssembly) &&
                       references.Contains(OfficialAssembly);
            })
            .Select(assembly => assembly.GetName().Name ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([BootstrapAssembly], referencingBoth);
    }

    [Fact]
    public void Project_references_form_a_one_way_graph()
    {
        Assert.Empty(ProjectReferences(SdkAssembly));
        Assert.Equal([SdkAssembly], ProjectReferences(KernelAssembly));
        Assert.Equal([SdkAssembly], ProjectReferences(OfficialAssembly));
        Assert.Equal(
            [OfficialAssembly, KernelAssembly],
            ProjectReferences(BootstrapAssembly));
        Assert.Equal([BootstrapAssembly], ProjectReferences("NuExtVault.Cli"));

        var officialProject = File.ReadAllText(ProjectPath(OfficialAssembly));
        var packages = Regex.Matches(officialProject, @"PackageReference\s+Include=""([^""]+)""")
            .Select(match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.All(packages, package => Assert.Contains(package, NeutralExtensionReferences));
        Assert.DoesNotContain("FrameworkReference", officialProject, StringComparison.Ordinal);
    }

    [Fact]
    public void The_official_sources_import_only_the_contract_boundary()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles(SourceRoot(OfficialAssembly)))
        {
            foreach (Match match in Regex.Matches(
                         File.ReadAllText(file),
                         @"using\s+(NuExtVault\.[A-Za-z0-9_\.]+)\s*;"))
            {
                var imported = match.Groups[1].Value;
                if (imported == "NuExtVault.Extensions.Sdk" ||
                    imported.StartsWith("NuExtVault.Extensions.", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{Relative(file)}: {imported}");
            }
        }

        Assert.Empty(offenders.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_official_sources_never_use_hosting_storage_or_platform_surfaces()
    {
        var forbidden = new Regex(
            @"using\s+Microsoft\.(AspNetCore|Extensions|Data)|using\s+NuGet\.(Packaging|Protocol)|" +
            @"\bWebApplication\b|\bHttpContext\b|\bIEndpointRouteBuilder\b|\bIServiceProvider\b|" +
            @"\bIServiceCollection\b|\bHttpClient\b|\bSqliteConnection\b|\bDirectory\s*\.|" +
            @"\bFile\s*\.|\bPath\s*\.Combine\b|\bOperationExecutionContext\b|" +
            @"\bOperationHttpResult\b|\bStatusCodes\s*\.|\bResults\s*\.",
            RegexOptions.CultureInvariant);
        var offenders = SourceFiles(SourceRoot(OfficialAssembly))
            .Where(file => forbidden.IsMatch(File.ReadAllText(file)))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_official_assembly_holds_no_process_global_mutable_state()
    {
        var offenders = Load(OfficialAssembly).GetTypes()
            .Where(type => type.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .SelectMany(type => type
                .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                           BindingFlags.DeclaredOnly)
                .Where(field => !field.IsLiteral && !field.IsInitOnly)
                .Select(field => $"{type.FullName}.{field.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_host_profile_explicitly_selects_the_official_bundle()
    {
        var expected = OfficialOwners
            .Select(owner => owner.ExtensionId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var withoutTestControl = expected
            .Where(id => id != "builtin.test-control")
            .ToArray();

        Assert.Equal(expected, SelectedOfficialIds(ServerProfiles.Embedded));
        Assert.Equal(expected, SelectedOfficialIds(ServerProfiles.Standard));
        Assert.Equal(withoutTestControl, SelectedOfficialIds(ServerProfiles.Production));
    }

    [Fact]
    public void Parallel_hosts_resolve_isolated_official_graphs()
    {
        using var first = TestServerApplication.Build(ServerProfiles.Embedded);
        using var second = TestServerApplication.Build(ServerProfiles.Embedded);

        var expected = OfficialOwners
            .Select(owner => owner.ExtensionId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotSame(first.Graph, second.Graph);
        foreach (var host in new[] { first, second })
        {
            var resolved = host.Graph.Extensions
                .Select(extension => extension.Id)
                .Where(id => expected.Contains(id, StringComparer.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expected, resolved);
        }
    }

    [Fact]
    public void The_solution_contains_every_split_project()
    {
        var solution = File.ReadAllText(Path.Combine(RepositoryRoot, "NuExtVault.slnx"));
        foreach (var project in new[]
                 {
                     SdkAssembly, KernelAssembly, OfficialAssembly, BootstrapAssembly
                 })
        {
            Assert.Contains($"src/{project}/{project}.csproj", solution, StringComparison.Ordinal);
        }
    }

    private static string[] SelectedOfficialIds(ServerProfile profile)
    {
        var official = OfficialOwners
            .Select(owner => owner.ExtensionId)
            .ToHashSet(StringComparer.Ordinal);
        return profile.Extensions
            .Select(extension => extension.Id)
            .Where(official.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AssertExtensionAssemblyReferences(Assembly assembly)
    {
        var references = ReferencedAssemblyNames(assembly);

        Assert.NotEmpty(references);
        Assert.Contains(SdkAssembly, references);
        Assert.DoesNotContain(BootstrapAssembly, references);
        var forbidden = references
            .Where(reference => ForbiddenExtensionReferencePrefixes.Any(prefix =>
                reference.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(forbidden);

        var unexpected = references
            .Where(reference =>
                !reference.StartsWith("System.", StringComparison.Ordinal) &&
                !NeutralExtensionReferences.Contains(reference))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(unexpected);
    }

    private static ImmutableHashSet<string> ReferencedAssemblyNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToImmutableHashSet(StringComparer.Ordinal);

    private static Assembly Load(string name)
    {
        try
        {
            return Assembly.Load(new AssemblyName(name));
        }
        catch (Exception exception)
        {
            Assert.Fail($"Assembly '{name}' could not be loaded: {exception.Message}");
            throw;
        }
    }

    private static string[] ProjectReferences(string project)
    {
        var path = ProjectPath(project);
        Assert.True(File.Exists(path), $"Project '{path}' does not exist.");
        return Regex.Matches(File.ReadAllText(path), @"ProjectReference\s+Include=""([^""]+)""")
            .Select(match => Regex.Split(match.Groups[1].Value, @"[\\/]").Last())
            .Select(name => name.Replace(".csproj", string.Empty, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ProjectPath(string project) =>
        Path.Combine(SourceRoot(project), $"{project}.csproj");

    private static string SourceRoot(string project) =>
        Path.Combine(RepositoryRoot, "src", project);

    private static IEnumerable<string> SourceFiles(string root)
    {
        Assert.True(Directory.Exists(root), $"Source directory '{root}' does not exist.");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
                !file.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) &&
                !file.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal));
    }

    private static string Relative(string file) => Path.GetRelativePath(RepositoryRoot, file);

    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "NuExtVault.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new InvalidOperationException("The repository root was not found.");
    }
}
