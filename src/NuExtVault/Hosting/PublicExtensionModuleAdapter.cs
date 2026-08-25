using System.Collections.Immutable;
using System.Reflection;
using NuExtVault.Extensions.Sdk;

namespace NuExtVault.Hosting;

internal static class PublicExtensionModuleAdapter
{
    public static IExtensionModule Materialize(
        IExtensionModule module,
        string manifestDigest,
        string stagedContentDigest)
    {
        ArgumentNullException.ThrowIfNull(module);
        var manifest = module.Contribution.Manifest;
        if (!manifest.Endpoints.IsEmpty || !module.Contribution.Contracts.IsEmpty)
        {
            return module;
        }

        var operations = new RecordingOperationRegistry();
        module.RegisterOperations(
            operations,
            RecordingCapabilities.Instance,
            EmptyContributions.Instance);
        var routes = new RecordingRouteRegistry();
        module.RegisterRoutes(routes);

        var materializedBindings = operations.Bindings.Select(binding =>
        {
            var declaration = manifest.Operations.Single(operation =>
                operation.Identity.Value == binding.Contract.Id.Value);
            return binding with
            {
                Contract = binding.Contract with
                {
                    RequestContract = declaration.RequestContract,
                    ResponseContract = declaration.ResponseContract
                }
            };
        }).ToImmutableArray();
        var bindings = materializedBindings.ToDictionary(
            binding => binding.Contract.Id.Value,
            StringComparer.Ordinal);
        var endpoints = manifest.Routes.Select(route =>
        {
            if (!bindings.TryGetValue(route.Operation.Value, out var binding))
            {
                throw new ServerHostingConfigurationException(
                    $"external-extension.route-operation-missing: Route '{route.Identity.Value}' " +
                    $"references operation '{route.Operation.Value}' without a typed registration.");
            }
            if (!routes.TryGet(route.Identity.Value, out var binder))
            {
                throw new ServerHostingConfigurationException(
                    $"external-extension.route-binder-missing: Route '{route.Identity.Value}' has no binder.");
            }

            return CreateDescriptor(route, binding, binder);
        }).ToImmutableArray();

        var resources = manifest.Contributions
            .Where(contribution => contribution.Kind == "service-resource")
            .Select(contribution =>
            {
                var route = contribution.Route is { } reference
                    ? manifest.Routes.SingleOrDefault(candidate =>
                        candidate.Identity.Value == reference.Value)
                      ?? throw new ServerHostingConfigurationException(
                          "external-extension.resource-route-missing: Service resource " +
                          $"'{contribution.Identity.Value}' references unknown route " +
                          $"'{reference.Value}'.")
                    : manifest.Routes.Length == 1
                        ? manifest.Routes[0]
                        : throw new ServerHostingConfigurationException(
                            "external-extension.resource-route-ambiguous: Service resource " +
                            $"'{contribution.Identity.Value}' must reference a route by id " +
                            "unless the extension declares exactly one route.");
                return new ServiceResourceContribution(
                    contribution.Identity.Value,
                    contribution.Version.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    new OperationId(route.Operation.Value),
                    route.Path,
                    ServiceResourceVisibility.Advertised,
                    ServiceResourceAccess.Read,
                    [],
                    [],
                    null,
                    1000,
                    ServiceResourceReadiness.Ready);
            })
            .ToImmutableArray();

        var enriched = new ExtensionManifest(
            manifest.SchemaVersion.Value,
            manifest.Identity.Id,
            new ExtensionVersion(
                int.Parse(manifest.Identity.Version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(manifest.Identity.Version.Split('.')[1], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(manifest.Identity.Version.Split('.')[2], System.Globalization.CultureInfo.InvariantCulture)),
            ExtensionVersionRange.Major(manifest.Sdk.Minimum.Major),
            [],
            [.. manifest.Operations.Select(operation => operation.Identity.Value)],
            endpoints,
            resources,
            manifest.Capabilities)
        {
            OwnedOperations = [.. manifest.Operations.Select(operation => operation.Identity.Value)],
            Resources = resources,
            State = manifest.State,
            ValidatedManifestDigest = manifestDigest,
            ValidatedStagedContentDigest = stagedContentDigest
        };
        var contribution = new ExtensionModuleContribution(enriched, materializedBindings);
        return new MaterializedModule(module, contribution);
    }

    private static EndpointDescriptor CreateDescriptor(
        RouteDeclaration route,
        OperationBinding binding,
        RecordedRouteBinder binder)
    {
        var routeParameterNames = EndpointPathTemplate.ReadParameterNames(route.Path);
        var handler = binder.CreateHandler(route.Operation.Value, binding.ResponseType, route);
        return new EndpointDescriptor
        {
            Name = route.Identity.Value,
            Methods = route.Methods,
            PathTemplate = route.Path,
            Head = route.Head == "mirrors-get"
                ? EndpointHeadPolicy.MirrorsGet
                : EndpointHeadPolicy.None,
            Access = EndpointAccessPolicy.Of(route.Access switch
            {
                "anonymous" => EndpointAccessKind.Anonymous,
                "read" => EndpointAccessKind.Read,
                "write" => EndpointAccessKind.Write,
                "publish" => EndpointAccessKind.Publish,
                "unlist" => EndpointAccessKind.Unlist,
                "delete" => EndpointAccessKind.Delete,
                "admin" => EndpointAccessKind.Admin,
                "control" => EndpointAccessKind.Control,
                _ => throw new ServerHostingConfigurationException(
                    $"external-extension.route-access-invalid: Route '{route.Identity.Value}' has invalid access.")
            }),
            Body = route.Body switch
            {
                RouteBodyBinding.Stream => EndpointBodyBinding.Stream,
                RouteBodyBinding.Bounded => EndpointBodyBinding.Bounded(),
                _ => EndpointBodyBinding.None
            },
            RouteParameters = [.. routeParameterNames.Select(name => new EndpointParameter(name))],
            Limits = route.MaximumRequestBytes == 0
                ? EndpointLimits.BodyFree with
                {
                    Timeout = TimeSpan.FromMilliseconds(route.TimeoutMilliseconds)
                }
                : new EndpointLimits(
                    route.MaximumRequestBytes,
                    route.MaximumResponseBytes,
                    64,
                    TimeSpan.FromMilliseconds(route.TimeoutMilliseconds)),
            Operations =
            [
                new EndpointOperationBinding(
                    route.Operation.Value,
                    binding.Contract.RequestContract,
                    binding.Contract.ResponseContract)
            ],
            Handler = handler
        };
    }

    private sealed class MaterializedModule(
        IExtensionModule inner,
        ExtensionModuleContribution contribution) : IExtensionModule
    {
        public ExtensionModuleContribution Contribution { get; } = contribution;

        public void RegisterOperations(
            IOperationOwnerRegistry operations,
            IExtensionCapabilities capabilities,
            IDocumentContributionSource contributions) =>
            inner.RegisterOperations(operations, capabilities, contributions);

        public void RegisterRoutes(IRouteBinderRegistry routes) => inner.RegisterRoutes(routes);
    }

    private sealed class RecordingOperationRegistry : IOperationOwnerRegistry
    {
        private readonly List<OperationBinding> _bindings = [];
        public ImmutableArray<OperationBinding> Bindings => [.. _bindings];

        public OperationDeclaration RegisterNew<TRequest, TResponse>(
            string extensionId,
            OperationIdentity identity,
            Func<TRequest, CancellationToken, ValueTask<OperationResponse<TResponse>>> handler)
        {
            _bindings.Add(new OperationBinding(
                new OperationContract(
                    new OperationId(identity.Value),
                    OperationFamily.Custom(extensionId),
                    ExtensionSdkVersions.OperationV1.Value,
                    $"{typeof(TRequest).FullName}.v1",
                    $"{typeof(TResponse).FullName}.v1"),
                typeof(TRequest),
                typeof(TResponse)));
            return new OperationDeclaration(
                identity,
                ExtensionSdkVersions.OperationV1,
                $"{typeof(TRequest).FullName}.v1",
                $"{typeof(TResponse).FullName}.v1",
                OperationOwnership.New,
                AllowReplacement: false);
        }

        IOperationOwnerRegistry IOperationOwnerRegistry.Register<TRequest, TResponse>(
            string extensionId,
            IOperationOwner<TRequest, TResponse> owner) =>
            throw new ServerHostingConfigurationException(
                "external-extension.internal-registration-forbidden: External modules must use RegisterNew.");
    }

    private sealed class RecordingRouteRegistry : IRouteBinderRegistry
    {
        private readonly Dictionary<string, RecordedRouteBinder> _binders =
            new(StringComparer.Ordinal);

        public void Bind<TRequest>(
            RouteIdentity route,
            Func<RouteBindingRequest, CancellationToken, ValueTask<TRequest>> binder)
        {
            if (!_binders.TryAdd(route.Value, new RecordedRouteBinder<TRequest>(binder)))
            {
                throw new ServerHostingConfigurationException(
                    $"external-extension.duplicate-route-binder: Route '{route.Value}' has multiple binders.");
            }
        }

        public bool TryGet(string route, out RecordedRouteBinder binder) =>
            _binders.TryGetValue(route, out binder!);
    }

    private abstract class RecordedRouteBinder
    {
        public abstract IEndpointHandler CreateHandler(
            string operationId, Type responseType, RouteDeclaration route);
    }

    private sealed class RecordedRouteBinder<TRequest>(
        Func<RouteBindingRequest, CancellationToken, ValueTask<TRequest>> binder)
        : RecordedRouteBinder
    {
        public override IEndpointHandler CreateHandler(
            string operationId, Type responseType, RouteDeclaration route)
        {
            var method = typeof(RecordedRouteBinder<TRequest>)
                .GetMethod(nameof(CreateTypedHandler), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(responseType);
            return (IEndpointHandler)method.Invoke(this, [operationId, route])!;
        }

        private IEndpointHandler CreateTypedHandler<TResponse>(
            string operationId, RouteDeclaration route)
        {
            var routeParameterNames = EndpointPathTemplate.ReadParameterNames(route.Path);
            var declaredHeaders = route.Headers
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            return EndpointHandler.Create(async (request, token) =>
            {
                var body = route.Body == RouteBodyBinding.Bounded
                    ? await request.ReadBoundedBodyAsync(
                        Math.Max(1, request.Limits.MaxRequestBytes),
                        token)
                    : null;
                var source = new KernelRouteBindingSource(
                    request,
                    routeParameterNames,
                    declaredHeaders,
                    route.Body,
                    body);
                var typed = await binder(new RouteBindingRequest(source), token);
                return EndpointInvocation.Operation<TRequest, TResponse>(operationId, typed);
            });
        }
    }

    /// <summary>
    /// The kernel side of a public route binding. It exposes only the route values the
    /// path template declares, the query the request actually carries, the headers the
    /// manifest declares, and either the bounded body the kernel already read or a
    /// kernel-issued, non-buffering stream handle.
    /// </summary>
    private sealed class KernelRouteBindingSource(
        EndpointRequest request,
        ImmutableArray<string> routeParameterNames,
        ImmutableHashSet<string> declaredHeaders,
        RouteBodyBinding body,
        BoundedDocument? boundedBody) : IRouteBindingSource
    {
        public bool TryGetRoute(string name, out string? value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(name) ||
                !routeParameterNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            value = request.GetRoute(name);
            return true;
        }

        public bool TryGetQuery(string name, out string? value)
        {
            value = string.IsNullOrWhiteSpace(name) ? null : request.GetQuery(name);
            return value is not null;
        }

        public string? FindHeader(string name) =>
            declaredHeaders.Contains(name) ? request.GetHeader(name) : null;

        public BoundedDocument ReadBody() =>
            boundedBody ?? throw new InvalidOperationException(
                body == RouteBodyBinding.Stream
                    ? "This route declares a streaming request body; use BindBodyStream."
                    : "This route does not declare a request body.");

        public StreamHandle BindBodyStream() =>
            body == RouteBodyBinding.Stream
                ? request.BindBodyStream()
                : throw new InvalidOperationException(
                    "This route does not declare a streaming request body.");
    }

    private sealed class RecordingCapabilities : IExtensionCapabilities
    {
        public static RecordingCapabilities Instance { get; } = new();

        TCapability IExtensionCapabilities.GetRequired<TCapability>(string capabilityName) =>
            CapabilityProxy<TCapability>.Instance;

        bool IExtensionCapabilities.TryGet<TCapability>(
            string capabilityName,
            out TCapability? capability)
            where TCapability : class
        {
            capability = CapabilityProxy<TCapability>.Instance;
            return true;
        }
    }

    private static class CapabilityProxy<TCapability> where TCapability : class
    {
        public static TCapability Instance { get; } =
            DispatchProxy.Create<TCapability, ThrowingCapabilityProxy>();
    }

    private class ThrowingCapabilityProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                "Capabilities cannot be invoked while materializing extension metadata.");
    }

    private sealed class EmptyContributions : IDocumentContributionSource
    {
        public static EmptyContributions Instance { get; } = new();
    }
}
