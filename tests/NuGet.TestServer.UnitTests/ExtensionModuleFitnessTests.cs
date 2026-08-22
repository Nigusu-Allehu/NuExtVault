using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Capabilities;
using NuGet.TestServer.Extensions.Vulnerabilities;

namespace NuGet.TestServer.UnitTests;

/// <summary>
/// Step 11C architecture fitness. The dependency direction is one-way, extension owners
/// have no kernel rendering escape, and every capability an extension owner sees is
/// action-scoped and serializable.
/// </summary>
public sealed class ExtensionModuleFitnessTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.AspNetCore",
        "Microsoft.Extensions",
        "Microsoft.Data",
        "NuGet.Packaging",
        "NuGet.Protocol",
        "NuGet.Versioning",
        "NuGet.TestServer,",
        "NuGet.TestServer.Cli"
    ];

    [Fact]
    public void Abstractions_reference_only_approved_framework_dependencies()
    {
        var references = typeof(OperationId).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(references);
        Assert.DoesNotContain(
            references,
            reference => ForbiddenAssemblyPrefixes.Any(forbidden =>
                reference.StartsWith(forbidden.TrimEnd(','), StringComparison.Ordinal)));
        Assert.All(references, reference => Assert.True(
            reference.StartsWith("System.", StringComparison.Ordinal) ||
            reference is "netstandard" or "mscorlib" or "System",
            $"The contract assembly references '{reference}'."));
    }

    [Fact]
    public void The_kernel_has_no_compile_time_knowledge_of_the_conformance_module()
    {
        var references = typeof(ServerApplication).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("NuGet.TestServer.RouteFixture", references);

        var pattern = new Regex(
            @"RouteFixture|FlavorsModule|contoso\.flavors|Contoso\.Flavors|/flavors/",
            RegexOptions.CultureInvariant);
        var offenders = EnumerateSourceFiles(Path.Combine(RepositoryRoot, "src"))
            .Where(file => pattern.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(RepositoryRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_conformance_module_references_only_the_extension_abstractions()
    {
        var project = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tests",
            "NuGet.TestServer.RouteFixture",
            "NuGet.TestServer.RouteFixture.csproj"));

        var references = Regex.Matches(project, @"ProjectReference\s+Include=""([^""]+)""")
            .Select(match => Regex.Split(match.Groups[1].Value, @"[\\/]").Last())
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["NuGet.TestServer.Extensions.Abstractions.csproj"],
            references);
        Assert.DoesNotContain("PackageReference", project, StringComparison.Ordinal);

        var offenders = EnumerateSourceFiles(Path.Combine(
                RepositoryRoot,
                "tests",
                "NuGet.TestServer.RouteFixture"))
            .Where(file => Regex.IsMatch(
                File.ReadAllText(file),
                @"using\s+NuGet\.TestServer\.(?!Extensions\.Abstractions)"))
            .Select(file => Path.GetRelativePath(RepositoryRoot, file))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Official_extension_owners_have_zero_kernel_rendering_escapes()
    {
        // Deserializing an extension's own persisted state is not a rendering escape;
        // producing an HTTP response is. Only response rendering is forbidden here.
        var forbidden = new Regex(
            @"\bOperationExecutionContext\b|\bOperationHttpResult\b|\bStatusCodes\s*\.|" +
            @"\bResults\s*\.|\bHttpContext\b|\bIEndpointRouteBuilder\b|\bWebApplication\b|" +
            @"\bIServiceProvider\b|\bUtf8JsonWriter\b",
            RegexOptions.CultureInvariant);
        var offenders = EnumerateSourceFiles(OfficialExtensionRoot)
            .Where(file => forbidden.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(RepositoryRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Official_extension_owners_use_only_the_abstraction_contract_boundary()
    {
        // Step 18 made this a compiled boundary: the official extensions are their own
        // assembly and may import the contracts plus their own feature namespaces only.
        var offenders = new List<string>();
        foreach (var file in EnumerateSourceFiles(OfficialExtensionRoot))
        {
            foreach (Match match in Regex.Matches(
                         File.ReadAllText(file),
                         @"using\s+(NuGet\.TestServer\.[A-Za-z0-9_\.]+)\s*;"))
            {
                var imported = match.Groups[1].Value;
                if (imported == "NuGet.TestServer.Extensions.Abstractions" ||
                    imported.StartsWith("NuGet.TestServer.Extensions.", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add(
                    $"{Path.GetRelativePath(RepositoryRoot, file)}: {imported}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Capabilities_reachable_from_extension_owners_are_action_scoped_and_serializable()
    {
        var discovered = ExtensionFacingCapabilities.Discover();

        Assert.Equal(
            ExtensionFacingCapabilities.Expected,
            discovered.Select(type => type.Name).Order(StringComparer.Ordinal).ToArray());

        var violations = new List<string>();
        foreach (var capability in discovered.Concat([typeof(IHostClockCapability)]))
        {
            foreach (var member in capability.GetMembers())
            {
                foreach (var used in ExtensionFacingCapabilities.SignatureTypes(member))
                {
                    if (!ExtensionFacingCapabilities.IsSerializableContractType(used))
                    {
                        violations.Add($"{capability.Name}.{member.Name}: {used.FullName}");
                    }
                }
            }
        }

        Assert.Empty(violations.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Owner_shaped_kernel_types_never_appear_in_extension_facing_capabilities()
    {
        string[] banned =
        [
            "NuGet.TestServer.Kernel.OperationExecutionContext",
            "NuGet.TestServer.Packages.TestPackage",
            "NuGet.TestServer.Packages.IPackageStore",
            "NuGet.TestServer.Packages.PackageRepositoryMetadata",
            "NuGet.TestServer.Operations.StorageBackupManifest",
            "NuGet.TestServer.Faults.FaultRule",
            "NuGet.TestServer.Requests.RequestRecord",
            "NuGet.TestServer.Extensions.Vulnerabilities.VulnerabilitySnapshot",
            "System.IO.Stream",
            "System.IServiceProvider"
        ];
        var reached = ExtensionFacingCapabilities.Discover()
            .Concat([typeof(IHostClockCapability)])
            .SelectMany(capability => capability.GetMembers()
                .SelectMany(ExtensionFacingCapabilities.SignatureTypes))
            .Select(type => type.FullName ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(banned, reached.Contains);
    }

    [Fact]
    public void Capability_grants_stay_deny_by_default_for_unknown_capability_names()
    {
        Assert.False(CapabilityContracts.Supports(
            typeof(IHostClockCapability),
            BuiltInCapabilityNames.ControlPackagesManage));
        Assert.True(CapabilityContracts.Supports(
            typeof(IHostClockCapability),
            KernelCapabilityNames.HostClockRead));
        Assert.Equal(KernelCapabilityNames.HostClockRead, BuiltInCapabilityNames.HostClockRead);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
                !file.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) &&
                !file.Contains(
                    $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal));

    /// <summary>The source root of the separately compiled official extension assembly.</summary>
    internal static string OfficialExtensionRoot =>
        Path.Combine(RepositoryRoot, "src", "NuGet.TestServer.Extensions.Official");

    internal static string RepositoryRoot { get; } = FindRepositoryRoot();

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

/// <summary>
/// Discovers the capability interfaces an official extension owner can reach and
/// classifies whether a signature type may cross that boundary. Discovery runs against
/// the separately compiled official extension assembly, so it measures the real
/// boundary rather than a namespace convention.
/// </summary>
internal static class ExtensionFacingCapabilities
{
    /// <summary>
    /// The capability interfaces official extension owners are allowed to consume.
    /// Adding one requires an explicit review of its signature.
    /// </summary>
    public static ImmutableArray<string> Expected { get; } =
    [
        "IBackupCheckpointCapability",
        "IExtensionStateCapability",
        "IKernelInstrumentationControlCapability",
        "IOperationsQueryCapability",
        "IOutboundHttpCapability",
        "IPackageContentReadCapability",
        "IPackageControlCapability",
        "IPackageDeleteCapability",
        "IPackageManagementListCapability",
        "IPackageMetadataReadCapability",
        "IPackagePushCapability",
        "IPackageRelistCapability",
        "IPackageScannerCapability",
        "IPackageSignatureInspectionCapability",
        "IPackageSymbolReadCapability",
        "IPackageSymbolsPushCapability",
        "IPackageUnlistCapability",
        "IRegistrationMetadataReadCapability",
        "IRegistrationVulnerabilityReadCapability",
        "IRestoreCheckpointCapability",
        "ISearchIndexQueryCapability",
        "IVulnerabilityCatalogCapability"
    ];

    public static ImmutableArray<Type> Discover()
    {
        var assembly = typeof(NuGet.TestServer.Extensions.Official.OfficialExtensionModules)
            .Assembly;
        var owners = assembly.GetTypes();
        var reachable = owners
            .SelectMany(type => type
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(constructor =>
                    constructor.GetParameters().Select(parameter => parameter.ParameterType))
                .Concat(type
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .SelectMany(method =>
                        method.GetParameters().Select(parameter => parameter.ParameterType)
                            .Append(method.ReturnType))))
            .Where(type =>
                type.IsInterface &&
                type.Name.EndsWith("Capability", StringComparison.Ordinal))
            .Distinct()
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        return reachable;
    }

    public static IEnumerable<Type> SignatureTypes(MemberInfo member)
    {
        var roots = member switch
        {
            MethodInfo method => method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType),
            PropertyInfo property => [property.PropertyType],
            _ => []
        };
        return roots.SelectMany(type => Expand(type, []));
    }

    public static bool IsSerializableContractType(Type type)
    {
        var candidate = type.IsByRef || type.IsArray ? type.GetElementType()! : type;
        if (candidate.IsGenericParameter ||
            candidate == typeof(void) ||
            candidate == typeof(CancellationToken))
        {
            return true;
        }

        if (candidate == typeof(Stream) ||
            candidate == typeof(FileInfo) ||
            candidate == typeof(DirectoryInfo) ||
            candidate == typeof(IServiceProvider))
        {
            return false;
        }

        // After the Step 18 split the boundary is the contract assembly itself: an
        // extension-facing capability may only name contract types and framework types.
        var namespaceName = candidate.Namespace ?? string.Empty;
        return namespaceName == "NuGet.TestServer.Extensions.Abstractions" ||
               namespaceName.StartsWith("System", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> Expand(Type type, HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            yield break;
        }

        yield return type;
        if (type.IsArray || type.IsByRef)
        {
            foreach (var nested in Expand(type.GetElementType()!, visited))
            {
                yield return nested;
            }
            yield break;
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var nested in Expand(argument, visited))
                {
                    yield return nested;
                }
            }
        }

        if (type.Namespace == "NuGet.TestServer.Extensions.Abstractions" &&
            ReviewedCapabilityDocuments.Contains(type.Name))
        {
            foreach (var property in type.GetProperties(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                foreach (var nested in Expand(property.PropertyType, visited))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// Capability payloads that were explicitly reviewed as contract-safe and whose
    /// members are expanded transitively. <c>OutboundHttpResponse</c> carries a bounded
    /// byte document rather than a raw stream. Adding an entry requires the same review.
    /// </summary>
    private static readonly ImmutableHashSet<string> ReviewedCapabilityDocuments =
    [
        "ExtensionStateEntry`1",
        "ExtensionStateFile",
        "ExtensionStateFileSet",
        "OutboundHttpRequest",
        "OutboundHttpResponse",
        "VulnerabilityCatalogDocument",
        "VulnerabilityCatalogPageDocument"
    ];
}
