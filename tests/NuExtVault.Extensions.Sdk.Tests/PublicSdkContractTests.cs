using System.Reflection;
using System.Runtime.Versioning;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Extensions.Sdk.Tests;

public sealed class PublicSdkContractTests
{
    private static readonly string[] ForbiddenReferencePrefixes =
    [
        "Microsoft.AspNetCore",
        "Microsoft.Extensions",
        "Microsoft.Data",
        "NuExtVault.Kernel",
        "NuExtVault.Extensions.Official",
        "NuExtVault,",
        "NuGet.Packaging",
        "NuGet.Protocol",
        "SQLitePCLRaw"
    ];

    [Fact]
    public void Sdk_has_frozen_public_assembly_identity_and_api()
    {
        var assembly = typeof(ExtensionManifest).Assembly;
        var name = assembly.GetName();

        Assert.Equal("NuExtVault.Extensions.Sdk", name.Name);
        Assert.Equal(new Version(1, 3, 0, 0), name.Version);
        Assert.Equal(
            ".NETCoreApp,Version=v10.0",
            assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
        Assert.Equal(
            TestPaths.NormalizePublicApi(
                File.ReadAllText(TestPaths.Snapshot("Sdk.PublicApi.approved.txt"))),
            TestPaths.NormalizePublicApi(TestPaths.PublicApi(assembly)));
    }

    [Fact]
    public void TestKit_has_independent_frozen_assembly_identity()
    {
        var assembly = typeof(NuExtVault.Extensions.TestKit.ManifestBuilder).Assembly;

        Assert.Equal("NuExtVault.Extensions.TestKit", assembly.GetName().Name);
        Assert.Equal(new Version(1, 1, 0, 0), assembly.GetName().Version);
        Assert.NotSame(typeof(ExtensionManifest).Assembly, assembly);
    }

    [Fact]
    public void Contract_identities_are_independently_typed_and_versioned()
    {
        Type[] versions =
        [
            typeof(SdkContractVersion),
            typeof(ManifestSchemaVersion),
            typeof(OperationContractVersion),
            typeof(ContributionContractVersion),
            typeof(RouteContractVersion),
            typeof(CapabilityContractVersion),
            typeof(StructuralContractVersion)
        ];

        Type[] identities =
        [
            typeof(SdkContractIdentity),
            typeof(ExtensionIdentity),
            typeof(OperationIdentity),
            typeof(ContributionIdentity),
            typeof(RouteIdentity),
            typeof(CapabilityIdentity),
            typeof(StructuralContractIdentity)
        ];

        Assert.Equal(versions.Length, versions.Distinct().Count());
        Assert.Equal(identities.Length, identities.Distinct().Count());
        Assert.All(versions, version => Assert.True(version.IsValueType));
        Assert.Equal(
            new SdkContractIdentity("NuExtVault.Extensions.Sdk"),
            ExtensionSdkVersions.Identity);
        Assert.Equal(new SdkContractVersion(1, 4, 0), ExtensionSdkVersions.Current);
        Assert.Equal(new SdkContractVersion(1, 0, 0), ExtensionSdkVersions.OldestSupported);
        Assert.Equal(new ManifestSchemaVersion(1), ExtensionSdkVersions.ManifestV1);
        Assert.Equal(new ManifestSchemaVersion(2), ExtensionSdkVersions.ManifestV2);
        Assert.Equal(new OperationContractVersion(1), ExtensionSdkVersions.OperationV1);
        Assert.Equal(new ContributionContractVersion(1), ExtensionSdkVersions.ContributionV1);
        Assert.Equal(new RouteContractVersion(1), ExtensionSdkVersions.RouteV1);
        Assert.Equal(new CapabilityContractVersion(1), ExtensionSdkVersions.CapabilityV1);
        Assert.Equal(new StructuralContractVersion(1), ExtensionSdkVersions.StructuralV1);
        Assert.Equal("NuExtVault.Extensions.Conformance/v1", ExtensionSdkVersions.ConformanceSuiteV1);
    }

    [Theory]
    [InlineData(1, 0, 0, true)]
    [InlineData(1, 1, 0, true)]
    [InlineData(1, 2, 0, true)]
    [InlineData(0, 9, 9, false)]
    [InlineData(1, 3, 0, true)]
    [InlineData(1, 4, 0, true)]
    [InlineData(1, 5, 0, false)]
    [InlineData(2, 0, 0, false)]
    public void Sdk_support_range_is_same_major_and_bounded_by_host(
        int major,
        int minor,
        int patch,
        bool expected)
    {
        Assert.Equal(
            expected,
            ExtensionSdkVersions.IsSupported(new SdkContractVersion(major, minor, patch)));
    }

    [Fact]
    public void Compatibility_policy_guarantees_time_and_release_floors()
    {
        Assert.Equal(TimeSpan.FromDays(365), ExtensionSdkCompatibility.MinimumSupportDuration);
        Assert.Equal(2, ExtensionSdkCompatibility.MinimumPriorMinorReleases);
        Assert.True(ExtensionSdkCompatibility.SameMajorRequired);
    }

    [Fact]
    public void Sdk_references_no_host_kernel_storage_security_di_or_rendering_stack()
    {
        var references = typeof(ExtensionManifest).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(
            references,
            reference => ForbiddenReferencePrefixes.Any(prefix =>
                reference.StartsWith(prefix.TrimEnd(','), StringComparison.Ordinal)));

        var forbiddenPublicTypes = typeof(ExtensionManifest).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                                BindingFlags.Static | BindingFlags.DeclaredOnly))
            .SelectMany(PublicSignatureTypes)
            .Where(type =>
            {
                var name = type.FullName ?? string.Empty;
                return name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) ||
                       name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) ||
                       name.StartsWith("Microsoft.Data.", StringComparison.Ordinal) ||
                       name.StartsWith("NuExtVault.Kernel.", StringComparison.Ordinal) ||
                       name.Contains("OperationExecutionContext", StringComparison.Ordinal) ||
                       name.Contains("TestPackage", StringComparison.Ordinal) ||
                       name.Contains("StorageBackupManifest", StringComparison.Ordinal) ||
                       name is "System.IServiceProvider" or "System.IO.Stream";
            })
            .Select(type => type.FullName)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(forbiddenPublicTypes);
    }

    [Fact]
    public void Runtime_discovery_loading_and_sidecars_are_not_part_of_step_19()
    {
        var forbidden = typeof(ExtensionManifest).Assembly.GetExportedTypes()
            .Where(type =>
                type.Name.Contains("Discovery", StringComparison.OrdinalIgnoreCase) ||
                type.Name.Contains("Loader", StringComparison.OrdinalIgnoreCase) ||
                type.Name.Contains("AssemblyLoadContext", StringComparison.OrdinalIgnoreCase) ||
                type.Name.Contains("Sidecar", StringComparison.OrdinalIgnoreCase))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(forbidden);
    }

    private static IEnumerable<Type> PublicSignatureTypes(MemberInfo member)
    {
        var roots = member switch
        {
            MethodInfo method => method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType),
            ConstructorInfo constructor =>
                constructor.GetParameters().Select(parameter => parameter.ParameterType),
            PropertyInfo property => [property.PropertyType],
            FieldInfo field => [field.FieldType],
            _ => []
        };
        return roots.SelectMany(Expand);
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.IsArray || type.IsByRef)
        {
            yield return type.GetElementType()!;
        }
        else if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Expand))
            {
                yield return argument;
            }
        }
    }
}
