using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Kernel;

namespace NuGet.TestServer.Hosting;

/// <summary>
/// A separately compiled extension contribution. It carries the manifest (including its
/// endpoint descriptors), the operation contracts it introduces, and the registration of
/// its typed operation owners. Contributions never receive <c>WebApplication</c>, the
/// root service provider, endpoint routing, or middleware registration.
/// </summary>
internal sealed record ExtensionContribution(
    ExtensionManifest Manifest,
    ImmutableArray<OperationBinding> Contracts,
    Action<OperationRegistryBuilder> RegisterOperations)
{
    /// <summary>
    /// The profile selection a host uses to activate this contribution.
    /// </summary>
    public ExtensionSelection Selection => new(Manifest.Id, Manifest.RequestedCapabilities);

    /// <summary>
    /// Indexes built-in and contributed operation contracts by operation ID.
    /// </summary>
    public static IReadOnlyDictionary<string, OperationBinding> CreateContractIndex(
        ImmutableArray<ExtensionContribution> contributions)
    {
        var contracts = OperationContracts.Bindings.ToDictionary(
            binding => binding.Contract.Id.Value,
            StringComparer.Ordinal);
        if (contributions.IsDefaultOrEmpty)
        {
            return contracts;
        }

        foreach (var binding in contributions
                     .SelectMany(contribution => contribution.Contracts)
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
}
