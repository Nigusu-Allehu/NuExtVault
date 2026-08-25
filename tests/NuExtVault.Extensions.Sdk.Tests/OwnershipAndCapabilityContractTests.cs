using System.Collections.Immutable;
using System.Reflection;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Extensions.TestKit;

namespace NuExtVault.Extensions.Sdk.Tests;

public sealed class OwnershipAndCapabilityContractTests
{
    private static readonly ImmutableArray<string> ExistingOperationIds =
    [
        "NuGet.FlatContainer.GetHash",
        "NuGet.FlatContainer.GetNuspec",
        "NuGet.FlatContainer.GetPackage",
        "NuGet.FlatContainer.GetSymbol",
        "NuGet.FlatContainer.GetVersions",
        "NuGet.PackageManagement.Delete",
        "NuGet.PackageManagement.List",
        "NuGet.PackageManagement.Push",
        "NuGet.PackageManagement.PushSymbols",
        "NuGet.PackageManagement.Relist",
        "NuGet.PackageManagement.Unlist",
        "NuGet.Registration.GetIndex",
        "NuGet.Registration.GetLeaf",
        "NuGet.Registration.GetPage",
        "NuGet.Search.Query",
        "NuGet.ServiceIndex.Get",
        "NuGet.Vulnerabilities.GetIndex",
        "NuGet.Vulnerabilities.GetPage",
        "NuExtVault.Backup.Create",
        "NuExtVault.Control.AddFault",
        "NuExtVault.Control.AddPackage",
        "NuExtVault.Control.ClearFaults",
        "NuExtVault.Control.ClearRequests",
        "NuExtVault.Control.DeletePackage",
        "NuExtVault.Control.GetFaults",
        "NuExtVault.Control.GetPackages",
        "NuExtVault.Control.GetRequests",
        "NuExtVault.Control.GetState",
        "NuExtVault.Control.RelistPackage",
        "NuExtVault.Control.Reset",
        "NuExtVault.Control.UnlistPackage",
        "NuExtVault.Control.UpdatePackageMetadata",
        "NuExtVault.Diagnostics.Get",
        "NuExtVault.Health.GetLiveness",
        "NuExtVault.Health.GetReadiness",
        "NuExtVault.Health.GetStorage",
        "NuExtVault.Moderation.GetAudit",
        "NuExtVault.Moderation.GetValidations",
        "NuExtVault.Moderation.Moderate",
        "NuExtVault.Restore.Execute"
    ];

    [Fact]
    public void V1_contributor_can_own_only_extension_defined_new_stable_operation_ids()
    {
        var contributor = new OperationContributor("Contoso.Flavors");
        var declaration = contributor.Define<GetFlavorIndexRequest, GetFlavorIndexResponse>(
            new OperationIdentity("Contoso.Flavors.GetIndex"),
            new OperationContractVersion(1),
            static (_, _) => ValueTask.FromResult(
                OperationResponse<GetFlavorIndexResponse>.Success(
                    new GetFlavorIndexResponse(["vanilla"]))));

        Assert.Equal("Contoso.Flavors.GetIndex", declaration.Identity.Value);
        Assert.Equal(OperationOwnership.New, declaration.Ownership);
        Assert.False(declaration.AllowReplacement);
        Assert.Throws<ArgumentException>(() => contributor.Define<
            GetFlavorIndexRequest,
            GetFlavorIndexResponse>(
            new OperationIdentity("not-stable"),
            new OperationContractVersion(1),
            static (_, _) => throw new InvalidOperationException()));
    }

    [Theory]
    [MemberData(nameof(ExistingOperations))]
    public void Existing_builtin_operations_are_nonreplaceable(string operationId)
    {
        var result = ExtensionConformance.ValidateOwnership(
            "Contoso.Flavors",
            new OperationDeclaration(
                new OperationIdentity(operationId),
                new OperationContractVersion(1),
                "Contoso.Request.v1",
                "Contoso.Response.v1",
                OperationOwnership.New,
                AllowReplacement: false));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "operation.existing.nonreplaceable");
    }

    [Fact]
    public void Identity_publication_moderation_ownership_recovery_and_package_mutations_are_never_replaceable()
    {
        string[] protectedPrefixes =
        [
            "NuGet.PackageManagement.",
            "NuExtVault.Moderation.",
            "NuExtVault.Backup.",
            "NuExtVault.Restore.",
            "NuExtVault.Control.AddPackage",
            "NuExtVault.Control.DeletePackage",
            "NuExtVault.Control.RelistPackage",
            "NuExtVault.Control.UnlistPackage",
            "NuExtVault.Control.UpdatePackageMetadata"
        ];

        Assert.All(
            ExistingOperationIds.Where(id =>
                protectedPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal))),
            id => Assert.Equal(ReplacementPolicy.Never, OperationReplacementPolicies.For(id)));
        Assert.Equal(ReplacementPolicy.Never, OperationReplacementPolicies.IdentityMutations);
        Assert.Equal(ReplacementPolicy.Never, OperationReplacementPolicies.OwnershipMutations);
        Assert.Equal(ReplacementPolicy.Disabled, OperationReplacementPolicies.Default);
    }

    [Fact]
    public void Public_sdk_exposes_no_replace_override_or_takeover_registration_api()
    {
        var suspicious = typeof(ExtensionManifest).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                                BindingFlags.Static | BindingFlags.DeclaredOnly))
            .OfType<MethodInfo>()
            .Where(method => !method.IsSpecialName)
            .Where(method =>
                method.Name.Contains("Replace", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("Override", StringComparison.OrdinalIgnoreCase) ||
                method.Name.Contains("TakeOver", StringComparison.OrdinalIgnoreCase))
            .Select(member => $"{member.DeclaringType?.FullName}.{member.Name}")
            .ToArray();

        Assert.Empty(suspicious);
    }

    [Fact]
    public void Required_and_optional_capabilities_are_explicit_and_deny_by_default()
    {
        var required = new CapabilityRequest(
            new CapabilityIdentity("host.clock.read"),
            CapabilityRequirement.Required);
        var optional = new CapabilityRequest(
            new CapabilityIdentity("network.outbound-http"),
            CapabilityRequirement.Optional);
        var grants = CapabilityGrantSet.Create(
            "Contoso.Flavors",
            [new CapabilityIdentity("host.clock.read")]);

        Assert.True(grants.GetRequired<IHostClockCapability>(required) is not null);
        Assert.False(grants.TryGet<IOutboundHttpCapability>(optional, out _));
        Assert.Throws<CapabilityDeniedException>(() => grants.GetRequired<IOutboundHttpCapability>(
            new CapabilityRequest(
                new CapabilityIdentity("network.outbound-http"),
                CapabilityRequirement.Required)));
    }

    [Fact]
    public void Capability_signatures_are_async_action_scoped_serializable_and_bounded()
    {
        var capabilities = typeof(IHostClockCapability).Assembly.GetExportedTypes()
            .Where(type => type.IsInterface &&
                           type.Name.EndsWith("Capability", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(capabilities);

        var violations = capabilities.SelectMany(type => type.GetMethods().SelectMany(method =>
        {
            var errors = new List<string>();
            if (method.ReturnType != typeof(ValueTask) &&
                !(method.ReturnType.IsGenericType &&
                  method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            {
                errors.Add($"{type.Name}.{method.Name}: not ValueTask");
            }

            if (!method.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(CancellationToken)))
            {
                errors.Add($"{type.Name}.{method.Name}: no CancellationToken");
            }

            foreach (var signatureType in method.GetParameters()
                         .Select(parameter => parameter.ParameterType)
                         .Append(method.ReturnType)
                         .SelectMany(Expand))
            {
                var name = signatureType.FullName ?? string.Empty;
                if (name is "System.IO.Stream" or "System.IServiceProvider" ||
                    name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) ||
                    name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) ||
                    name.StartsWith("NuExtVault.Kernel.", StringComparison.Ordinal) ||
                    name.Contains("OperationExecutionContext", StringComparison.Ordinal) ||
                    name.Contains("TestPackage", StringComparison.Ordinal) ||
                    name.Contains("StorageBackupManifest", StringComparison.Ordinal))
                {
                    errors.Add($"{type.Name}.{method.Name}: {name}");
                }
            }

            return errors;
        })).Order(StringComparer.Ordinal).ToArray();

        Assert.Empty(violations);
        Assert.All(typeof(StreamHandle).GetProperties(), property => Assert.NotNull(property.GetMethod));
        Assert.Contains(typeof(StreamHandle).GetProperties(), property => property.Name == "MaximumLength");
        Assert.Contains(typeof(BoundedDocument).GetProperties(), property => property.Name == "MaximumLength");
    }

    [Fact]
    public void Handles_and_documents_reject_unbounded_or_oversized_content()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StreamHandle("content-1", 0, "application/octet-stream"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedDocument([1, 2, 3, 4], 3, "application/json"));

        var document = new BoundedDocument([1, 2, 3], 3, "application/json");
        Assert.Equal(3, document.Content.Length);
        Assert.Equal(3, document.MaximumLength);
    }

    [Fact]
    public void Official_modules_and_separately_compiled_fixture_conform_to_the_public_sdk()
    {
        var sdk = typeof(ExtensionManifest).Assembly.GetName().Name
                  ?? throw new InvalidOperationException("The SDK assembly has no name.");
        var official = Assembly.Load("NuExtVault.Extensions.Official");
        var fixture = typeof(NuExtVault.SdkFixture.FlavorsExtension).Assembly;

        Assert.Equal(
            [sdk],
            official.GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .Where(name => name?.StartsWith("NuExtVault.Extensions.", StringComparison.Ordinal) == true)
                .Except(["NuExtVault.Extensions.Official"])
                .Select(name => name!)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal([sdk], fixture.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith("NuExtVault.Extensions.", StringComparison.Ordinal) == true)
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray());

        Assert.True(ExtensionConformance.ValidateAssembly(official).IsValid);
        Assert.True(ExtensionConformance.ValidateAssembly(fixture).IsValid);
        Assert.True(ExtensionConformance.ValidateAssembly(
            official,
            ExtensionSdkVersions.OldestSupported).IsValid);
        Assert.True(ExtensionConformance.ValidateAssembly(
            official,
            ExtensionSdkVersions.Current).IsValid);
        Assert.False(ExtensionConformance.ValidateAssembly(
            official,
            new SdkContractVersion(2, 0, 0)).IsValid);
    }

    public static IEnumerable<object[]> ExistingOperations() =>
        ExistingOperationIds.Select(id => new object[] { id });

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.IsByRef || type.IsArray)
        {
            yield return type.GetElementType()!;
        }
        else if (type.IsGenericType)
        {
            foreach (var nested in type.GetGenericArguments().SelectMany(Expand))
            {
                yield return nested;
            }
        }
    }

    public sealed record GetFlavorIndexRequest;

    public sealed record GetFlavorIndexResponse(ImmutableArray<string> Flavors);
}
