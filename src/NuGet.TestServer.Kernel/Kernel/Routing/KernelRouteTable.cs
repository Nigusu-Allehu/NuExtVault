using System.Collections.Immutable;
using System.Text;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.Kernel.Routing;

/// <summary>
/// One generated route: the validated descriptor, its owner, the resolved access
/// requirement, and the host-resolved limits.
/// </summary>
internal sealed record KernelRouteEndpoint(
    EndpointDescriptor Descriptor,
    string ExtensionId,
    NuGetAccessRequirement Access,
    EndpointLimits Limits,
    ImmutableArray<string> Methods);

/// <summary>
/// The frozen, per-host-instance route table. It is generated from validated
/// descriptors before the host listens and is never mutated afterwards; runtime route
/// mutation is out of scope.
/// </summary>
internal sealed class KernelRouteTable
{
    private KernelRouteTable(ImmutableArray<KernelRouteEndpoint> endpoints, string diagnostics)
    {
        Endpoints = endpoints;
        Diagnostics = diagnostics;
    }

    public ImmutableArray<KernelRouteEndpoint> Endpoints { get; }

    /// <summary>
    /// The table is immutable once created. Startup fails before this point when any
    /// descriptor is invalid.
    /// </summary>
    public bool IsFrozen => true;

    public string Diagnostics { get; }

    public static KernelRouteTable Create(
        ResolvedExtensionGraph graph,
        PackageTransferLimits limits,
        bool hasProductionIdentity)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(limits);

        var endpoints = graph.Endpoints
            .Select(endpoint => new KernelRouteEndpoint(
                endpoint.Descriptor,
                endpoint.ExtensionId,
                Requirement(endpoint.Descriptor.Access.Resolve(hasProductionIdentity)),
                endpoint.Descriptor.Limits.Resolve(
                    limits.MaxRequestBodyBytes,
                    limits.MaxPackageBytes),
                EndpointDescriptorValidator.NormalizeMethods(endpoint.Descriptor)))
            .OrderBy(endpoint => endpoint.Descriptor.Name, StringComparer.Ordinal)
            .ToImmutableArray();

        VerifyRouteRegistry(graph, endpoints);
        return new KernelRouteTable(endpoints, CreateDiagnostics(graph, endpoints));
    }

    private static void VerifyRouteRegistry(
        ResolvedExtensionGraph graph,
        ImmutableArray<KernelRouteEndpoint> endpoints)
    {
        var generated = endpoints
            .SelectMany(endpoint => endpoint.Methods.Select(
                method => $"{method} {endpoint.Descriptor.PathTemplate}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var declared = graph.Routes
            .Select(route => $"{route.Method} {route.Path}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!generated.SequenceEqual(declared, StringComparer.Ordinal))
        {
            throw new ServerHostingConfigurationException(
                "routes.registry-mismatch: The generated route table does not match the resolved " +
                "extension graph.");
        }

        foreach (var resource in graph.Resources)
        {
            var owned = endpoints.Any(endpoint =>
                StringComparer.OrdinalIgnoreCase.Equals(
                    endpoint.ExtensionId,
                    resource.ExtensionId) &&
                Matches(resource.Contribution.RouteName, endpoint.Descriptor.PathTemplate));
            if (!owned)
            {
                throw new ServerHostingConfigurationException(
                    $"routes.resource-mismatch: Resource " +
                    $"'{resource.Contribution.AdvertisedType}' from '{resource.ExtensionId}' has " +
                    $"no generated route for '{resource.Contribution.RouteName}'.");
            }
        }
    }

    private static bool Matches(string routeName, string template) =>
        StringComparer.Ordinal.Equals(routeName, template) ||
        (routeName.EndsWith('/') && template.StartsWith(routeName, StringComparison.Ordinal));

    private static NuGetAccessRequirement Requirement(EndpointAccessKind kind) => kind switch
    {
        EndpointAccessKind.Anonymous => NuGetAccessRequirement.Anonymous,
        EndpointAccessKind.Read => NuGetAccessRequirement.Read,
        EndpointAccessKind.Write => NuGetAccessRequirement.Write,
        EndpointAccessKind.Publish => NuGetAccessRequirement.Publish,
        EndpointAccessKind.Unlist => NuGetAccessRequirement.Unlist,
        EndpointAccessKind.Delete => NuGetAccessRequirement.Delete,
        EndpointAccessKind.Admin => NuGetAccessRequirement.Admin,
        EndpointAccessKind.Control => NuGetAccessRequirement.Control,
        _ => throw new ServerHostingConfigurationException(
            "routes.missing-access-policy: A generated route has no access policy.")
    };

    private static string CreateDiagnostics(
        ResolvedExtensionGraph graph,
        ImmutableArray<KernelRouteEndpoint> endpoints)
    {
        var builder = new StringBuilder();
        builder.Append("profile=").Append(graph.ProfileName).Append('\n');
        foreach (var endpoint in endpoints)
        {
            builder.Append("route=").Append(endpoint.Descriptor.Name)
                .Append(" methods=").Append(string.Join(",", endpoint.Methods))
                .Append(" path=").Append(endpoint.Descriptor.PathTemplate)
                .Append(" owner=").Append(endpoint.ExtensionId)
                .Append(" access=").Append(endpoint.Access.Kind)
                .Append(" operations=").Append(string.Join(
                    ",",
                    endpoint.Descriptor.Operations.Select(operation => operation.OperationId)))
                .Append('\n');
        }

        return builder.ToString();
    }
}
