using System.Collections.Immutable;
using System.Text;
using NuGet.Versioning;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Hosting.Endpoints;
using NuExtVault.Kernel;
using NuExtVault.Kernel.Routing;

namespace NuExtVault.Hosting;

internal sealed record ResolvedOperation(string OperationId, string ExtensionId);

/// <summary>
/// A validated endpoint descriptor together with its owning extension. Every active
/// route in the host is generated from this list.
/// </summary>
internal sealed record ResolvedEndpoint(EndpointDescriptor Descriptor, string ExtensionId);

internal sealed record ResolvedRoute(string Method, string Path, string ExtensionId);

internal sealed record ResolvedServiceIndexResource(
    ServiceResourceContribution Contribution,
    string ExtensionId);

internal sealed record ResolvedCapability(
    string Name,
    string ExtensionId,
    bool IsRequired,
    bool IsGranted);

internal sealed record ResolvedExtensionGraph(
    string ProfileName,
    ImmutableArray<ExtensionManifest> Extensions,
    ImmutableArray<ResolvedOperation> Operations,
    ImmutableArray<ResolvedEndpoint> Endpoints,
    ImmutableArray<ResolvedRoute> Routes,
    ImmutableArray<ResolvedServiceIndexResource> Resources,
    ImmutableArray<ResolvedCapability> Capabilities,
    string Diagnostics);

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
        bool hasProductionIdentity = false,
        IReadOnlyDictionary<string, OperationBinding>? contracts = null)
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
        var endpoints = EndpointDescriptorValidator.Validate(
            ordered,
            operations.ToDictionary(
                operation => operation.OperationId,
                operation => operation.ExtensionId,
                StringComparer.Ordinal),
            contracts ?? DefaultContracts,
            hasProductionIdentity);
        var routes = ResolveRoutes(endpoints);
        var resources = ResolveResources(ordered, operations, routes);
        ValidateResourceLinks(resources);
        var diagnostics = CreateDiagnostics(profile, ordered, routes, resources, capabilities);

        return new ResolvedExtensionGraph(
            profile.Name,
            [.. ordered],
            operations,
            endpoints,
            routes,
            resources,
            capabilities,
            diagnostics);
    }

    private static IReadOnlyDictionary<string, OperationBinding> DefaultContracts { get; } =
        OperationContracts.Bindings.ToDictionary(
            binding => binding.Contract.Id.Value,
            StringComparer.Ordinal);

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
            BuiltInCapabilityNames.ControlPackagesManage,
            BuiltInCapabilityNames.ControlInstrumentationManage,
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
            foreach (var operation in manifest.OwnedOperations.Order(StringComparer.Ordinal))
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
        ImmutableArray<ResolvedEndpoint> endpoints) =>
    [
        .. endpoints
            .SelectMany(endpoint => EndpointDescriptorValidator
                .NormalizeMethods(endpoint.Descriptor)
                .Select(method => new ResolvedRoute(
                    method,
                    endpoint.Descriptor.PathTemplate,
                    endpoint.ExtensionId)))
            .OrderBy(route => route.Method, StringComparer.Ordinal)
            .ThenBy(route => route.Path, StringComparer.Ordinal)
    ];

    private static ImmutableArray<ResolvedServiceIndexResource> ResolveResources(
        IReadOnlyList<ExtensionManifest> manifests,
        ImmutableArray<ResolvedOperation> operations,
        ImmutableArray<ResolvedRoute> routes)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var operationOwners = operations.ToDictionary(
            operation => operation.OperationId,
            operation => operation.ExtensionId,
            StringComparer.Ordinal);
        foreach (var manifest in manifests.OrderBy(value => value.Id, ExtensionIdComparer.Instance))
        {
            foreach (var resource in manifest.Resources.OrderBy(
                         value => value.AdvertisedType,
                         StringComparer.Ordinal))
            {
                ValidateResourceContribution(manifest, resource, operationOwners, routes);
                if (owners.TryGetValue(resource.AdvertisedType, out var existingOwner))
                {
                    throw Failure(
                        "resource-owner-conflict",
                        $"Resource '{resource.AdvertisedType}' is owned by " +
                        $"'{existingOwner}' and '{manifest.Id}'.");
                }

                owners.Add(resource.AdvertisedType, manifest.Id);
            }
        }

        return
        [
            .. manifests
                .SelectMany(manifest => manifest.Resources.Select(resource =>
                    new ResolvedServiceIndexResource(resource, manifest.Id)))
                .Where(resource =>
                    resource.Contribution.Visibility == ServiceResourceVisibility.Advertised)
                .OrderBy(resource => resource.Contribution.Order)
                .ThenBy(resource => resource.Contribution.AdvertisedType, StringComparer.Ordinal)
                .ThenBy(resource => resource.ExtensionId, StringComparer.Ordinal)
        ];
    }

    private static void ValidateResourceContribution(
        ExtensionManifest manifest,
        ServiceResourceContribution resource,
        IReadOnlyDictionary<string, string> operationOwners,
        ImmutableArray<ResolvedRoute> routes)
    {
        if (string.IsNullOrWhiteSpace(resource.ResourceType) ||
            resource.ResourceType.Contains('/', StringComparison.Ordinal) ||
            !NuGetVersion.TryParse(resource.Version, out _) ||
            !resource.RouteName.StartsWith("/", StringComparison.Ordinal) ||
            resource.Order < 0)
        {
            throw Failure(
                "invalid-resource",
                $"Extension '{manifest.Id}' declares invalid resource " +
                $"'{resource.AdvertisedType}'.");
        }

        if (WellKnownResourceVersions.TryGetValue(resource.ResourceType, out var supported) &&
            !supported.Contains(resource.Version))
        {
            throw Failure(
                "unsupported-resource-version",
                $"Resource '{resource.ResourceType}' does not support version " +
                $"'{resource.Version}'.");
        }

        if (resource.Readiness != ServiceResourceReadiness.Ready)
        {
            throw Failure(
                "resource-not-ready",
                $"Selected resource '{resource.AdvertisedType}' from " +
                $"'{manifest.Id}' is not ready.");
        }

        if (!operationOwners.TryGetValue(resource.OperationId.Value, out var operationOwner))
        {
            throw Failure(
                "missing-resource-operation",
                $"Resource '{resource.AdvertisedType}' from '{manifest.Id}' requires missing " +
                $"operation '{resource.OperationId.Value}'.");
        }

        if (!StringComparer.OrdinalIgnoreCase.Equals(operationOwner, manifest.Id))
        {
            throw Failure(
                "resource-operation-owner-mismatch",
                $"Resource '{resource.AdvertisedType}' is contributed by '{manifest.Id}', but " +
                $"operation '{resource.OperationId.Value}' is owned by '{operationOwner}'.");
        }

        var ownedRoutes = routes
            .Where(route =>
                StringComparer.OrdinalIgnoreCase.Equals(route.ExtensionId, manifest.Id) &&
                RouteMatches(resource.RouteName, route.Path))
            .ToArray();
        if (ownedRoutes.Length == 0)
        {
            throw Failure(
                "missing-resource-route",
                $"Resource '{resource.AdvertisedType}' from '{manifest.Id}' requires missing " +
                $"route name '{resource.RouteName}'.");
        }

        if (!ownedRoutes.Any(route => RouteSupportsAccess(route.Method, resource.RequiredAccess)))
        {
            throw Failure(
                "resource-access-mismatch",
                $"Resource '{resource.AdvertisedType}' declares access " +
                $"'{resource.RequiredAccess}', but route name '{resource.RouteName}' has no " +
                "compatible method.");
        }

        if (RequiredResourceAccess.TryGetValue(resource.OperationId.Value, out var required) &&
            required != resource.RequiredAccess)
        {
            throw Failure(
                "resource-access-mismatch",
                $"Resource '{resource.AdvertisedType}' declares access " +
                $"'{resource.RequiredAccess}', but operation '{resource.OperationId.Value}' " +
                $"requires '{required}'.");
        }
    }

    private static bool RouteMatches(string routeName, string route) =>
        StringComparer.Ordinal.Equals(routeName, route) ||
        routeName.EndsWith("/", StringComparison.Ordinal) &&
        route.StartsWith(routeName, StringComparison.Ordinal);

    private static bool RouteSupportsAccess(string method, ServiceResourceAccess access) =>
        access switch
        {
            ServiceResourceAccess.Read => method is "GET" or "HEAD",
            ServiceResourceAccess.Write or ServiceResourceAccess.PackagePublish =>
                method is "PUT" or "POST",
            _ => false
        };

    private static void ValidateResourceLinks(
        ImmutableArray<ResolvedServiceIndexResource> resources)
    {
        var available = resources
            .Select(resource => resource.Contribution.AdvertisedType)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            foreach (var required in resource.Contribution.RequiresResourceTypes.Order(StringComparer.Ordinal))
            {
                if (!available.Contains(required))
                {
                    throw Failure(
                        "missing-linked-resource",
                        $"Resource '{resource.Contribution.AdvertisedType}' from " +
                        $"'{resource.ExtensionId}' requires missing resource '{required}'.");
                }
            }

            foreach (var produced in resource.Contribution.ProducesUrlsFor.Order(StringComparer.Ordinal))
            {
                if (!available.Contains(produced))
                {
                    throw Failure(
                        "missing-produced-resource",
                        $"Resource '{resource.Contribution.AdvertisedType}' from " +
                        $"'{resource.ExtensionId}' produces URLs for missing resource " +
                        $"'{produced}'.");
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
                .Select(resource => resource.Contribution.AdvertisedType)
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

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> WellKnownResourceVersions
    { get; } = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
    {
        ["PackageBaseAddress"] = new HashSet<string>(["3.0.0"], StringComparer.Ordinal),
        ["RegistrationsBaseUrl"] = new HashSet<string>(["3.6.0"], StringComparer.Ordinal),
        ["SearchQueryService"] = new HashSet<string>(
                ["3.0.0-beta", "3.5.0"],
                StringComparer.Ordinal),
        ["PackagePublish"] = new HashSet<string>(["2.0.0"], StringComparer.Ordinal),
        ["SymbolPackagePublish"] = new HashSet<string>(["4.9.0"], StringComparer.Ordinal),
        ["VulnerabilityInfo"] = new HashSet<string>(["6.7.0"], StringComparer.Ordinal)
    };

    private static IReadOnlyDictionary<string, ServiceResourceAccess> RequiredResourceAccess
    { get; } = new Dictionary<string, ServiceResourceAccess>(StringComparer.Ordinal)
    {
        [OperationIds.FlatContainerGetVersions] = ServiceResourceAccess.Read,
        [OperationIds.RegistrationGetIndex] = ServiceResourceAccess.Read,
        [OperationIds.SearchQuery] = ServiceResourceAccess.Read,
        [OperationIds.PackageManagementPush] = ServiceResourceAccess.PackagePublish,
        [OperationIds.PackageManagementPushSymbols] = ServiceResourceAccess.Write,
        [OperationIds.VulnerabilitiesGetIndex] = ServiceResourceAccess.Read
    };

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
