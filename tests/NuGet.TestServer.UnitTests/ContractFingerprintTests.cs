using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;

namespace NuGet.TestServer.UnitTests;

/// <summary>
/// Step 11C structural contract identity. Operation, route, resource, and
/// capability-candidate shapes are reduced to a deterministic canonical form, compared
/// against a golden snapshot for a readable diff, and hashed so drift cannot pass
/// silently. These contracts are internal and pre-compatibility; the fingerprint is a
/// review gate, not a public SDK commitment.
/// </summary>
public sealed class ContractFingerprintTests
{
    [Theory]
    [InlineData("operations")]
    [InlineData("routes")]
    [InlineData("resources")]
    [InlineData("capabilities")]
    public void Structural_contract_matches_its_golden_snapshot(string surface)
    {
        var canonical = ContractFingerprint.Canonical(surface).ReplaceLineEndings("\n");
        var path = ContractFingerprint.SnapshotPath(surface);

        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, canonical);
            Assert.Fail(
                $"The golden snapshot '{path}' was missing and has been generated. Review " +
                "the generated contract text before accepting this change.");
        }

        var golden = File.ReadAllText(path).ReplaceLineEndings("\n");

        Assert.Equal(golden, canonical);
        Assert.Equal(ContractFingerprint.Hash(golden), ContractFingerprint.Hash(canonical));
    }

    [Fact]
    public void Fingerprints_are_deterministic_across_repeated_computation()
    {
        foreach (var surface in ContractFingerprint.Surfaces)
        {
            Assert.Equal(
                ContractFingerprint.Hash(ContractFingerprint.Canonical(surface)),
                ContractFingerprint.Hash(ContractFingerprint.Canonical(surface)));
        }
    }

    [Fact]
    public void Fingerprints_change_when_a_contract_shape_changes()
    {
        var baseline = ContractFingerprint.Hash(ContractFingerprint.Canonical("operations"));
        var drifted = ContractFingerprint.Hash(
            ContractFingerprint.Canonical("operations") + "operation=Test.Drift\n");

        Assert.NotEqual(baseline, drifted);
        Assert.Equal(64, baseline.Length);
    }

    [Fact]
    public void The_rendering_contract_is_versioned_and_immutable()
    {
        Assert.Equal(1, OperationResult.ContractVersion);
        Assert.Equal(1, OperationResult.NoContent().Version);
        Assert.All(
            typeof(OperationResult).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Concat(typeof(OperationResult).Assembly.GetTypes()
                    .Where(type => type.IsSubclassOf(typeof(OperationResultBody)))
                    .SelectMany(type => type.GetProperties(
                        BindingFlags.Public | BindingFlags.Instance))),
            property => Assert.True(
                property.SetMethod is null || IsInitOnly(property),
                $"{property.DeclaringType?.Name}.{property.Name} is settable after creation."));
    }

    private static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod!.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(modifier => modifier.Name == "IsExternalInit");
}

/// <summary>
/// Reduces the operation, route, resource, and capability-candidate surfaces to a
/// deterministic canonical text so drift is reviewable in a diff and detectable by a
/// hash.
/// </summary>
internal static class ContractFingerprint
{
    public static ImmutableArray<string> Surfaces { get; } =
        ["operations", "routes", "resources", "capabilities"];

    public static string Canonical(string surface) => surface switch
    {
        "operations" => Operations(),
        "routes" => Routes(),
        "resources" => Resources(),
        "capabilities" => Capabilities(),
        _ => throw new ArgumentOutOfRangeException(nameof(surface))
    };

    public static string SnapshotPath(string surface) => Path.Combine(
        ExtensionModuleFitnessTests.RepositoryRoot,
        "tests",
        "NuGet.TestServer.UnitTests",
        "Snapshots",
        $"{surface}.contract.txt");

    public static string Operations()
    {
        var builder = new StringBuilder();
        foreach (var binding in OperationContracts.Bindings
                     .OrderBy(binding => binding.Contract.Id.Value, StringComparer.Ordinal))
        {
            builder.Append("operation=").Append(binding.Contract.Id.Value)
                .Append(" family=").Append(binding.Contract.Family.Name)
                .Append(" version=").Append(
                    binding.Contract.ContractVersion.ToString(CultureInfo.InvariantCulture))
                .Append(" request=").Append(binding.Contract.RequestContract)
                .Append(" response=").Append(binding.Contract.ResponseContract)
                .Append(" request-shape=").Append(Shape(binding.RequestType))
                .Append(" response-shape=").Append(Shape(binding.ResponseType))
                .Append('\n');
        }

        return builder.ToString();
    }

    public static string Routes()
    {
        var builder = new StringBuilder();
        foreach (var manifest in BuiltInExtensionCatalog.Manifests
                     .OrderBy(manifest => manifest.Id, StringComparer.Ordinal))
        {
            foreach (var descriptor in manifest.Endpoints
                         .OrderBy(descriptor => descriptor.Name, StringComparer.Ordinal))
            {
                builder.Append("route=").Append(descriptor.Name)
                    .Append(" owner=").Append(manifest.Id)
                    .Append(" methods=").Append(string.Join(
                        ",",
                        descriptor.Methods.Order(StringComparer.Ordinal)))
                    .Append(" path=").Append(descriptor.PathTemplate)
                    .Append(" access=").Append(descriptor.Access.Default)
                    .Append('/').Append(
                        descriptor.Access.ProductionIdentity?.ToString() ?? "-")
                    .Append(" head=").Append(descriptor.Head)
                    .Append(" body=").Append(descriptor.Body.Kind)
                    .Append('/').Append(descriptor.Body.MediaType ?? "-")
                    .Append(" limits=").Append(
                        descriptor.Limits.MaxRequestBytes.ToString(CultureInfo.InvariantCulture))
                    .Append('/').Append(
                        descriptor.Limits.MaxContentBytes.ToString(CultureInfo.InvariantCulture))
                    .Append(" route-parameters=").Append(Parameters(descriptor.RouteParameters))
                    .Append(" query-parameters=").Append(Parameters(descriptor.QueryParameters))
                    .Append(" operations=").Append(string.Join(
                        ",",
                        descriptor.Operations
                            .Select(operation =>
                                $"{operation.OperationId}:{operation.RequestContract}:" +
                                $"{operation.ResponseContract}")
                            .Order(StringComparer.Ordinal)))
                    .Append(" production-only=").Append(descriptor.RequiresProductionIdentity)
                    .Append('\n');
            }
        }

        return builder.ToString();
    }

    public static string Resources()
    {
        var builder = new StringBuilder();
        foreach (var manifest in BuiltInExtensionCatalog.Manifests
                     .OrderBy(manifest => manifest.Id, StringComparer.Ordinal))
        {
            foreach (var resource in manifest.Resources
                         .OrderBy(resource => resource.AdvertisedType, StringComparer.Ordinal))
            {
                builder.Append("resource=").Append(resource.AdvertisedType)
                    .Append(" owner=").Append(manifest.Id)
                    .Append(" operation=").Append(resource.OperationId.Value)
                    .Append(" route=").Append(resource.RouteName)
                    .Append(" access=").Append(resource.RequiredAccess)
                    .Append(" visibility=").Append(resource.Visibility)
                    .Append(" readiness=").Append(resource.Readiness)
                    .Append(" order=").Append(resource.Order.ToString(CultureInfo.InvariantCulture))
                    .Append(" requires=").Append(Join(resource.RequiresResourceTypes))
                    .Append(" produces=").Append(Join(resource.ProducesUrlsFor))
                    .Append('\n');
            }
        }

        return builder.ToString();
    }

    public static string Capabilities()
    {
        var builder = new StringBuilder();
        var candidates = ExtensionFacingCapabilities.Discover()
            .Concat([typeof(IHostClockCapability)])
            .OrderBy(type => type.Name, StringComparer.Ordinal);
        foreach (var capability in candidates)
        {
            builder.Append("capability=").Append(capability.Name).Append('\n');
            foreach (var member in capability.GetMembers()
                         .Where(member => member is not MethodInfo { IsSpecialName: true })
                         .Select(Signature)
                         .Order(StringComparer.Ordinal))
            {
                builder.Append("  member=").Append(member).Append('\n');
            }
        }

        return builder.ToString();
    }

    public static string Hash(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ReplaceLineEndings("\n"))));
    }

    private static string Join(ImmutableArray<string> values) =>
        values.IsDefaultOrEmpty ? "-" : string.Join(",", values.Order(StringComparer.Ordinal));

    private static string Parameters(ImmutableArray<EndpointParameter> parameters) =>
        parameters.IsDefaultOrEmpty
            ? "-"
            : string.Join(
                ",",
                parameters
                    .Select(parameter =>
                        $"{parameter.Name}:{parameter.Kind}:{parameter.IsRequired}")
                    .Order(StringComparer.Ordinal));

    private static string Signature(MemberInfo member) => member switch
    {
        MethodInfo method =>
            $"{method.Name}({string.Join(",", method.GetParameters()
                .Select(parameter => Name(parameter.ParameterType)))})->" +
            Name(method.ReturnType),
        PropertyInfo property => $"{property.Name}:{Name(property.PropertyType)}",
        _ => member.Name
    };

    private static string Name(Type type) =>
        type.IsGenericType
            ? $"{type.Name}<{string.Join(",", type.GetGenericArguments().Select(Name))}>"
            : type.Name;

    private static string Shape(Type type)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name != "EqualityContract")
            .Select(property => $"{property.Name}:{Name(property.PropertyType)}")
            .Order(StringComparer.Ordinal);
        return $"{type.Name}{{{string.Join(";", properties)}}}";
    }
}
