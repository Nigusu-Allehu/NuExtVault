# Extension SDK reference

Package: `NuGet.TestServer.Extensions.Sdk` (namespace
`NuGet.TestServer.Extensions.Sdk`). Test helpers live in
`NuGet.TestServer.Extensions.TestKit`.

An extension is a class library that:

1. declares an `extension-manifest.json` document that validates against the
   strict v1 schema,
2. implements `IExtensionModule`,
3. registers its operations and routes during composition, and
4. requests only capabilities declared in its manifest.

## Module lifecycle

```csharp
public interface IExtensionModule
{
    ExtensionModuleContribution Contribution { get; }

    void RegisterOperations(
        IOperationOwnerRegistry operations,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource contributions);

    void RegisterRoutes(IRouteBinderRegistry routes);
}
```

| Type | Purpose |
| --- | --- |
| `ExtensionModuleContribution` | Wraps the validated manifest. Create with `ExtensionModuleContribution.FromManifest(manifest)`. |
| `IOperationOwnerRegistry` | Registers operation handlers, for example `RegisterNew<TRequest, TResponse>(extensionId, operationId, handler)`. |
| `IRouteBinderRegistry` | Binds a declared route identity to a request-binding delegate. |
| `IExtensionCapabilities` | Resolves granted capabilities: `GetRequired<T>(CapabilityRequest)` and `TryGet<T>(CapabilityRequest, out T?)`. |
| `IDocumentContributionSource` | Access to bounded, typed document contributions declared in the manifest. |
| `OperationResponse<TResponse>` | Operation result envelope; `OperationResponse<T>.Success(value)`. |

`RegisterRoutes` is optional for extensions that only own operations.

## Manifest model

`ExtensionManifest` is the in-memory projection of `extension-manifest.json`.
`ExtensionManifestJson.Validate(ReadOnlyMemory<byte>)` returns a
`ManifestValidationResult` carrying either the manifest or an immutable array of
`ManifestValidationError` values (`Path`, `Code`, `Message`).

| Manifest member | Type | Meaning |
| --- | --- | --- |
| `SchemaVersion` | `ManifestSchemaVersion` | Manifest schema version (`1`). |
| `Identity` | `ExtensionIdentity(Id, Version, Publisher)` | Extension identity. |
| `Sdk` | `SdkCompatibilityRange(Minimum, MaximumExclusive)` | Supported SDK contract range. |
| `Contracts` | `ContractVersionSet` | Manifest, operation, contribution, route, capability, and structural contract versions. |
| `Operations` | `ImmutableArray<OperationDeclaration>` | Owned operations, their request/response contracts, ownership, and replacement policy. |
| `Contributions` | `ImmutableArray<ContributionDeclaration>` | Typed document contributions, for example service-index resources. |
| `Routes` | `ImmutableArray<RouteDeclaration>` | HTTP routes: methods, path, access kind, HEAD policy, byte and timeout limits. |
| `Capabilities` | `ImmutableArray<CapabilityRequest>` | Required and optional capability requests. |
| `State` | `ExtensionStateDeclaration?` | Persisted state schema name/version and whether it is required. |

A complete manifest example:

```json
{
  "$schema": "https://schemas.nutestserver.dev/extensions/manifest/v1",
  "schemaVersion": 1,
  "id": "Contoso.Flavors",
  "version": "1.2.3",
  "publisher": "Contoso",
  "sdk": { "minimum": "1.0.0", "maximumExclusive": "2.0.0" },
  "contracts": {
    "manifest": 1,
    "operation": 1,
    "contribution": 1,
    "route": 1,
    "capability": 1,
    "structural": 1
  },
  "operations": [
    {
      "id": "Contoso.Flavors.GetIndex",
      "version": 1,
      "requestContract": "Contoso.Flavors.GetIndexRequest.v1",
      "responseContract": "Contoso.Flavors.GetIndexResponse.v1",
      "ownership": "new",
      "allowReplacement": false
    }
  ],
  "contributions": [
    { "id": "Contoso.Flavors.ServiceIndex", "kind": "service-resource", "version": 1 }
  ],
  "routes": [
    {
      "id": "contoso.flavors.index",
      "operationId": "Contoso.Flavors.GetIndex",
      "methods": [ "GET", "HEAD" ],
      "path": "/flavors/index.json",
      "access": "read",
      "head": "mirrors-get",
      "maximumRequestBytes": 0,
      "maximumResponseBytes": 1048576,
      "timeoutMilliseconds": 30000
    }
  ],
  "capabilities": [
    { "name": "host.clock.read", "requirement": "required" },
    { "name": "network.outbound-http", "requirement": "optional" }
  ]
}
```

## A minimal extension

```csharp
using NuGet.TestServer.Extensions.Sdk;

public sealed class FlavorsExtension : IExtensionModule
{
    public ExtensionModuleContribution Contribution { get; } =
        ExtensionModuleContribution.FromManifest(
            ExtensionManifestJson.Parse(
                File.ReadAllBytes(Path.Combine(
                    AppContext.BaseDirectory,
                    "extension-manifest.json"))));

    public void RegisterRoutes(IRouteBinderRegistry routes)
    {
        routes.Bind<GetFlavorIndexRequest>(
            new RouteIdentity("contoso.flavors.index"),
            static (_, _) => ValueTask.FromResult(new GetFlavorIndexRequest()));
    }

    public void RegisterOperations(
        IOperationOwnerRegistry operations,
        IExtensionCapabilities capabilities,
        IDocumentContributionSource contributions)
    {
        var clock = capabilities.GetRequired<IHostClockCapability>(
            new CapabilityRequest(
                new CapabilityIdentity("host.clock.read"),
                CapabilityRequirement.Required));

        operations.RegisterNew<GetFlavorIndexRequest, GetFlavorIndexResponse>(
            Contribution.Manifest.Identity.Id,
            new OperationIdentity("Contoso.Flavors.GetIndex"),
            async (_, token) =>
            {
                var now = await clock.GetUtcNowAsync(token);
                return OperationResponse<GetFlavorIndexResponse>.Success(
                    new GetFlavorIndexResponse(["vanilla"], now));
            });
    }
}

public sealed record GetFlavorIndexRequest;

public sealed record GetFlavorIndexResponse(
    ImmutableArray<string> Flavors,
    DateTimeOffset GeneratedAt);
```

The compiled fixture in `tests/NuGet.TestServer.SdkFixture` is the executable form
of this example.

## Routing types

| Type | Purpose |
| --- | --- |
| `RouteIdentity` | Identifies a route declared in the manifest. |
| `RouteReference` | Produces a URL for a declared route: `RouteReference.Endpoint(name, parameters)` or `RouteReference.Base(name)`. |
| `RouteParameterValue` | Typed route parameter: `Text`, `PackageId`, or `PackageVersion`. |
| `RouteQueryValue` | Typed query-string value. |
| `EndpointAccessKind` | Route access requirement: `Anonymous`, `Read`, `Write`, `Publish`, `Unlist`, `Delete`, `Admin`, `Control`. |
| `RouteBodyBinding` | Declared body binding kind for a route. |
| `StreamHandle` | Bounded stream handle used for staged content and streamed responses. |

Extensions never receive `HttpContext`. The kernel binds requests, enforces route
limits, and produces absolute URLs from the validated request origin.

## Capabilities

Capabilities are denied by default, declared in the manifest, and granted by the
operator with [`--extension-grant`](../cli-reference.md#extension-options).
`CapabilityRequest(CapabilityIdentity, CapabilityRequirement)` describes a request;
`CapabilityDeniedException` is thrown when a required capability was not granted.

Well-known capability names include:

| Name | Contract | Purpose |
| --- | --- | --- |
| `host.clock.read` | `IHostClockCapability` | Read the kernel clock. |
| `packages.search.query` | Search capability | Indexed package search with authoritative visibility. |
| `packages.content.write-staged` | `IStagedContentWriteCapability` | Stream content into kernel-owned staged storage. |
| `publication.request` | `IAtomicPackagePublicationCapability` | Request atomic publication of staged content. |
| `extension-state.read` / `extension-state.write` | `ITransactionalStateCapability` | Read and write owner-scoped transactional state. |
| `supply-chain.signature.inspect` | Signature inspection | Inspect NuGet signatures. |
| `supply-chain.package.scan` | Package scanning | Invoke policy scanning. |
| `network.outbound-http` | `IOutboundHttpCapability` | Bounded outbound HTTP; denied for embedded hosts. |

## Staged content and publication

| Type | Purpose |
| --- | --- |
| `StagedContentHandle`, `StagedPackageIdentity` | Identify staged content and its package identity. |
| `StagedContentWriteResult` / `StagedContentWriteOutcome` | Result of a staged write. |
| `StagedContentReleaseResult` / `StagedContentReleaseOutcome` | Result of releasing a staged lease. |
| `AtomicPublicationRequest<TState>`, `AtomicPublicationResult`, `PublicationRequestOutcome` | Atomic publication of staged content together with an extension-state transition. |
| `TransactionalStateEntry<T>`, `TransactionalStateWriteResult`, `TransactionalStateWriteOutcome` | Owner-scoped transactional state values and write outcomes. |

The [Package Staging extension](package-staging.md) is the reference consumer of
these contracts.

## Results and errors

`OperationResult` carries a status, an optional body, and an optional location.
Statuses are `Ok`, `Created`, `Accepted`, `NoContent`, `InvalidRequest`,
`Unauthorized`, `Forbidden`, `NotFound`, `Conflict`, `UnsupportedMediaType`,
`PayloadTooLarge`, `Unprocessable`, `TooManyRequests`, `Unavailable`, and
`InternalError`. `OperationErrors` provides the matching helpers (`NotFound`,
`InvalidRequest`, `Unauthorized`, `Conflict`, `PolicyDenied`, `LimitExceeded`,
`Unavailable`, `Internal`).

## Conformance and testing

| Type | Purpose |
| --- | --- |
| `ExtensionConformance.ValidateAssembly(Assembly)` | Validates that a compiled extension satisfies the SDK contracts. |
| `ExtensionConformance.ValidateOwnership(extensionId, declaration)` | Validates operation ownership rules. |
| `ConformanceResult` | `IsValid` plus `ManifestValidationError` values. |
| `ConformanceAttestation`, `ConformanceAttestationVerifier`, `ConformanceTrustRoot` | Signed attestation payloads and verification used by trusted extension loading. |
| `ManifestBuilder` (TestKit) | Fluent manifest construction for tests. |
| `FakeHostClock` (TestKit) | Deterministic `IHostClockCapability` test double. |
| `ConformanceCheck.Validate(Assembly)` (TestKit) | Convenience wrapper over `ExtensionConformance`. |

See [Load trusted in-process extensions](README.md#load-trusted-in-process-extensions)
for packaging, attestation, and trust-root requirements.
