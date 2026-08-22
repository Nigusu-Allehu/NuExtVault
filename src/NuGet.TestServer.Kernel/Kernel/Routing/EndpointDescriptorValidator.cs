using System.Collections.Immutable;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Hosting;

namespace NuGet.TestServer.Kernel.Routing;

/// <summary>
/// Deterministic validation for endpoint descriptors. Every failure stops host startup
/// before any listener is created, so an invalid route table can never serve traffic.
/// </summary>
internal static class EndpointDescriptorValidator
{
    private static readonly ImmutableArray<string> KnownMethods =
        ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];

    private static readonly ImmutableArray<string> BodyRequiredMethods = ["PUT", "PATCH"];

    private static readonly ImmutableArray<string> BodyFreeMethods = ["GET", "HEAD", "DELETE"];

    /// <summary>Path prefixes the kernel reserves for itself.</summary>
    public static ImmutableArray<string> ReservedPathPrefixes { get; } = ["/__kernel"];

    /// <summary>Route-name prefixes the kernel reserves for itself.</summary>
    public static ImmutableArray<string> ReservedNamePrefixes { get; } = ["kernel."];

    /// <summary>
    /// Validates every declared endpoint and returns the applicable, deterministically
    /// ordered endpoints for the current host.
    /// </summary>
    public static ImmutableArray<ResolvedEndpoint> Validate(
        IReadOnlyList<ExtensionManifest> manifests,
        IReadOnlyDictionary<string, string> operationOwners,
        IReadOnlyDictionary<string, OperationBinding> contracts,
        bool hasProductionIdentity)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(operationOwners);
        ArgumentNullException.ThrowIfNull(contracts);

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var applicable = new List<ResolvedEndpoint>();
        var canonical = new Dictionary<string, ResolvedEndpoint>(StringComparer.OrdinalIgnoreCase);
        var concrete = new Dictionary<string, ResolvedEndpoint>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in manifests)
        {
            foreach (var descriptor in manifest.Endpoints
                         .OrderBy(endpoint => endpoint.Name, StringComparer.Ordinal))
            {
                ValidateStructure(manifest.Id, descriptor);
                ValidateOperations(manifest.Id, descriptor, operationOwners, contracts);
                if (names.TryGetValue(descriptor.Name, out var existingOwner))
                {
                    throw Failure(
                        "duplicate-endpoint-name",
                        $"Route name '{descriptor.Name}' is declared by '{existingOwner}' and " +
                        $"'{manifest.Id}'.");
                }

                names.Add(descriptor.Name, manifest.Id);
                if (!descriptor.AppliesTo(hasProductionIdentity))
                {
                    continue;
                }

                applicable.Add(new ResolvedEndpoint(descriptor, manifest.Id));
            }
        }

        foreach (var endpoint in applicable
                     .OrderBy(endpoint => endpoint.ExtensionId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(endpoint => endpoint.Descriptor.Name, StringComparer.Ordinal))
        {
            foreach (var method in NormalizeMethods(endpoint.Descriptor))
            {
                var concreteKey = $"{method} {endpoint.Descriptor.PathTemplate}";
                if (concrete.TryGetValue(concreteKey, out var existing))
                {
                    throw ConflictFailure(existing, endpoint, method);
                }

                concrete.Add(concreteKey, endpoint);
                EndpointPathTemplate.TryCanonicalize(
                    endpoint.Descriptor.PathTemplate,
                    out var canonicalPath,
                    out _);
                var canonicalKey = $"{method} {canonicalPath}";
                if (canonical.TryGetValue(canonicalKey, out var overlapping))
                {
                    throw CollisionFailure(overlapping, endpoint, method);
                }

                canonical.Add(canonicalKey, endpoint);
            }
        }

        return
        [
            .. applicable.OrderBy(endpoint => endpoint.Descriptor.Name, StringComparer.Ordinal)
        ];
    }

    public static ImmutableArray<string> NormalizeMethods(EndpointDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return
        [
            .. descriptor.Methods
                .Select(method => method.ToUpperInvariant())
                .Order(StringComparer.Ordinal)
        ];
    }

    private static void ValidateStructure(string extensionId, EndpointDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Name) ||
            descriptor.Name.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-')))
        {
            throw Invalid(extensionId, descriptor, "the route name is missing or malformed");
        }

        if (ReservedNamePrefixes.Any(prefix =>
                descriptor.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw Failure(
                "reserved-endpoint",
                $"Extension '{extensionId}' declares route name '{descriptor.Name}', which uses a " +
                "kernel-reserved prefix.");
        }

        if (ReservedPathPrefixes.Any(prefix =>
                descriptor.PathTemplate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            throw Failure(
                "reserved-endpoint",
                $"Extension '{extensionId}' declares route '{descriptor.PathTemplate}', which uses " +
                "a kernel-reserved path prefix.");
        }

        if (descriptor.Methods.IsDefaultOrEmpty)
        {
            throw Invalid(extensionId, descriptor, "no HTTP method is declared");
        }

        var methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in descriptor.Methods)
        {
            var normalized = method?.ToUpperInvariant() ?? string.Empty;
            if (!KnownMethods.Contains(normalized))
            {
                throw Invalid(extensionId, descriptor, $"method '{method}' is not supported");
            }

            if (!methods.Add(normalized))
            {
                throw Invalid(extensionId, descriptor, $"method '{normalized}' is declared twice");
            }
        }

        if (!EndpointPathTemplate.TryCanonicalize(descriptor.PathTemplate, out _, out var error))
        {
            throw Invalid(extensionId, descriptor, error!);
        }

        var templateParameters = EndpointPathTemplate
            .ReadParameterNames(descriptor.PathTemplate)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declaredParameters = descriptor.RouteParameters
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!templateParameters.SetEquals(declaredParameters))
        {
            throw Invalid(
                extensionId,
                descriptor,
                "the declared route parameters do not match the path template");
        }

        var declaresGet = methods.Contains("GET");
        var declaresHead = methods.Contains("HEAD");
        if ((descriptor.Head == EndpointHeadPolicy.MirrorsGet) != (declaresGet && declaresHead))
        {
            throw Failure(
                "endpoint-head-policy",
                $"Extension '{extensionId}' declares route '{descriptor.Name}' with HEAD policy " +
                $"'{descriptor.Head}', which does not match its declared methods.");
        }

        if (declaresHead && !declaresGet)
        {
            throw Failure(
                "endpoint-head-policy",
                $"Extension '{extensionId}' declares route '{descriptor.Name}' with HEAD but " +
                "without GET.");
        }

        if (descriptor.Access is null ||
            descriptor.Access.Default == EndpointAccessKind.Unspecified ||
            descriptor.Access.ProductionIdentity == EndpointAccessKind.Unspecified)
        {
            throw Failure(
                "endpoint-access-policy",
                $"Extension '{extensionId}' declares route '{descriptor.Name}' without an access " +
                "policy.");
        }

        ValidateBinding(extensionId, descriptor, methods);
        ValidateLimits(extensionId, descriptor);
    }

    private static void ValidateLimits(string extensionId, EndpointDescriptor descriptor)
    {
        var limits = descriptor.Limits;
        if (limits is null || limits.MaxConcurrentCalls <= 0 || limits.Timeout <= TimeSpan.Zero)
        {
            throw Failure(
                "endpoint-limits",
                $"Extension '{extensionId}' declares route '{descriptor.Name}' without positive " +
                "concurrency and timeout limits.");
        }

        var bodyFree = descriptor.Body.Kind == EndpointBodyKind.None;
        if (bodyFree && (limits.MaxRequestBytes != 0 || limits.MaxContentBytes != 0))
        {
            throw Failure(
                "endpoint-limits",
                $"Extension '{extensionId}' declares body-free route '{descriptor.Name}' with " +
                "request payload limits.");
        }

        if (!bodyFree &&
            (limits.MaxRequestBytes == 0 ||
             limits.MaxRequestBytes < EndpointLimits.Inherit ||
             limits.MaxContentBytes == 0 ||
             limits.MaxContentBytes < EndpointLimits.Inherit))
        {
            throw Failure(
                "endpoint-limits",
                $"Extension '{extensionId}' declares route '{descriptor.Name}' with a payload but " +
                "without request payload limits.");
        }
    }

    private static void ValidateBinding(
        string extensionId,
        EndpointDescriptor descriptor,
        IReadOnlySet<string> methods)
    {
        if (descriptor.Body is null)
        {
            throw Failure(
                "endpoint-binding",
                $"Extension '{extensionId}' declares route '{descriptor.Name}' without a body " +
                "binding.");
        }

        if (methods.Any(BodyRequiredMethods.Contains) &&
            descriptor.Body.Kind == EndpointBodyKind.None)
        {
            throw Failure(
                "endpoint-binding",
                $"Extension '{extensionId}' declares route '{descriptor.Name}' without a declared " +
                "body or stream binding, which its methods require.");
        }

        if (methods.Any(BodyFreeMethods.Contains) &&
            descriptor.Body.Kind != EndpointBodyKind.None)
        {
            throw Failure(
                "endpoint-binding",
                $"Extension '{extensionId}' declares body-free route '{descriptor.Name}' with a " +
                $"'{descriptor.Body.Kind}' binding.");
        }
    }

    private static void ValidateOperations(
        string extensionId,
        EndpointDescriptor descriptor,
        IReadOnlyDictionary<string, string> operationOwners,
        IReadOnlyDictionary<string, OperationBinding> contracts)
    {
        if (descriptor.Operations.IsDefaultOrEmpty)
        {
            throw Invalid(extensionId, descriptor, "no operation is declared");
        }

        foreach (var operation in descriptor.Operations)
        {
            if (!contracts.TryGetValue(operation.OperationId, out var contract))
            {
                throw Failure(
                    "unknown-endpoint-operation",
                    $"Route '{descriptor.Name}' from '{extensionId}' dispatches unknown operation " +
                    $"'{operation.OperationId}'.");
            }

            if (!StringComparer.Ordinal.Equals(
                    contract.Contract.RequestContract,
                    operation.RequestContract) ||
                !StringComparer.Ordinal.Equals(
                    contract.Contract.ResponseContract,
                    operation.ResponseContract))
            {
                throw Failure(
                    "endpoint-contract-mismatch",
                    $"Route '{descriptor.Name}' from '{extensionId}' declares contracts " +
                    $"'{operation.RequestContract}'/'{operation.ResponseContract}' for operation " +
                    $"'{operation.OperationId}', which declares " +
                    $"'{contract.Contract.RequestContract}'/'{contract.Contract.ResponseContract}'.");
            }

            if (!operationOwners.TryGetValue(operation.OperationId, out var owner))
            {
                throw Failure(
                    "inactive-endpoint-operation",
                    $"Route '{descriptor.Name}' from '{extensionId}' dispatches operation " +
                    $"'{operation.OperationId}', which the resolved extension graph does not " +
                    "activate.");
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(owner, extensionId))
            {
                throw Failure(
                    "endpoint-owner-mismatch",
                    $"Route '{descriptor.Name}' from '{extensionId}' dispatches operation " +
                    $"'{operation.OperationId}', which is owned by '{owner}'.");
            }
        }
    }

    private static ServerHostingConfigurationException ConflictFailure(
        ResolvedEndpoint first,
        ResolvedEndpoint second,
        string method)
    {
        var ordered = Order(first, second);
        return Failure(
            "route-conflict",
            $"Route '{method} {ordered.First.Descriptor.PathTemplate}' is owned by " +
            $"'{ordered.First.ExtensionId}' and '{ordered.Second.ExtensionId}'.");
    }

    private static ServerHostingConfigurationException CollisionFailure(
        ResolvedEndpoint first,
        ResolvedEndpoint second,
        string method)
    {
        var ordered = Order(first, second);
        return Failure(
            "endpoint-collision",
            $"Route '{method} {ordered.First.Descriptor.PathTemplate}' owned by " +
            $"'{ordered.First.ExtensionId}' semantically collides with " +
            $"'{method} {ordered.Second.Descriptor.PathTemplate}' owned by " +
            $"'{ordered.Second.ExtensionId}'.");
    }

    private static (ResolvedEndpoint First, ResolvedEndpoint Second) Order(
        ResolvedEndpoint left,
        ResolvedEndpoint right)
    {
        var comparison = StringComparer.Ordinal.Compare(left.ExtensionId, right.ExtensionId);
        if (comparison == 0)
        {
            comparison = StringComparer.Ordinal.Compare(
                left.Descriptor.Name,
                right.Descriptor.Name);
        }

        return comparison <= 0 ? (left, right) : (right, left);
    }

    private static ServerHostingConfigurationException Invalid(
        string extensionId,
        EndpointDescriptor descriptor,
        string reason) =>
        Failure(
            "invalid-endpoint",
            $"Extension '{extensionId}' declares invalid route " +
            $"'{descriptor.PathTemplate}': {reason}.");

    private static ServerHostingConfigurationException Failure(string code, string message) =>
        new($"catalog.{code}: {message}");
}
