using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Abstractions;

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

/// <summary>A capability an extension requests. Grants are deny-by-default.</summary>
internal sealed record CapabilityRequest(string Name, bool IsRequired);

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
internal sealed record ExtensionManifest(
    int SchemaVersion,
    string Id,
    ExtensionVersion Version,
    ExtensionVersionRange HostCompatibility,
    ImmutableArray<ExtensionDependency> Dependencies,
    ImmutableArray<string> Operations,
    ImmutableArray<EndpointDescriptor> Endpoints,
    ImmutableArray<ServiceResourceContribution> Resources,
    ImmutableArray<CapabilityRequest> RequestedCapabilities);

/// <summary>
/// The registration surface the kernel hands to a module. A module registers typed
/// operation owners and nothing else; it never receives the operation registry
/// implementation, dependency injection, or the route table.
/// </summary>
internal interface IOperationOwnerRegistry
{
    IOperationOwnerRegistry Register<TRequest, TResponse>(
        string extensionId,
        IOperationOwner<TRequest, TResponse> owner);
}

/// <summary>
/// The capability surface the kernel hands to a module. Capabilities are denied by
/// default; the broker scopes every handle to this host instance and extension.
/// </summary>
internal interface IExtensionCapabilities
{
    ImmutableHashSet<string> GrantedCapabilities { get; }

    TCapability GetRequired<TCapability>(string capabilityName) where TCapability : class;

    bool TryGet<TCapability>(string capabilityName, out TCapability? capability)
        where TCapability : class;
}

/// <summary>
/// Everything a module declares before the host resolves the extension graph: its
/// manifest and the operation contracts it introduces.
/// </summary>
internal sealed record ExtensionModuleContribution(
    ExtensionManifest Manifest,
    ImmutableArray<OperationBinding> Contracts)
{
    /// <summary>The profile selection a host uses to activate this module.</summary>
    public ExtensionSelection Selection => new(Manifest.Id, Manifest.RequestedCapabilities);
}

/// <summary>
/// A separately compiled extension module. One coherent entry point contributes the
/// module's identity, operations, routes, resources, and requested capabilities.
/// Modules never receive <c>WebApplication</c>, the root service provider, endpoint
/// routing, middleware registration, or an the HTTP request context.
/// </summary>
internal interface IExtensionModule
{
    ExtensionModuleContribution Contribution { get; }

    void RegisterOperations(
        IOperationOwnerRegistry registry,
        IExtensionCapabilities capabilities);
}
