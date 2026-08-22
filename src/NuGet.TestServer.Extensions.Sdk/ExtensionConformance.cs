using System.Collections.Immutable;
using System.Reflection;

namespace NuGet.TestServer.Extensions.Sdk;

public sealed record ConformanceResult(
    bool IsValid,
    ImmutableArray<ManifestValidationError> Errors);

public static class ExtensionConformance
{
    public static ConformanceResult ValidateAssembly(Assembly assembly) =>
        ValidateAssembly(assembly, ExtensionSdkVersions.Current);

    public static ConformanceResult ValidateAssembly(
        Assembly assembly,
        SdkContractVersion sdkVersion)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var errors = new List<ManifestValidationError>();
        if (!ExtensionSdkVersions.IsSupported(sdkVersion))
        {
            errors.Add(new ManifestValidationError(
                "$.sdk",
                "assembly.sdk.unsupported",
                $"SDK version '{sdkVersion}' is not supported."));
        }

        var moduleTypes = assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IExtensionModule).IsAssignableFrom(type))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
        if (moduleTypes.Length == 0)
        {
            errors.Add(new ManifestValidationError(
                "$.assembly",
                "assembly.module.missing",
                "The assembly contains no extension module."));
        }

        foreach (var moduleType in moduleTypes)
        {
            IExtensionModule module;
            try
            {
                module = (IExtensionModule)(Activator.CreateInstance(moduleType, nonPublic: true)
                         ?? throw new InvalidOperationException("The module could not be created."));
            }
            catch (Exception exception)
            {
                errors.Add(new ManifestValidationError(
                    $"$.modules.{moduleType.FullName}",
                    "assembly.module.invalid",
                    exception.GetBaseException().Message));
                continue;
            }

            var manifest = module.Contribution.Manifest;
            if (!string.Equals(
                    manifest.Identity.Publisher,
                    "NuGet.TestServer",
                    StringComparison.Ordinal))
            {
                foreach (var operation in manifest.Operations)
                {
                    var result = ValidateOwnership(manifest.Identity.Id, operation);
                    errors.AddRange(result.Errors);
                }
            }
        }

        return new ConformanceResult(
            errors.Count == 0,
            [
                .. errors.OrderBy(error => error.Path, StringComparer.Ordinal)
                    .ThenBy(error => error.Code, StringComparer.Ordinal)
            ]);
    }

    public static ConformanceResult ValidateOwnership(
        string extensionId,
        OperationDeclaration declaration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentNullException.ThrowIfNull(declaration);
        var errors = new List<ManifestValidationError>();
        if (StableIdentity.BuiltInOperationIds.Contains(declaration.Identity.Value))
        {
            errors.Add(new ManifestValidationError(
                "$.operation.id",
                "operation.existing.nonreplaceable",
                $"Built-in operation '{declaration.Identity.Value}' cannot be replaced."));
        }
        else if (!StableIdentity.IsStable(declaration.Identity.Value) ||
                 !declaration.Identity.Value.StartsWith(
                     extensionId + ".",
                     StringComparison.Ordinal))
        {
            errors.Add(new ManifestValidationError(
                "$.operation.id",
                "operation.identity.not-owned",
                "A contributor may own only a new stable operation ID in its namespace."));
        }

        if (declaration.AllowReplacement)
        {
            errors.Add(new ManifestValidationError(
                "$.operation.allowReplacement",
                "operation.replacement.disabled",
                "Operation replacement is disabled."));
        }

        return new ConformanceResult(
            errors.Count == 0,
            [
                .. errors.OrderBy(error => error.Path, StringComparer.Ordinal)
                    .ThenBy(error => error.Code, StringComparer.Ordinal)
            ]);
    }
}
