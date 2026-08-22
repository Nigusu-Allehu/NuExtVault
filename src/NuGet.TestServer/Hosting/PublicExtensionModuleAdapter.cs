using System.Collections.Immutable;
using System.Reflection;
using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Hosting;

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
                var route = manifest.Routes.SingleOrDefault()
                    ?? throw new ServerHostingConfigurationException(
                        "external-extension.resource-route-ambiguous: A service resource requires exactly one route.");
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
        var handler = binder.CreateHandler(route.Operation.Value, binding.ResponseType);
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
            Body = EndpointBodyBinding.None,
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
        public abstract IEndpointHandler CreateHandler(string operationId, Type responseType);
    }

    private sealed class RecordedRouteBinder<TRequest>(
        Func<RouteBindingRequest, CancellationToken, ValueTask<TRequest>> binder)
        : RecordedRouteBinder
    {
        public override IEndpointHandler CreateHandler(string operationId, Type responseType)
        {
            var method = typeof(RecordedRouteBinder<TRequest>)
                .GetMethod(nameof(CreateTypedHandler), BindingFlags.Instance | BindingFlags.NonPublic)!
                .MakeGenericMethod(responseType);
            return (IEndpointHandler)method.Invoke(this, [operationId])!;
        }

        private IEndpointHandler CreateTypedHandler<TResponse>(string operationId) =>
            EndpointHandler.Create(async (request, token) =>
            {
                var body = new BoundedDocument(
                    [],
                    Math.Max(0, request.Limits.MaxRequestBytes),
                    "application/octet-stream");
                var typed = await binder(new RouteBindingRequest(
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    body), token);
                return EndpointInvocation.Operation<TRequest, TResponse>(operationId, typed);
            });
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
