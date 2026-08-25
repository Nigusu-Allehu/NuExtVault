using System.Collections.Immutable;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Hosting;

/// <summary>
/// Host-side composition of separately compiled extension modules. The host treats
/// every module identically: it merges the module manifest into the catalog and the
/// module contracts into the contract index. Nothing here knows the identity of any
/// individual module.
/// </summary>
internal static class ExtensionModules
{
    /// <summary>
    /// Indexes built-in and module-contributed operation contracts by operation ID.
    /// </summary>
    public static IReadOnlyDictionary<string, OperationBinding> CreateContractIndex(
        ImmutableArray<IExtensionModule> modules)
    {
        var contracts = OperationContracts.Bindings.ToDictionary(
            binding => binding.Contract.Id.Value,
            StringComparer.Ordinal);
        if (modules.IsDefaultOrEmpty)
        {
            return contracts;
        }

        foreach (var binding in modules
                     .SelectMany(module => module.Contribution.Contracts)
                     .OrderBy(binding => binding.Contract.Id.Value, StringComparer.Ordinal))
        {
            if (!contracts.TryAdd(binding.Contract.Id.Value, binding))
            {
                throw new ServerHostingConfigurationException(
                    $"catalog.duplicate-contract: Operation contract " +
                    $"'{binding.Contract.Id.Value}' is declared more than once.");
            }
        }

        return contracts;
    }

    /// <summary>
    /// Fails composition when two modules claim the same extension identity.
    /// </summary>
    public static ImmutableArray<IExtensionModule> Validate(
        ImmutableArray<IExtensionModule> modules)
    {
        if (modules.IsDefaultOrEmpty)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            var id = module.Contribution.Manifest.Id;
            if (!seen.Add(id))
            {
                throw new ServerHostingConfigurationException(
                    $"catalog.duplicate-module: Extension module '{id}' is contributed " +
                    "more than once.");
            }
        }

        var duplicateNamespace = modules
            .SelectMany(module => module.Contribution.DocumentContributors)
            .GroupBy(
                contributor => (contributor.Point, contributor.Namespace),
                EqualityComparer<(string Point, string Namespace)>.Default)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateNamespace is not null)
        {
            throw new ServerHostingConfigurationException(
                $"document-contributor-namespace-conflict: Namespace " +
                $"'{duplicateNamespace.Key.Namespace}' is declared more than once for " +
                $"contribution point '{duplicateNamespace.Key.Point}'.");
        }

        return modules;
    }
}
