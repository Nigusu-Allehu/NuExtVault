using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Sdk;

internal sealed record ExtensionVersion(int Major, int Minor, int Patch) :
    IComparable<ExtensionVersion>
{
    public int CompareTo(ExtensionVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

internal sealed record ExtensionVersionRange(ExtensionVersion Minimum, ExtensionVersion Maximum)
{
    public static ExtensionVersionRange Major(int major) =>
        new(new ExtensionVersion(major, 0, 0), new ExtensionVersion(major + 1, 0, 0));

    public bool Contains(ExtensionVersion version) =>
        version.CompareTo(Minimum) >= 0 && version.CompareTo(Maximum) < 0;

    public override string ToString() => $"[{Minimum},{Maximum})";
}

internal sealed record ExtensionDependency(
    string ExtensionId,
    ExtensionVersionRange VersionRange);

/// <summary>
/// The profile-level selection of one extension, plus any capabilities the host asks
/// for on top of the manifest.
/// </summary>
internal sealed record ExtensionSelection(
    string Id,
    ImmutableArray<CapabilityRequest> RequestedCapabilities);

/// <summary>
/// The declarative identity of an extension: what it is, what it depends on, what
/// operations it owns, which routes and service-index resources it contributes, and
/// which capabilities it requests.
/// </summary>
public sealed record ExtensionManifest : IEquatable<ExtensionManifest>
{
    internal const string ManifestV1Schema =
        "https://schemas.nutestserver.dev/extensions/manifest/v1";

    public ExtensionManifest(
        ManifestSchemaVersion schemaVersion,
        ExtensionIdentity identity,
        SdkCompatibilityRange sdk,
        ContractVersionSet contracts,
        ImmutableArray<OperationDeclaration> operations,
        ImmutableArray<ContributionDeclaration> contributions,
        ImmutableArray<RouteDeclaration> routes,
        ImmutableArray<CapabilityRequest> capabilities)
        : this(
            schemaVersion,
            identity,
            sdk,
            contracts,
            operations,
            contributions,
            routes,
            capabilities,
            null)
    {
    }

    public ExtensionManifest(
        ManifestSchemaVersion schemaVersion,
        ExtensionIdentity identity,
        SdkCompatibilityRange sdk,
        ContractVersionSet contracts,
        ImmutableArray<OperationDeclaration> operations,
        ImmutableArray<ContributionDeclaration> contributions,
        ImmutableArray<RouteDeclaration> routes,
        ImmutableArray<CapabilityRequest> capabilities,
        ExtensionStateDeclaration? state)
    {
        SchemaVersion = schemaVersion;
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Sdk = sdk ?? throw new ArgumentNullException(nameof(sdk));
        Contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
        Operations = Initialized(operations, nameof(operations));
        Contributions = Initialized(contributions, nameof(contributions));
        Routes = Initialized(routes, nameof(routes));
        Capabilities = Initialized(capabilities, nameof(capabilities));
        State = state;

        Id = identity.Id;
        Version = ExtensionVersionParser.Parse(identity.Version);
        HostCompatibility = ExtensionVersionRange.Major(sdk.Minimum.Major);
        Dependencies = [];
        OwnedOperations = [.. Operations.Select(operation => operation.Identity.Value)];
        Endpoints = [];
        Resources = [];
        SchemaUri = ManifestV1Schema;
    }

    internal ExtensionManifest(
        int SchemaVersion,
        string Id,
        ExtensionVersion Version,
        ExtensionVersionRange HostCompatibility,
        ImmutableArray<ExtensionDependency> Dependencies,
        ImmutableArray<string> Operations,
        ImmutableArray<EndpointDescriptor> Endpoints,
        ImmutableArray<ServiceResourceContribution> Resources,
        ImmutableArray<CapabilityRequest> RequestedCapabilities)
    {
        this.SchemaVersion = new ManifestSchemaVersion(SchemaVersion);
        Identity = new ExtensionIdentity(Id, Version.ToString(), "NuGet.TestServer");
        Sdk = new SdkCompatibilityRange(
            ExtensionSdkVersions.OldestSupported,
            new SdkContractVersion(2, 0, 0));
        Contracts = new ContractVersionSet(
            ExtensionSdkVersions.ManifestV1,
            ExtensionSdkVersions.OperationV1,
            ExtensionSdkVersions.ContributionV1,
            ExtensionSdkVersions.RouteV1,
            ExtensionSdkVersions.CapabilityV1,
            ExtensionSdkVersions.StructuralV1);
        this.Operations =
        [
            .. Operations.Select(operation =>
            {
                var binding = OperationContracts.Bindings.FirstOrDefault(
                    candidate => candidate.Contract.Id.Value == operation);
                return new OperationDeclaration(
                    new OperationIdentity(operation),
                    ExtensionSdkVersions.OperationV1,
                    binding?.Contract.RequestContract ?? $"{operation}.Request.v1",
                    binding?.Contract.ResponseContract ?? $"{operation}.Response.v1",
                    OperationOwnership.New,
                    AllowReplacement: false);
            })
        ];
        Contributions =
        [
            .. Resources.Select(resource => new ContributionDeclaration(
                new ContributionIdentity(
                    $"{Id}.{resource.ResourceType}.{resource.Version.Replace('.', '-')}"),
                "service-resource",
                ExtensionSdkVersions.ContributionV1))
        ];
        Routes =
        [
            .. Endpoints.Select(endpoint => new RouteDeclaration(
                new RouteIdentity(endpoint.Name),
                new OperationIdentity(endpoint.Operations[0].OperationId),
                ExtensionSdkVersions.RouteV1,
                endpoint.Methods,
                endpoint.PathTemplate,
                endpoint.Limits.MaxRequestBytes,
                endpoint.Limits.MaxContentBytes))
        ];
        Capabilities = RequestedCapabilities;

        this.Id = Id;
        this.Version = Version;
        this.HostCompatibility = HostCompatibility;
        this.Dependencies = Dependencies;
        OwnedOperations = Operations;
        this.Endpoints = Endpoints;
        this.Resources = Resources;
        SchemaUri = ManifestV1Schema;
    }

    public ManifestSchemaVersion SchemaVersion { get; }

    public ExtensionIdentity Identity { get; }

    public SdkCompatibilityRange Sdk { get; }

    public ContractVersionSet Contracts { get; }

    public ImmutableArray<OperationDeclaration> Operations { get; }

    public ImmutableArray<ContributionDeclaration> Contributions { get; }

    public ImmutableArray<RouteDeclaration> Routes { get; }

    public ImmutableArray<CapabilityRequest> Capabilities { get; }

    /// <summary>
    /// The authoritative extension state this extension owns, or <c>null</c> when it
    /// keeps no kernel-managed state.
    /// </summary>
    public ExtensionStateDeclaration? State { get; init; }

    internal string Id { get; }

    internal ExtensionVersion Version { get; }

    internal ExtensionVersionRange HostCompatibility { get; }

    internal ImmutableArray<ExtensionDependency> Dependencies { get; }

    internal ImmutableArray<string> OwnedOperations { get; init; }

    internal ImmutableArray<EndpointDescriptor> Endpoints { get; }

    internal ImmutableArray<ServiceResourceContribution> Resources { get; init; }

    internal ImmutableArray<CapabilityRequest> RequestedCapabilities => Capabilities;

    internal string SchemaUri { get; }

    internal string? ValidatedManifestDigest { get; init; }

    internal string? ValidatedStagedContentDigest { get; init; }

    public bool Equals(ExtensionManifest? other) =>
        other is not null &&
        SchemaVersion == other.SchemaVersion &&
        Identity == other.Identity &&
        Sdk == other.Sdk &&
        Contracts == other.Contracts &&
        Operations.SequenceEqual(other.Operations) &&
        Contributions.SequenceEqual(other.Contributions) &&
        Routes.SequenceEqual(other.Routes, RouteDeclarationComparer.Instance) &&
        Capabilities.SequenceEqual(other.Capabilities) &&
        State == other.State;

    public override int GetHashCode() => HashCode.Combine(SchemaVersion, Identity, Sdk, Contracts);

    private static ImmutableArray<T> Initialized<T>(
        ImmutableArray<T> values,
        string parameterName) =>
        values.IsDefault
            ? throw new ArgumentException("The collection must be initialized.", parameterName)
            : values;

    private sealed class RouteDeclarationComparer : IEqualityComparer<RouteDeclaration>
    {
        internal static RouteDeclarationComparer Instance { get; } = new();

        public bool Equals(RouteDeclaration? left, RouteDeclaration? right) =>
            ReferenceEquals(left, right) ||
            left is not null &&
            right is not null &&
            left.Identity == right.Identity &&
            left.Operation == right.Operation &&
            left.Version == right.Version &&
            left.Methods.SequenceEqual(right.Methods) &&
            left.Path == right.Path &&
            left.MaximumRequestBytes == right.MaximumRequestBytes &&
            left.MaximumResponseBytes == right.MaximumResponseBytes &&
            left.Access == right.Access &&
            left.Head == right.Head &&
            left.TimeoutMilliseconds == right.TimeoutMilliseconds &&
            left.DeclaredBody == right.DeclaredBody &&
            left.Headers.SequenceEqual(right.Headers, StringComparer.Ordinal);

        public int GetHashCode(RouteDeclaration value) => value.Identity.GetHashCode();
    }

    private static class ExtensionVersionParser
    {
        internal static ExtensionVersion Parse(string version) =>
            SdkContractVersion.TryParse(version, out var parsed)
                ? new ExtensionVersion(parsed.Major, parsed.Minor, parsed.Patch)
                : throw new ArgumentException(
                    "Extension versions must use major.minor.patch.",
                    nameof(version));
    }
}

/// <summary>
/// Declares one policy participant before activation. Policy aggregation never depends
/// on module registration order.
/// </summary>
internal sealed record PolicyParticipantDescriptor(
    string PolicyPoint,
    string ParticipantId,
    bool IsAuthoritative);

internal sealed record PolicyParticipantRegistration<TContext>(
    string PolicyPoint,
    string ParticipantId,
    bool IsAuthoritative,
    IPolicyParticipant<TContext> Participant);

internal interface IPolicyParticipantRegistry
{
    IPolicyParticipantRegistry Register<TContext>(
        string extensionId,
        PolicyParticipantRegistration<TContext> participant);
}

/// <summary>
/// The registration surface the kernel hands to a module. A module registers typed
/// operation owners and nothing else; it never receives the operation registry
/// implementation, dependency injection, or the route table.
/// </summary>
public interface IOperationOwnerRegistry
{
    OperationDeclaration RegisterNew<TRequest, TResponse>(
        string extensionId,
        OperationIdentity identity,
        Func<TRequest, CancellationToken, ValueTask<OperationResponse<TResponse>>> handler);

    internal IOperationOwnerRegistry Register<TRequest, TResponse>(
        string extensionId,
        IOperationOwner<TRequest, TResponse> owner);
}

/// <summary>
/// The capability surface the kernel hands to a module. Capabilities are denied by
/// default; the broker scopes every handle to this host instance and extension.
/// </summary>
public interface IExtensionCapabilities
{
    TCapability GetRequired<TCapability>(CapabilityRequest request)
        where TCapability : class =>
        GetRequired<TCapability>(request.Identity.Value);

    bool TryGet<TCapability>(CapabilityRequest request, out TCapability? capability)
        where TCapability : class
    {
        if (request.Requirement != CapabilityRequirement.Optional)
        {
            throw new ArgumentException(
                "TryGet may be used only for optional capabilities.",
                nameof(request));
        }

        return TryGet(request.Identity.Value, out capability);
    }

    internal ImmutableHashSet<string> GrantedCapabilities => [];

    internal TCapability GetRequired<TCapability>(string capabilityName)
        where TCapability : class =>
        throw new NotSupportedException();

    internal bool TryGet<TCapability>(string capabilityName, out TCapability? capability)
        where TCapability : class
    {
        capability = null;
        return false;
    }
}

/// <summary>
/// Everything a module declares before the host resolves the extension graph: its
/// manifest and the operation contracts it introduces.
/// </summary>
public sealed record ExtensionModuleContribution
{
    internal ExtensionModuleContribution(
        ExtensionManifest manifest,
        ImmutableArray<OperationBinding> contracts)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Contracts = contracts;
    }

    public ExtensionManifest Manifest { get; }

    internal ImmutableArray<OperationBinding> Contracts { get; }

    internal ImmutableArray<PolicyParticipantDescriptor> PolicyParticipants { get; init; } = [];

    internal ImmutableArray<DocumentContributorDescriptor> DocumentContributors { get; init; } = [];

    /// <summary>The profile selection a host uses to activate this module.</summary>
    internal ExtensionSelection Selection => new(Manifest.Id, Manifest.RequestedCapabilities);

    public static ExtensionModuleContribution FromManifest(ExtensionManifest manifest) =>
        new(manifest, []);
}

/// <summary>
/// A separately compiled extension module. One coherent entry point contributes the
/// module's identity, operations, routes, resources, and requested capabilities.
/// Modules never receive <c>WebApplication</c>, the root service provider, endpoint
/// routing, middleware registration, or an the HTTP request context.
/// </summary>
public interface IExtensionModule
{
    ExtensionModuleContribution Contribution { get; }

    void RegisterOperations(
        IOperationOwnerRegistry operations,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource contributions);

    void RegisterRoutes(IRouteBinderRegistry routes)
    {
    }

    internal void RegisterDocumentContributors(
        IDocumentContributorRegistry registry,
        IExtensionCapabilities capabilities)
    {
    }

    internal void RegisterPolicyParticipants(
        IPolicyParticipantRegistry registry,
        IExtensionCapabilities capabilities)
    {
    }
}
