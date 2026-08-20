using System.Collections.Immutable;
using System.Text;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Hosting;

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

internal sealed record RouteDescriptor(
    string Method,
    string Path,
    bool RequiresProductionIdentity = false)
{
    public bool AppliesTo(bool hasProductionIdentity) =>
        !RequiresProductionIdentity || hasProductionIdentity;
}

internal sealed record ServiceIndexResourceDescriptor(
    string ResourceType,
    ImmutableArray<string> RequiresResourceTypes);

internal sealed record ExtensionManifest(
    int SchemaVersion,
    string Id,
    ExtensionVersion Version,
    ExtensionVersionRange HostCompatibility,
    ImmutableArray<ExtensionDependency> Dependencies,
    ImmutableArray<string> Operations,
    ImmutableArray<RouteDescriptor> Routes,
    ImmutableArray<ServiceIndexResourceDescriptor> Resources,
    ImmutableArray<CapabilityRequest> RequestedCapabilities);

internal sealed record ResolvedOperation(string OperationId, string ExtensionId);

internal sealed record ResolvedRoute(string Method, string Path, string ExtensionId);

internal sealed record ResolvedServiceIndexResource(string ResourceType, string ExtensionId);

internal sealed record ResolvedCapability(
    string Name,
    string ExtensionId,
    bool IsRequired,
    bool IsGranted);

internal sealed record ResolvedExtensionGraph(
    string ProfileName,
    ImmutableArray<ExtensionManifest> Extensions,
    ImmutableArray<ResolvedOperation> Operations,
    ImmutableArray<ResolvedRoute> Routes,
    ImmutableArray<ResolvedServiceIndexResource> Resources,
    ImmutableArray<ResolvedCapability> Capabilities,
    string Diagnostics)
{
    public ResolvedExtensionGraph(
        string profileName,
        ImmutableArray<ExtensionManifest> extensions,
        ImmutableArray<ResolvedOperation> operations,
        ImmutableArray<ResolvedRoute> routes,
        ImmutableArray<ResolvedServiceIndexResource> resources,
        string diagnostics)
        : this(profileName, extensions, operations, routes, resources, [], diagnostics)
    {
    }
}

internal sealed class ExtensionCatalog
{
    private static readonly ExtensionVersion HostVersion = new(1, 0, 0);
    private readonly ImmutableDictionary<string, ExtensionManifest> _manifests;

    public ExtensionCatalog(IEnumerable<ExtensionManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        var ordered = manifests.OrderBy(manifest => manifest.Id, ExtensionIdComparer.Instance).ToArray();
        var duplicate = ordered
            .GroupBy(manifest => manifest.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            var ids = duplicate.Select(manifest => manifest.Id).Order(StringComparer.Ordinal).ToArray();
            throw Failure(
                "duplicate-extension",
                $"Extension IDs '{ids[0]}' and '{ids[1]}' differ only by case.");
        }

        _manifests = ordered.ToImmutableDictionary(
            manifest => manifest.Id,
            StringComparer.OrdinalIgnoreCase);
    }

    public ResolvedExtensionGraph Resolve(
        ServerProfile profile,
        bool hasProductionIdentity = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var selected = ResolveSelectedManifests(profile);
        var selectedById = selected.ToDictionary(
            manifest => manifest.Id,
            StringComparer.OrdinalIgnoreCase);
        ValidateManifests(selected);
        ValidateDependencies(selectedById);
        var ordered = OrderByDependencies(selectedById);
        ValidateProductionCapabilityPolicy(profile);
        var capabilities = ResolveCapabilities(profile, ordered);

        var operations = ResolveOperations(ordered);
        var routes = ResolveRoutes(ordered, hasProductionIdentity);
        var resources = ResolveResources(ordered);
        ValidateResourceLinks(ordered, resources);
        var diagnostics = CreateDiagnostics(profile, ordered, routes, resources, capabilities);

        return new ResolvedExtensionGraph(
            profile.Name,
            [.. ordered],
            operations,
            routes,
            resources,
            capabilities,
            diagnostics);
    }

    private ExtensionManifest[] ResolveSelectedManifests(ServerProfile profile)
    {
        var duplicate = profile.Extensions
            .GroupBy(extension => extension.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw Failure(
                "duplicate-selection",
                $"Profile '{profile.Name}' selects extension '{duplicate.Key}' more than once.");
        }

        return profile.Extensions
            .OrderBy(extension => extension.Id, ExtensionIdComparer.Instance)
            .Select(extension =>
            {
                if (!_manifests.TryGetValue(extension.Id, out var manifest))
                {
                    throw Failure(
                        "missing-extension",
                        $"Profile '{profile.Name}' selects unknown extension '{extension.Id}'.");
                }

                return manifest;
            })
            .ToArray();
    }

    private static void ValidateManifests(IReadOnlyList<ExtensionManifest> manifests)
    {
        foreach (var manifest in manifests)
        {
            if (manifest.SchemaVersion != 1)
            {
                throw Failure(
                    "unsupported-manifest-schema",
                    $"Extension '{manifest.Id}' uses unsupported manifest schema " +
                    $"'{manifest.SchemaVersion}'.");
            }

            if (!manifest.HostCompatibility.Contains(HostVersion))
            {
                throw Failure(
                    "incompatible-host",
                    $"Extension '{manifest.Id}' requires host range " +
                    $"'{manifest.HostCompatibility}', but host version '{HostVersion}' is running.");
            }
        }
    }

    private static void ValidateDependencies(
        IReadOnlyDictionary<string, ExtensionManifest> selected)
    {
        foreach (var manifest in selected.Values.OrderBy(value => value.Id, ExtensionIdComparer.Instance))
        {
            foreach (var dependency in manifest.Dependencies
                         .OrderBy(value => value.ExtensionId, ExtensionIdComparer.Instance))
            {
                if (!selected.TryGetValue(dependency.ExtensionId, out var provider))
                {
                    throw Failure(
                        "missing-dependency",
                        $"Extension '{manifest.Id}' requires missing extension " +
                        $"'{dependency.ExtensionId}' in range '{dependency.VersionRange}'.");
                }

                if (!dependency.VersionRange.Contains(provider.Version))
                {
                    throw Failure(
                        "incompatible-dependency",
                        $"Extension '{manifest.Id}' requires '{dependency.ExtensionId}' in range " +
                        $"'{dependency.VersionRange}', but version '{provider.Version}' is selected.");
                }
            }
        }
    }

    private static ExtensionManifest[] OrderByDependencies(
        IReadOnlyDictionary<string, ExtensionManifest> selected)
    {
        var states = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        var result = new List<ExtensionManifest>(selected.Count);

        foreach (var manifest in selected.Values.OrderBy(value => value.Id, ExtensionIdComparer.Instance))
        {
            Visit(manifest);
        }

        return [.. result];

        void Visit(ExtensionManifest manifest)
        {
            if (states.TryGetValue(manifest.Id, out var state))
            {
                if (state == VisitState.Visited)
                {
                    return;
                }

                var cycleStart = stack.FindIndex(
                    id => StringComparer.OrdinalIgnoreCase.Equals(id, manifest.Id));
                var cycle = stack.Skip(cycleStart).Append(manifest.Id);
                throw Failure(
                    "dependency-cycle",
                    $"Dependency cycle detected: {string.Join(" -> ", cycle)}.");
            }

            states[manifest.Id] = VisitState.Visiting;
            stack.Add(manifest.Id);
            foreach (var dependency in manifest.Dependencies
                         .OrderBy(value => value.ExtensionId, ExtensionIdComparer.Instance))
            {
                Visit(selected[dependency.ExtensionId]);
            }

            stack.RemoveAt(stack.Count - 1);
            states[manifest.Id] = VisitState.Visited;
            result.Add(manifest);
        }
    }

    private static ImmutableArray<ResolvedCapability> ResolveCapabilities(
        ServerProfile profile,
        IReadOnlyList<ExtensionManifest> manifests)
    {
        var grants = profile.Grants
            .Select(grant => grant.Name)
            .ToHashSet(StringComparer.Ordinal);
        var resolved = new List<ResolvedCapability>();
        var selections = profile.Extensions.ToDictionary(
            extension => extension.Id,
            StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in manifests)
        {
            var requested = manifest.RequestedCapabilities
                .Concat(selections[manifest.Id].RequestedCapabilities)
                .GroupBy(request => request.Name, StringComparer.Ordinal)
                .Select(group => new CapabilityRequest(
                    group.Key,
                    group.Any(request => request.IsRequired)))
                .OrderBy(request => request.Name, StringComparer.Ordinal);
            foreach (var capability in requested)
            {
                var granted = grants.Contains(capability.Name);
                if (capability.IsRequired && !granted)
                {
                    throw Failure(
                        "missing-capability-grant",
                        $"Extension '{manifest.Id}' requires ungranted capability " +
                        $"'{capability.Name}'.");
                }

                resolved.Add(new ResolvedCapability(
                    capability.Name,
                    manifest.Id,
                    capability.IsRequired,
                    granted));
            }
        }

        return [.. resolved];
    }

    private static void ValidateProductionCapabilityPolicy(ServerProfile profile)
    {
        var granted = profile.Grants.Select(grant => grant.Name).ToHashSet(StringComparer.Ordinal);
        if (profile.Kind == ServerProfileKind.Embedded)
        {
            string[] embeddedDenied =
            [
                BuiltInCapabilityNames.OutboundHttp,
                BuiltInCapabilityNames.SecretsResolveReference,
                BuiltInCapabilityNames.SidecarExecution
            ];
            var embeddedViolation = embeddedDenied.FirstOrDefault(granted.Contains);
            if (embeddedViolation is not null)
            {
                throw Failure(
                    "embedded-capability-denied",
                    $"Embedded profile cannot grant capability '{embeddedViolation}'.");
            }
        }

        if (profile.Kind != ServerProfileKind.Production)
        {
            return;
        }

        string[] denied =
        [
            BuiltInCapabilityNames.ControlFaultsInject,
            BuiltInCapabilityNames.ControlRequestsRead,
            BuiltInCapabilityNames.SecretsResolveReference,
            BuiltInCapabilityNames.SidecarExecution
        ];
        var requested = profile.Extensions
            .SelectMany(extension => extension.RequestedCapabilities)
            .Select(request => request.Name)
            .ToHashSet(StringComparer.Ordinal);
        var violation = denied.FirstOrDefault(
            capability => granted.Contains(capability) || requested.Contains(capability));
        if (violation is not null)
        {
            throw Failure(
                "production-capability-denied",
                $"Production profile cannot grant capability '{violation}'.");
        }
    }

    private static ImmutableArray<ResolvedOperation> ResolveOperations(
        IReadOnlyList<ExtensionManifest> manifests)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var manifest in manifests.OrderBy(value => value.Id, ExtensionIdComparer.Instance))
        {
            foreach (var operation in manifest.Operations.Order(StringComparer.Ordinal))
            {
                if (owners.TryGetValue(operation, out var existingOwner))
                {
                    throw Failure(
                        "operation-owner-conflict",
                        $"Operation '{operation}' is owned by '{existingOwner}' and '{manifest.Id}'.");
                }

                owners.Add(operation, manifest.Id);
            }
        }

        return
        [
            .. owners.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ResolvedOperation(pair.Key, pair.Value))
        ];
    }

    private static ImmutableArray<ResolvedRoute> ResolveRoutes(
        IReadOnlyList<ExtensionManifest> manifests,
        bool hasProductionIdentity)
    {
        var owners = new Dictionary<string, ResolvedRoute>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests.OrderBy(value => value.Id, ExtensionIdComparer.Instance))
        {
            foreach (var route in manifest.Routes
                         .Where(route => route.AppliesTo(hasProductionIdentity))
                         .OrderBy(route => route.Method, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(route => route.Path, StringComparer.OrdinalIgnoreCase))
            {
                var normalized = new ResolvedRoute(
                    route.Method.ToUpperInvariant(),
                    route.Path,
                    manifest.Id);
                var key = $"{normalized.Method} {normalized.Path}";
                if (owners.TryGetValue(key, out var existing))
                {
                    throw Failure(
                        "route-conflict",
                        $"Route '{existing.Method} {existing.Path}' is owned by " +
                        $"'{existing.ExtensionId}' and '{manifest.Id}'.");
                }

                owners.Add(key, normalized);
            }
        }

        return
        [
            .. owners.Values
                .OrderBy(route => route.Method, StringComparer.Ordinal)
                .ThenBy(route => route.Path, StringComparer.Ordinal)
        ];
    }

    private static ImmutableArray<ResolvedServiceIndexResource> ResolveResources(
        IReadOnlyList<ExtensionManifest> manifests)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var manifest in manifests.OrderBy(value => value.Id, ExtensionIdComparer.Instance))
        {
            foreach (var resource in manifest.Resources.OrderBy(
                         value => value.ResourceType,
                         StringComparer.Ordinal))
            {
                if (owners.TryGetValue(resource.ResourceType, out var existingOwner))
                {
                    throw Failure(
                        "resource-owner-conflict",
                        $"Resource '{resource.ResourceType}' is owned by " +
                        $"'{existingOwner}' and '{manifest.Id}'.");
                }

                owners.Add(resource.ResourceType, manifest.Id);
            }
        }

        return
        [
            .. owners.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new ResolvedServiceIndexResource(pair.Key, pair.Value))
        ];
    }

    private static void ValidateResourceLinks(
        IReadOnlyList<ExtensionManifest> manifests,
        ImmutableArray<ResolvedServiceIndexResource> resources)
    {
        var available = resources
            .Select(resource => resource.ResourceType)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var manifest in manifests.OrderBy(value => value.Id, ExtensionIdComparer.Instance))
        {
            foreach (var resource in manifest.Resources.OrderBy(
                         value => value.ResourceType,
                         StringComparer.Ordinal))
            {
                foreach (var required in resource.RequiresResourceTypes.Order(StringComparer.Ordinal))
                {
                    if (!available.Contains(required))
                    {
                        throw Failure(
                            "missing-linked-resource",
                            $"Resource '{resource.ResourceType}' from '{manifest.Id}' requires " +
                            $"missing resource '{required}'.");
                    }
                }
            }
        }
    }

    private static string CreateDiagnostics(
        ServerProfile profile,
        IReadOnlyList<ExtensionManifest> manifests,
        ImmutableArray<ResolvedRoute> routes,
        ImmutableArray<ResolvedServiceIndexResource> resources,
        ImmutableArray<ResolvedCapability> resolvedCapabilities)
    {
        var builder = new StringBuilder();
        builder.Append("profile=").Append(profile.Name).Append('\n');
        foreach (var manifest in manifests)
        {
            var capabilities = manifest.RequestedCapabilities
                .Concat(profile.Extensions.Single(
                    extension => StringComparer.OrdinalIgnoreCase.Equals(
                        extension.Id,
                        manifest.Id)).RequestedCapabilities)
                .Select(capability => capability.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var ownedRoutes = routes
                .Where(route => route.ExtensionId == manifest.Id)
                .Select(route => $"{route.Method} {route.Path}")
                .Order(StringComparer.Ordinal)
                .ToArray();
            var ownedResources = resources
                .Where(resource => resource.ExtensionId == manifest.Id)
                .Select(resource => resource.ResourceType)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var omittedOptional = resolvedCapabilities
                .Where(capability =>
                    capability.ExtensionId == manifest.Id &&
                    !capability.IsRequired &&
                    !capability.IsGranted)
                .Select(capability => capability.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();

            builder.Append("extension=").Append(manifest.Id)
                .Append(" version=").Append(manifest.Version)
                .Append(" capabilities=").Append(ListOrDash(capabilities))
                .Append(" routes=").Append(ListOrDash(ownedRoutes))
                .Append(" resources=").Append(ListOrDash(ownedResources))
                .Append(" omitted-optional=").Append(ListOrDash(omittedOptional))
                .Append('\n');
        }

        return builder.ToString();
    }

    private static string ListOrDash(IReadOnlyList<string> values) =>
        values.Count == 0 ? "-" : string.Join(",", values);

    private static ServerHostingConfigurationException Failure(string code, string message) =>
        new($"catalog.{code}: {message}");

    private enum VisitState
    {
        Visiting,
        Visited
    }

    private sealed class ExtensionIdComparer : IComparer<string>, IComparer<ExtensionManifest>
    {
        public static ExtensionIdComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            var insensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return insensitive != 0 ? insensitive : StringComparer.Ordinal.Compare(left, right);
        }

        public int Compare(ExtensionManifest? left, ExtensionManifest? right) =>
            Compare(left?.Id, right?.Id);
    }
}

internal static class BuiltInExtensionCatalog
{
    private static readonly ExtensionVersion Version = new(1, 0, 0);
    private static readonly ExtensionVersionRange Compatibility = ExtensionVersionRange.Major(1);

    public static ExtensionCatalog Instance { get; } = new(
    [
        Manifest(
            BuiltInExtensionIds.Protocol,
            operations: Operations(
                OperationFamily.ServiceIndex,
                OperationFamily.FlatContainer,
                OperationFamily.Registration,
                OperationFamily.Search),
            routes:
            [
                .. ReadRoutes(
                    "/v3/index.json",
                    "/flatcontainer/{id}/index.json",
                    "/flatcontainer/{id}/{version}/{fileName}",
                    "/registration/{id}/index.json",
                    "/registration/{id}/page/{lower}/{upper}.json",
                    "/registration/{id}/{version}.json",
                    "/query")
            ],
            resources:
            [
                new("PackageBaseAddress/3.0.0", []),
                new("RegistrationsBaseUrl/3.6.0", ["PackageBaseAddress/3.0.0"]),
                new(
                    "SearchQueryService/3.0.0-beta",
                    ["PackageBaseAddress/3.0.0", "RegistrationsBaseUrl/3.6.0"]),
                new(
                    "SearchQueryService/3.5.0",
                    ["PackageBaseAddress/3.0.0", "RegistrationsBaseUrl/3.6.0"])
            ],
            capabilities:
            [
                Required(BuiltInCapabilityNames.PackagesIdentityRead),
                Required(BuiltInCapabilityNames.PackagesMetadataRead),
                Required(BuiltInCapabilityNames.PackagesContentRead),
                Required(BuiltInCapabilityNames.VulnerabilityStateRead)
            ]),
        Manifest(
            BuiltInExtensionIds.Publication,
            dependencies: [Dependency(BuiltInExtensionIds.Protocol)],
            operations: Operations(OperationFamily.PackageManagement),
            routes:
            [
                new("PUT", "/package"),
                new("PUT", "/symbolpackage"),
                new("DELETE", "/package/{id}/{version}"),
                new(
                    "DELETE",
                    "/package/{id}/{version}/hard",
                    RequiresProductionIdentity: true)
            ],
            resources:
            [
                new("PackagePublish/2.0.0", []),
                new("SymbolPackagePublish/4.9.0", [])
            ],
            capabilities:
            [
                Required(BuiltInCapabilityNames.PackagesMetadataRead),
                Required(BuiltInCapabilityNames.PackagesContentWrite),
                Required(BuiltInCapabilityNames.PackagesPublish),
                Required(BuiltInCapabilityNames.PackagesUnlist),
                Required(BuiltInCapabilityNames.PackagesRelist),
                Required(BuiltInCapabilityNames.PackagesDelete),
                Required(BuiltInCapabilityNames.EventsPublish)
            ]),
        Manifest(
            BuiltInExtensionIds.Vulnerabilities,
            operations: Operations(OperationFamily.Vulnerabilities),
            routes:
            [
                .. ReadRoutes(
                    "/v3/vulnerabilities/index.json",
                    "/v3/vulnerabilities/{snapshotId}/{pageName}.json")
            ],
            resources: [new("VulnerabilityInfo/6.7.0", [])],
            capabilities: [Required(BuiltInCapabilityNames.VulnerabilityStateRead)]),
        Manifest(
            BuiltInExtensionIds.TestControl,
            dependencies: [Dependency(BuiltInExtensionIds.Publication)],
            operations: Operations(OperationFamily.TestControl),
            routes:
            [
                new("GET", "/__test/state"),
                new("POST", "/__test/reset"),
                new("GET", "/__test/packages"),
                new("POST", "/__test/packages"),
                new("DELETE", "/__test/packages/{id}/{version}"),
                new("POST", "/__test/packages/{id}/{version}/list"),
                new("POST", "/__test/packages/{id}/{version}/unlist"),
                new("PUT", "/__test/packages/{id}/{version}/metadata"),
                new("GET", "/__test/requests"),
                new("DELETE", "/__test/requests"),
                new("GET", "/__test/faults"),
                new("POST", "/__test/faults"),
                new("DELETE", "/__test/faults")
            ],
            capabilities:
            [
                Required(BuiltInCapabilityNames.PackagesMetadataRead),
                Required(BuiltInCapabilityNames.PackagesMetadataWrite),
                Required(BuiltInCapabilityNames.PackagesContentWrite),
                Required(BuiltInCapabilityNames.PackagesPublish),
                Required(BuiltInCapabilityNames.PackagesUnlist),
                Required(BuiltInCapabilityNames.PackagesRelist),
                Required(BuiltInCapabilityNames.PackagesDelete),
                Required(BuiltInCapabilityNames.ControlFaultsInject),
                Required(BuiltInCapabilityNames.ControlRequestsRead),
                Required(BuiltInCapabilityNames.EventsPublish)
            ]),
        Manifest(
            BuiltInExtensionIds.DurableStorage,
            capabilities: [Required(BuiltInCapabilityNames.DurableStorage)]),
        Manifest(
            BuiltInExtensionIds.Operations,
            operations: Operations(
                OperationFamily.Health,
                OperationFamily.Diagnostics,
                OperationFamily.Backup,
                OperationFamily.Restore),
            routes:
            [
                new("GET", "/health/live"),
                new("GET", "/health/ready"),
                new("GET", "/health/storage"),
                new("GET", "/__test/health")
            ],
            capabilities:
            [
                Required(BuiltInCapabilityNames.OperationsQuery),
                Required(BuiltInCapabilityNames.BackupInvoke),
                Required(BuiltInCapabilityNames.RestoreInvoke)
            ]),
        Manifest(
            BuiltInExtensionIds.SupplyChain,
            dependencies: [Dependency(BuiltInExtensionIds.Publication)],
            operations: Operations(OperationFamily.Moderation),
            routes:
            [
                new("POST", "/__admin/packages/{id}/{version}/{action}"),
                new("GET", "/__admin/supply-chain/audit"),
                new("GET", "/__admin/packages/{id}/{version}/validations")
            ],
            capabilities:
            [
                Required(BuiltInCapabilityNames.ModerationRead),
                Required(BuiltInCapabilityNames.ModerationDecide)
            ]),
        Manifest(
            BuiltInExtensionIds.VulnerabilityRefresh,
            dependencies: [Dependency(BuiltInExtensionIds.Vulnerabilities)],
            capabilities: [Required(BuiltInCapabilityNames.OutboundHttp)])
    ]);

    private static ExtensionManifest Manifest(
        string id,
        ImmutableArray<ExtensionDependency> dependencies = default,
        ImmutableArray<string> operations = default,
        ImmutableArray<RouteDescriptor> routes = default,
        ImmutableArray<ServiceIndexResourceDescriptor> resources = default,
        ImmutableArray<CapabilityRequest> capabilities = default) =>
        new(
            1,
            id,
            Version,
            Compatibility,
            dependencies.IsDefault ? [] : dependencies,
            operations.IsDefault ? [] : operations,
            routes.IsDefault ? [] : routes,
            resources.IsDefault ? [] : resources,
            capabilities.IsDefault ? [] : capabilities);

    private static ExtensionDependency Dependency(string id) => new(id, Compatibility);

    private static CapabilityRequest Required(string capability) => new(capability, true);

    private static ImmutableArray<string> Operations(params OperationFamily[] families) =>
    [
        .. OperationContracts.All
            .Where(contract => families.Contains(contract.Family))
            .Select(contract => contract.Id.Value)
            .Order(StringComparer.Ordinal)
    ];

    private static ImmutableArray<RouteDescriptor> ReadRoutes(params string[] paths) =>
    [
        .. paths.SelectMany(path => new[]
        {
            new RouteDescriptor("GET", path),
            new RouteDescriptor("HEAD", path)
        })
    ];
}
