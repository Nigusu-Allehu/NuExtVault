using NuGet.TestServer.Extensions.Abstractions;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel.Capabilities;

namespace NuGet.TestServer.Kernel;

internal sealed class PolicyParticipantRegistry : IPolicyParticipantRegistry
{
    private readonly Dictionary<Type, List<object>> _registrations = [];
    private readonly Dictionary<string, HashSet<PolicyParticipantDescriptor>> _declared;

    private PolicyParticipantRegistry(IEnumerable<IExtensionModule> modules)
    {
        _declared = modules.ToDictionary(
            module => module.Contribution.Manifest.Id,
            module => module.Contribution.PolicyParticipants.ToHashSet(),
            StringComparer.OrdinalIgnoreCase);
    }

    public IPolicyParticipantRegistry Register<TContext>(
        string extensionId,
        PolicyParticipantRegistration<TContext> participant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentNullException.ThrowIfNull(participant);
        var descriptor = new PolicyParticipantDescriptor(
            participant.PolicyPoint,
            participant.ParticipantId,
            participant.IsAuthoritative);
        if (!_declared.TryGetValue(extensionId, out var declared) ||
            !declared.Contains(descriptor))
        {
            throw new ServerHostingConfigurationException(
                $"catalog.undeclared-policy-participant: Extension '{extensionId}' registered " +
                $"undeclared participant '{participant.ParticipantId}' for " +
                $"'{participant.PolicyPoint}'.");
        }

        if (!_registrations.TryGetValue(typeof(TContext), out var registrations))
        {
            registrations = [];
            _registrations.Add(typeof(TContext), registrations);
        }

        if (registrations
            .Cast<PolicyParticipantRegistration<TContext>>()
            .Any(existing =>
                string.Equals(existing.PolicyPoint, participant.PolicyPoint, StringComparison.Ordinal) &&
                string.Equals(existing.ParticipantId, participant.ParticipantId, StringComparison.Ordinal)))
        {
            throw new ServerHostingConfigurationException(
                $"catalog.duplicate-policy-participant: Participant " +
                $"'{participant.ParticipantId}' is registered more than once for " +
                $"'{participant.PolicyPoint}'.");
        }

        registrations.Add(participant);
        return this;
    }

    public IReadOnlyList<PolicyParticipantRegistration<TContext>> Get<TContext>(
        string policyPoint) =>
        _registrations.TryGetValue(typeof(TContext), out var registrations)
            ? registrations
                .Cast<PolicyParticipantRegistration<TContext>>()
                .Where(registration => string.Equals(
                    registration.PolicyPoint,
                    policyPoint,
                    StringComparison.Ordinal))
                .OrderBy(registration => registration.ParticipantId, StringComparer.Ordinal)
                .ToArray()
            : [];

    public static PolicyParticipantRegistry Create(
        ResolvedExtensionGraph graph,
        IEnumerable<IExtensionModule> modules,
        CapabilityBroker broker)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(broker);
        var selected = graph.Extensions
            .Select(extension => extension.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = modules
            .Where(module => selected.Contains(module.Contribution.Manifest.Id))
            .OrderBy(module => module.Contribution.Manifest.Id, StringComparer.Ordinal)
            .ToArray();
        var registry = new PolicyParticipantRegistry(active);
        foreach (var module in active)
        {
            var extensionId = module.Contribution.Manifest.Id;
            module.RegisterPolicyParticipants(registry, broker.ForOwner(extensionId));
        }

        return registry;
    }

    public void ValidateRequirement<TContext>(
        string policyPoint,
        PolicyAggregationRequirement requirement)
    {
        var authoritative = Get<TContext>(policyPoint)
            .Where(participant => participant.IsAuthoritative)
            .Select(participant => participant.ParticipantId)
            .ToHashSet(StringComparer.Ordinal);
        var missing = requirement.RequiredAuthoritativeParticipants
            .Where(id => !authoritative.Contains(id))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (authoritative.Count < requirement.MinimumAuthoritativeParticipants ||
            missing.Length > 0)
        {
            throw new ServerHostingConfigurationException(
                $"catalog.missing-active-authoritative-policy-participant: Policy point " +
                $"'{policyPoint}' requires active authoritative participants for context " +
                $"'{typeof(TContext).Name}'. Missing: {string.Join(", ", missing)}.");
        }
    }
}

internal sealed class SupplyChainPolicyEvaluator
{
    private readonly PolicyParticipantRegistry _registry;
    private readonly IReadOnlyDictionary<string, PolicyAggregationRequirement> _requirements;

    public SupplyChainPolicyEvaluator(
        PolicyParticipantRegistry registry,
        IReadOnlyDictionary<string, PolicyAggregationRequirement> requirements)
    {
        _registry = registry;
        _requirements = requirements;
        foreach (var requirement in requirements)
        {
            registry.ValidateRequirement<SupplyChainPolicyContext>(
                requirement.Key,
                requirement.Value);
        }
    }

    public ValueTask<PolicyAggregationResult> EvaluateAdmissionAsync(
        SupplyChainPolicyContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(SupplyChainPolicyPoints.Admission, context, cancellationToken);

    public ValueTask<PolicyAggregationResult> EvaluateValidationAsync(
        SupplyChainPolicyContext context,
        CancellationToken cancellationToken) =>
        EvaluateAsync(SupplyChainPolicyPoints.Validation, context, cancellationToken);

    private ValueTask<PolicyAggregationResult> EvaluateAsync(
        string policyPoint,
        SupplyChainPolicyContext context,
        CancellationToken cancellationToken) =>
        PolicyParticipantAggregator.EvaluateAsync(
            policyPoint,
            context,
            _registry.Get<SupplyChainPolicyContext>(policyPoint),
            _requirements[policyPoint],
            cancellationToken);
}
