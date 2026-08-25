using System.Collections.Immutable;
using System.Reflection;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Extensions.TestKit;

public sealed class ManifestBuilder
{
    private ExtensionIdentity? _identity;
    private SdkCompatibilityRange _sdk = new(
        ExtensionSdkVersions.OldestSupported,
        new SdkContractVersion(2, 0, 0));
    private readonly List<OperationDeclaration> _operations = [];
    private readonly List<ContributionDeclaration> _contributions = [];
    private readonly List<RouteDeclaration> _routes = [];
    private readonly List<CapabilityRequest> _capabilities = [];

    public ManifestBuilder WithIdentity(string id, string version, string publisher)
    {
        _identity = new ExtensionIdentity(id, version, publisher);
        return this;
    }

    public ManifestBuilder TargetSdk(
        SdkContractVersion minimum,
        SdkContractVersion maximumExclusive)
    {
        _sdk = new SdkCompatibilityRange(minimum, maximumExclusive);
        return this;
    }

    public ManifestBuilder AddOperation(OperationDeclaration operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _operations.Add(operation);
        return this;
    }

    public ManifestBuilder AddContribution(ContributionDeclaration contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        _contributions.Add(contribution);
        return this;
    }

    public ManifestBuilder AddRoute(RouteDeclaration route)
    {
        ArgumentNullException.ThrowIfNull(route);
        _routes.Add(route);
        return this;
    }

    public ManifestBuilder RequireCapability(string name) =>
        AddCapability(name, CapabilityRequirement.Required);

    public ManifestBuilder OptionalCapability(string name) =>
        AddCapability(name, CapabilityRequirement.Optional);

    public ExtensionManifest Build()
    {
        if (_identity is null)
        {
            throw new InvalidOperationException("An extension identity is required.");
        }

        return new ExtensionManifest(
            ExtensionSdkVersions.ManifestV1,
            _identity,
            _sdk,
            new ContractVersionSet(
                ExtensionSdkVersions.ManifestV1,
                ExtensionSdkVersions.OperationV1,
                ExtensionSdkVersions.ContributionV1,
                ExtensionSdkVersions.RouteV1,
                ExtensionSdkVersions.CapabilityV1,
                ExtensionSdkVersions.StructuralV1),
            [.. _operations.OrderBy(value => value.Identity.Value, StringComparer.Ordinal)],
            [.. _contributions.OrderBy(value => value.Identity.Value, StringComparer.Ordinal)],
            [.. _routes.OrderBy(value => value.Identity.Value, StringComparer.Ordinal)],
            [.. _capabilities.OrderBy(value => value.Identity.Value, StringComparer.Ordinal)]);
    }

    private ManifestBuilder AddCapability(string name, CapabilityRequirement requirement)
    {
        _capabilities.Add(new CapabilityRequest(new CapabilityIdentity(name), requirement));
        return this;
    }
}

public sealed class FakeHostClock(DateTimeOffset utcNow) : IHostClockCapability
{
    public ValueTask<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(utcNow);
    }
}

public static class ConformanceCheck
{
    public static ConformanceResult Validate(Assembly assembly) =>
        ExtensionConformance.ValidateAssembly(assembly);
}
