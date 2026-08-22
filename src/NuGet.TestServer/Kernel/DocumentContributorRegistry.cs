using System.Collections.Immutable;
using System.Text.Json;
using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;

namespace NuGet.TestServer.Kernel;

internal sealed class DocumentContributorRegistry :
    IDocumentContributorRegistry,
    IDocumentContributionSource
{
    internal const int MaximumContributorsPerPoint = 16;
    internal const int MaximumContributionBytes = 16 * 1024;

    private readonly Dictionary<(string Point, string Contract, Type Context, Type Value), List<object>>
        _registrations = new();
    private readonly HashSet<RegistrationKey> _declared;
    private readonly HashSet<RegistrationKey> _registered = [];

    private DocumentContributorRegistry(IEnumerable<(string ExtensionId, DocumentContributorDescriptor Descriptor)> declared)
    {
        _declared =
        [
            .. declared.Select(item => new RegistrationKey(
                item.ExtensionId,
                item.Descriptor.Point,
                item.Descriptor.Contract,
                item.Descriptor.Namespace,
                item.Descriptor.Priority))
        ];
    }

    public void Register<TContext, TContribution>(
        string extensionId,
        string point,
        string contract,
        string @namespace,
        int priority,
        IDocumentContributor<TContext, TContribution> contributor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(point);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentNullException.ThrowIfNull(contributor);
        var registrationKey = new RegistrationKey(
            extensionId,
            point,
            contract,
            @namespace,
            priority);
        if (!_declared.Contains(registrationKey))
        {
            throw new ServerHostingConfigurationException(
                $"document-contributor-undeclared: Extension '{extensionId}' registered " +
                $"undeclared namespace '{@namespace}' for contribution point '{point}'.");
        }

        if (!_registered.Add(registrationKey))
        {
            throw new ServerHostingConfigurationException(
                $"document-contributor-duplicate-registration: Extension '{extensionId}' " +
                $"registered namespace '{@namespace}' more than once for '{point}'.");
        }

        var key = (point, contract, typeof(TContext), typeof(TContribution));
        if (!_registrations.TryGetValue(key, out var registrations))
        {
            registrations = [];
            _registrations.Add(key, registrations);
        }

        if (registrations.Count >= MaximumContributorsPerPoint)
        {
            throw new ServerHostingConfigurationException(
                $"document-contributor-limit: Contribution point '{point}' exceeds " +
                $"{MaximumContributorsPerPoint} contributors.");
        }

        if (registrations
            .Cast<RegisteredDocumentContributor<TContext, TContribution>>()
            .Any(item => string.Equals(item.Namespace, @namespace, StringComparison.Ordinal)))
        {
            throw new ServerHostingConfigurationException(
                $"document-contributor-namespace-conflict: Namespace '{@namespace}' is already " +
                $"registered for contribution point '{point}'.");
        }

        registrations.Add(new RegisteredDocumentContributor<TContext, TContribution>(
            extensionId,
            @namespace,
            priority,
            new BoundedContributor<TContext, TContribution>(contributor)));
    }

    public ImmutableArray<RegisteredDocumentContributor<TContext, TContribution>>
        Get<TContext, TContribution>(string point, string contract)
    {
        var key = (point, contract, typeof(TContext), typeof(TContribution));
        return !_registrations.TryGetValue(key, out var registrations)
            ? []
            :
            [
                .. registrations
                    .Cast<RegisteredDocumentContributor<TContext, TContribution>>()
                    .OrderBy(item => item.Priority)
                    .ThenBy(item => item.ExtensionId, StringComparer.Ordinal)
                    .ThenBy(item => item.Namespace, StringComparer.Ordinal)
            ];
    }

    public static DocumentContributorRegistry Create(
        ResolvedExtensionGraph graph,
        IEnumerable<IExtensionModule> modules,
        CapabilityBroker broker)
    {
        var selected = graph.Extensions
            .Select(extension => extension.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeModules = modules
            .Where(module => selected.Contains(module.Contribution.Manifest.Id))
            .OrderBy(
                module => module.Contribution.Manifest.Id,
                StringComparer.Ordinal)
            .ToArray();
        var registry = new DocumentContributorRegistry(activeModules.SelectMany(module =>
            module.Contribution.DocumentContributors.Select(
                descriptor => (module.Contribution.Manifest.Id, descriptor))));
        foreach (var module in activeModules
                     .OrderBy(
                         module => module.Contribution.Manifest.Id,
                         StringComparer.Ordinal))
        {
            module.RegisterDocumentContributors(
                registry,
                broker.ForOwner(module.Contribution.Manifest.Id));
        }

        var missing = registry._declared.Except(registry._registered).FirstOrDefault();
        if (missing is not null)
        {
            throw new ServerHostingConfigurationException(
                $"document-contributor-missing-registration: Extension " +
                $"'{missing.ExtensionId}' did not register namespace '{missing.Namespace}' " +
                $"for contribution point '{missing.Point}'.");
        }

        return registry;
    }

    private sealed record RegistrationKey(
        string ExtensionId,
        string Point,
        string Contract,
        string Namespace,
        int Priority);

    private sealed class BoundedContributor<TContext, TContribution>(
        IDocumentContributor<TContext, TContribution> inner)
        : IDocumentContributor<TContext, TContribution>
    {
        public async ValueTask<TContribution> ContributeAsync(
            TContext context,
            CancellationToken cancellationToken)
        {
            var value = await inner.ContributeAsync(context, cancellationToken);
            var size = JsonSerializer.SerializeToUtf8Bytes(value).Length;
            if (size > MaximumContributionBytes)
            {
                throw new InvalidOperationException(
                    $"Document contribution exceeds {MaximumContributionBytes} bytes.");
            }

            return value;
        }
    }
}
