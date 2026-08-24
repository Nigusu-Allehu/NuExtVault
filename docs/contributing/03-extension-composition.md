# 3. Extension composition

NuTestServer resolves a complete extension graph before accepting requests. A
module contributes immutable declarations and typed registrations; the kernel
validates ownership, dependencies, routes, resources, grants, and profiles as
one deterministic unit.

## Module contract

A separately compiled extension implements `IExtensionModule`. Its
`Contribution` returns an immutable manifest, `RegisterOperations` registers
typed owners, and `RegisterRoutes` registers typed binders. The public contract
is in [`ExtensionModel.cs`](../../src/NuGet.TestServer.Extensions.Sdk/ExtensionModel.cs)
and is frozen by the [SDK API
snapshot](../../tests/NuGet.TestServer.Extensions.Sdk.Tests/Snapshots/Sdk.PublicApi.approved.txt).

The strict manifest is authoritative for identity, SDK range, independent
contract versions, operations, service-resource contributions, routes,
capability requests, and optional state. Code registrations must exactly match
declared operations, and every declared route requires a matching binder.
Undeclared extra binders are currently ignored rather than rejected.

## One active owner

An operation has a stable identity, typed request/response contracts, and
exactly one selected owner. Graph validation rejects duplicate manifest claims.
Registry construction then rejects missing, duplicate, inactive,
differently-owned, or type-mismatched handlers. Registration order cannot select
a winner.

Public v1 extensions may introduce only new extension-prefixed operation IDs.
Replacement is disabled, and the SDK exposes no takeover API. Authoritative
identity, publication, moderation, ownership, recovery, and package mutations
are nonreplaceable. See [`OperationRegistryTests`](../../tests/NuGet.TestServer.UnitTests/OperationRegistryTests.cs)
and [`OwnershipAndCapabilityContractTests`](../../tests/NuGet.TestServer.Extensions.Sdk.Tests/OwnershipAndCapabilityContractTests.cs).

## Routes, resources, and contributions

A route makes an operation callable. A service resource makes it discoverable
through the NuGet service index. Routes declare transport behavior; binders see
only declared route values and headers, declared body mode, and any supplied
query key (the current public route contract has no query-key declaration).
Contributions may identify the route that backs a resource, and the kernel
projects its absolute URL.

The catalog rejects concrete and semantic route conflicts, reserved prefixes,
dangling operations, incompatible contracts, invalid limits, access mismatches,
duplicate single-owner resources, and missing resource links. Resource ordering
is explicit and independent of registration order. Evidence lives in
[`ExtensionCatalogTests`](../../tests/NuGet.TestServer.UnitTests/ExtensionCatalogTests.cs),
[`EndpointDescriptorTests`](../../tests/NuGet.TestServer.UnitTests/EndpointDescriptorTests.cs),
and [`ServiceIndexCompositionTests`](../../tests/NuGet.TestServer.UnitTests/ServiceIndexCompositionTests.cs).

Typed registration-document contributors and policy participants exist
internally. V1 does not expose arbitrary JSON mutation, arbitrary contribution
kinds, or public policy-participant registration.

## Profiles and diagnostics

Profiles are internal host composition, not public SDK objects:

| Profile | Implemented role |
| --- | --- |
| `embedded` | In-memory, host-isolated programmatic default with test control; no external network or sidecars |
| `standard` | Default CLI composition with durable storage, official features, and test control |
| `production` | Durable, production-security composition without test-control features or grants |

[`ServerProfiles`](../../src/NuGet.TestServer/Hosting/ServerProfiles.cs) defines
the selections and grants. [`ServerProfileTests`](../../tests/NuGet.TestServer.UnitTests/ServerProfileTests.cs)
and [composition functional
tests](../../tests/NuGet.TestServer.FunctionalTests/ServerProfileCompositionTests.cs)
freeze their differences and per-host isolation.

Resolved graph diagnostics and operation inventories are ordinal and stable.
Trusted-package diagnostics redact configured filesystem roots. These are
internal diagnostics; no public CLI graph-dump command is currently promised.

## Official and trusted external modules

Official modules ship in `NuGet.TestServer.Extensions.Official` and obey the SDK
boundary. Administrator-installed packages are discovered only from explicit
local roots, validated, staged, loaded, then merged into the same catalog,
operation, route, resource, and capability graph. They are trusted in-process
code, not sandboxed code.

The reviewed built-in structural contract snapshots are:

<!-- example-id: contrib-03-evidence-inventories; evidence: reference -->
```text
tests/NuGet.TestServer.UnitTests/Snapshots/operations.contract.txt
tests/NuGet.TestServer.UnitTests/Snapshots/routes.contract.txt
tests/NuGet.TestServer.UnitTests/Snapshots/resources.contract.txt
tests/NuGet.TestServer.UnitTests/Snapshots/capabilities.contract.txt
```

[`ContractFingerprintTests`](../../tests/NuGet.TestServer.UnitTests/ContractFingerprintTests.cs)
regenerate and compare these internal snapshots. They cover global built-in
contracts/manifests and capability interface shapes, not resolved profiles or
loaded external modules. Update them only as part of an approved, reviewed
contract or composition change.

---

[Contributor manual](README.md) | **Previous:** [Request lifecycle](02-request-lifecycle.md) | **Next:** [Capabilities and security](04-capabilities-and-security.md)
