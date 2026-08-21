# Microkernel Migration Plan

## Status

Approved migration plan. Each implementation step still requires its own focused
proposal and explicit approval under `AGENTS.md`.

Implementation status: Steps 1 through 11 are implemented through merged PR #62 and
pass Windows, Ubuntu, and macOS CI. Step 11A generates every active route from
validated, startup-frozen endpoint descriptors and is merged through PR #65. Step 11B
projects absolute URLs in the kernel from typed route references and is merged through
PR #67. Step 11C moves the transport-neutral extension contracts into
`NuGet.TestServer.Extensions.Abstractions`, proves closed-world composition with a
separately compiled conformance module, and enforces the architecture fitness gates; it
is implemented but not yet merged. The old Step 12 is paused. Steps 11A through 11D
are blocking prerequisites added without renumbering the existing tracker issues.

The implementation is currently a closed built-in modular system for discovery and
loading, but composition itself is no longer closed: a module compiled against the
extension abstractions alone contributes a route, an operation, a resource, and a
requested capability with no kernel source change. Filesystem discovery, assembly load
contexts, and SDK publication remain out of scope.

Target architecture:
[`design/microkernel-extension-architecture.md`](../design/microkernel-extension-architecture.md).

## Objective

Migrate NuTestServer from a single host that directly composes all NuGet V3,
control, security, storage, and operations behavior into a microkernel where:

- The kernel owns safety, consistency, lifecycle, routing, and package invariants.
- Official features use the same typed extension contracts intended for third
  parties.
- The default CLI continues to behave exactly as it does today.
- Programmatic in-memory hosts remain fast, deterministic, and parallel-safe.
- Each migration step is independently reviewable, testable, reversible, and useful.

## Current implementation baseline

The current server already has strong feature coverage:

- Durable SQLite metadata and file-backed package content.
- Indexed search and registration queries.
- Package integrity and recovery.
- Authentication, authorization, TLS, throttling, and production mode.
- Supply-chain validation, quarantine, moderation, and ownership.
- Vulnerability information.
- Test controls, fault injection, and request recording.
- Health, diagnostics, backup, and restore.
- In-memory programmatic hosts.
- Real NuGet client, restore, push, restart, and CLI tests.

The primary migration hotspots are:

- `Hosting/ServerApplication.cs`
  - Registers the concrete services.
  - Owns middleware.
  - Generates the frozen route table from the resolved endpoint descriptors.
  - Builds service-index, registration, and search documents.
- `Hosting/NuGetTestServerHost.cs`
  - Exposes many direct construction overloads.
  - Resolves concrete control services directly from dependency injection.
- `NuGet.TestServer.Cli/Program.cs`
  - Parses configuration.
  - Constructs storage, vulnerability, security, and hosting services directly.
  - Owns backup, restore, startup, initial package loading, and refresh behavior.

The migration must separate these responsibilities without changing external
behavior.

## Rules for every migration pull request

Every step below is one pull request unless it proves too large during its proposal.

Each pull request must:

1. Begin with a focused proposal and explicit approval.
2. Add or update tests before production changes.
3. Preserve existing URLs, payloads, status codes, headers, and CLI behavior unless
   the proposal explicitly changes them.
4. Keep exactly one active owner for every operation.
5. Avoid publishing unstable extension APIs before the SDK publication step.
6. Run targeted tests, the full unit and functional suites, warning-as-error Release
   build, and CLI pack.
7. Keep programmatic hosts in-memory and network-independent by default.
8. Define a rollback point.
9. Update the README only after implemented behavior is final.
10. Leave unrelated features and refactors untouched.

## Completion gates

Do not advance to the next phase unless:

- Windows, Ubuntu, and macOS CI pass.
- Standard CLI behavior remains compatible.
- Programmatic hosts pass parallel-isolation tests.
- Real NuGet.Protocol, restore, and push scenarios remain green.
- No extension assembly references kernel implementation types.
- No operation has multiple active owners.
- Every route is generated from a typed descriptor and frozen before listening.
- No extension contract carries a host-derived absolute URL.
- No operation owner depends on kernel-specific rendering state.
- Capability signatures remain action-scoped and transport-neutral.

## Phase 1: Freeze behavior and define boundaries

### Step 1: Create a protocol compatibility baseline

**Goal:** Make accidental wire-level changes visible before introducing abstractions.

**Changes:**

- Add characterization fixtures for the service index, flat-container version
  documents, registration index/page/leaf documents, search results, vulnerability
  documents, push responses, and control errors.
- Normalize only inherently variable fields such as host, port, generated IDs, and
  timestamps.
- Record HEAD and GET behavior, status codes, relevant headers, ordering, casing,
  paging, and listed/unlisted behavior.
- Add immediate push-then-read, unlist-then-read, quarantine-then-read, and
  delete-then-read scenarios.

**Tests first:**

- Real Kestrel snapshot/semantic assertions.
- Real NuGet.Protocol queries.
- `dotnet restore` and `dotnet nuget push`.
- Parallel programmatic-host isolation.

**Done when:** Current behavior is represented by stable tests without production
changes.

**Rollback:** Remove only the new characterization tests.

### Step 2: Define the package-state and visibility contract

**Goal:** Make the kernel's most important invariant explicit before extracting
protocol behavior.

**Changes:**

- Introduce internal typed package-authority facts and public resource classes.
- Add one authoritative visibility service that derives immutable public-resource
  grants for exact content, version enumeration, registration, search, and symbols.
- Keep administrative and raw reads on a separate authorized path rather than
  modeling them as public visibility.
- Adapt existing stores and supply-chain services to use the contract.
- Preserve current durable schema unless a migration is demonstrably required.

**Tests first:**

- Full matrix for authoritative fact combinations and public resource classes,
  including unknown values that must fail closed.
- Unlisted exact-version restore remains possible.
- Quarantined and deleted packages never leak through public resources.
- Symbols follow the same publication policy.
- Independently differing resource classes cannot be pre-filtered through another
  class's visibility decision.

**Done when:** Protocol code no longer implements visibility rules independently.

**Rollback:** Revert the PR; no public contract exists yet.

### Step 3: Introduce internal operation contracts

**Goal:** Describe current behavior without moving it.

**Changes:**

- Add internal operation IDs.
- Add typed request, response, and error contracts for current endpoints.
- Keep contracts independent of `HttpContext`, ASP.NET result types, SQLite types,
  and concrete stores.
- Add internal interfaces for operation owners, validators, contributors, and
  policy participants.
- Mark all contracts internal and pre-compatibility.

**Initial operation families:**

- Service index.
- Flat container and symbols.
- Registration.
- Search.
- Push, list, unlist, relist, and delete.
- Moderation.
- Vulnerabilities.
- Test control.
- Health, diagnostics, backup, and restore.

**Tests first:**

- Contract serialization tests.
- Error mapping tests.
- Operation ID uniqueness tests.
- Architecture tests rejecting ASP.NET and storage implementation types in contract
  assemblies.

**Done when:** Every current endpoint can be named and represented by a typed
operation contract.

**Rollback:** Revert the contract project and tests.

## Phase 2: Build the kernel composition path

### Step 4: Add per-instance server profiles

**Goal:** Replace the growing construction overload set with one composable
configuration model while preserving all overloads.

**Changes:**

- Add internal `ServerProfile`, `ExtensionSelection`, and capability-grant models.
- Define `embedded`, `standard`, and `production` built-in profiles.
- Make existing `NuGetTestServerHost.StartAsync` overloads translate to the embedded
  profile.
- Make CLI options translate to standard or production profiles.
- Do not load external extensions yet.

**Tests first:**

- Every existing overload maps to equivalent configuration.
- Two parallel hosts can use different profiles without shared mutable state.
- Embedded denies outbound network and sidecars by default.
- Production requires durable storage, security, operations, and supply-chain policy.

**Done when:** Both CLI and programmatic hosts pass through one profile model.

**Rollback:** Existing overloads remain the public facade; revert their internal
translation.

### Step 5: Add the extension catalog and graph validator

**Goal:** Resolve built-in components declaratively before executing them.

**Changes:**

- Add internal manifest records.
- Register current feature groups as built-in manifest descriptors.
- Add dependency, version, operation-ownership, route-conflict, resource-link, and
  capability validation.
- Produce deterministic startup diagnostics.
- Keep all implementations in the existing assembly.

**Tests first:**

- Duplicate operations and routes fail.
- Missing dependencies and cycles fail.
- Missing linked resources fail.
- Ungranted required capabilities fail before listening.
- Ordering is stable across runs and operating systems.

**Done when:** Startup can print one valid resolved graph for each built-in profile.

**Rollback:** Bypass the catalog and retain direct composition until the PR is
reverted.

### Step 6: Add the typed operation registry

**Goal:** Give every operation exactly one active owner without changing routes.

**Changes:**

- Register adapters around existing endpoint methods.
- Resolve handlers by operation ID.
- Keep existing route mapping temporarily, but make route handlers call the registry.
- Add kernel error translation, cancellation, limits, and diagnostics around
  registry dispatch.

**Tests first:**

- One owner per operation.
- Unknown operations fail startup or return the correct internal error.
- Existing endpoint characterization tests remain unchanged.
- Dispatch preserves cancellation and streaming.

**Done when:** Existing routes contain no feature logic beyond binding and registry
dispatch.

**Rollback:** Route handlers can be changed back to direct method calls by reverting
one PR.

### Step 7: Add the capability broker

**Goal:** Remove direct store and privileged-service access from operation owners.

**Changes:**

- Implement internal capability interfaces for package reads, content streams,
  publication, moderation, extension state, events, backup, control instrumentation,
  outbound HTTP, and secrets.
- Give each built-in owner a capability-scoped context.
- Fail startup when required grants are absent.
- Audit privileged broker calls.

**Tests first:**

- Denied capabilities cannot be invoked.
- Capabilities cannot expose service providers, database connections, storage paths,
  or raw secrets.
- Required versus optional grant behavior.
- Per-host and per-extension attribution.
- Stream, timeout, and quota enforcement.

**Done when:** Operation owners no longer resolve `IPackageStore`,
`PackageSupplyChainService`, or other privileged concrete services directly.

**Rollback:** Revert adapters to existing service injection.

### Step 8: Move request instrumentation into the kernel gateway

**Goal:** Preserve fault injection and request recording without exposing arbitrary
middleware.

**Changes:**

- Add the profile-gated test-instrumentation stage to the kernel gateway.
- Move matching, delay, injected response, and request redaction into that stage.
- Let the control operation owner configure the stage through capabilities.
- Forbid grants in production profiles.

**Tests first:**

- Existing fault behavior remains compatible.
- Instrumentation runs before binding and can short-circuit.
- Authorization, API keys, cookies, and configured sensitive headers are redacted.
- Production cannot activate test instrumentation.

**Done when:** The future control extension can be extracted without middleware
access.

**Rollback:** Revert to existing middleware and stores.

## Phase 3: Prove the extension model internally

### Step 9: Extract service-index composition

**Goal:** Make discovery data derive from the validated resource registry.

**Changes:**

- Create the internal `NuGet.ServiceIndex` owner.
- Replace hard-coded descriptors with typed resource contributions.
- Keep URL generation in the kernel.
- Require all selected resources to be ready before listening.

**Tests first:**

- Exact current service-index resources, versions, ordering, URLs, and comments.
- Unsupported and unavailable resources are not advertised.
- Linked-resource validation catches unusable profiles.

**Done when:** Adding a built-in resource descriptor no longer requires editing the
service-index implementation.

**Rollback:** Restore the current hard-coded descriptor builder.

### Step 10: Extract vulnerability information

**Goal:** Validate resource ownership, background work, state, and outbound HTTP on a
low-risk read-only feature.

**Changes:**

- Move vulnerability endpoints and refresh orchestration behind the internal
  extension contract.
- Keep embedded snapshots deterministic and network-free.
- Use brokered extension state and outbound HTTP in CLI profiles.
- Migrate the existing vulnerability cache through a bounded, owner-scoped logical
  legacy-state reader without exposing storage paths to the extension.
- Keep the official extension in-process.

**Tests first:**

- Existing audit and protocol tests.
- Embedded host never refreshes from the network.
- Cache, stale fallback, integrity, and restart behavior.
- Extension failure and health behavior.

**Done when:** Vulnerability support has no direct registration in
`ServerApplication`.

**Rollback:** Re-register the existing provider and endpoints.

### Step 11: Extract test-control operations

**Goal:** Prove that an official extension can control kernel-owned instrumentation.

**Changes:**

- Move control endpoint ownership into `NuTest.Control`.
- Access package generation, reset, faults, and recordings through capabilities.
- Keep actual request interception in the kernel.
- Use only the host-scoped `control.packages.manage` and
  `control.instrumentation.manage` capabilities; production profiles cannot select
  or grant them.

**Tests first:**

- All current package generation, reset, fault, and request APIs.
- Redaction and production-profile denial.
- Parallel-host state isolation.

**Done when:** No control endpoint is mapped directly by `ServerApplication`.

**Rollback:** Restore direct control endpoint mapping.

### Step 11A: Generate startup-frozen routes from typed descriptors

**Goal:** Close the gap between declarative route strings and statically mapped
ASP.NET endpoint classes.

**Changes:**

- Define transport-neutral descriptors for method, semantic path template,
  parameter/body/stream binding, HEAD policy, access policy, limits, operation ID,
  and contract versions.
- Validate semantic path collisions, reserved prefixes, ownership, binding
  completeness, and contract compatibility.
- Generate and freeze the route table before listening.
- Keep runtime route mutation out of scope.

**Tests first:**

- A separately compiled route fixture adds `/flavors/index.json` through a descriptor,
  binder or codec, and owner without kernel source edits. Step 11C retains and extends
  this fixture into the full conformance module.
- Equivalent parameterized templates collide deterministically.
- Reserved-prefix, ownership, access-policy, limit, HEAD, and contract failures stop
  startup.
- Extensions never receive `WebApplication`, root DI, or endpoint-routing objects.

**Done when:** Adding a descriptor, binder or codec, and owner is sufficient to expose
a compatible route with no static endpoint mapping.

**Rollback:** Keep the generated path behind internal composition until all existing
routes pass characterization; revert to static mapping in one PR.

### Step 11B: Project absolute URLs from kernel route references

**Goal:** Remove host-derived absolute URLs from operation contracts.

**Changes:**

- Define typed route references and route parameters.
- Project absolute URLs in the kernel after trusted-proxy processing.
- Migrate service-index projection to the common mechanism.
- Remove `BaseAddress` and equivalent absolute-URL inputs from extension contracts
  before registration or search extraction.

**Tests first:**

- Direct, forwarded-host, forwarded-prefix, and untrusted-proxy cases.
- Route-reference ownership, missing parameter, encoding, and version failures.
- Golden service-index, registration, search, and vulnerability URLs.

**Done when:** Owners return route references and parameters; no extension contract
contains a host-derived absolute URL.

**Rollback:** Retain current kernel URL projection adapters until all URL-bearing
contracts use route references.

### Step 11C: Prove closed-world composition and enforce fitness gates

**Status:** Implemented. `tests/NuGet.TestServer.RouteFixture` now references only
`src/NuGet.TestServer.Extensions.Abstractions` and contributes `/flavors/index.json`
through `IExtensionModule`.

**Goal:** Demonstrate composition independently of built-in registration before more
large extraction or SDK work.

**Changes:**

- Extend the retained Step 11A separately compiled fixture into a test-only module
  that contributes an operation, route, binder or codec, resource, and requested
  capabilities with zero kernel source changes.
- Add architecture tests for route coverage, action-scoped capability signatures,
  transport-neutral contracts, one-owner composition, and forbidden dependencies.
- Replace or formalize `OperationHttpResult` as a versioned, transport-neutral
  rendering result; reach zero kernel-specific rendering escapes.
- Canonicalize capability names across code, manifests, tests, and documentation.
- Decompose any owner-shaped capability before proceeding.

**Implemented shape:**

- The transport-neutral contracts a module needs moved into the contract assembly:
  `EndpointDescriptor` and its binding surface, `ExtensionManifest`,
  `ExtensionSelection`, `CapabilityRequest`, `IOperationOwnerRegistry`,
  `IExtensionCapabilities`, `IExtensionModule`, and `ExtensionModuleContribution`.
- `OperationHttpResult` is gone. `OperationResult` is one immutable, versioned,
  transport-neutral rendering contract with semantic outcomes; the kernel is the only
  component that maps an outcome onto an HTTP status code and serializes a body.
- Owners attach a rendering to `OperationResponse<T>`, so the official Control and
  Vulnerabilities owners no longer take `OperationExecutionContext`. Content moves
  through kernel-issued `StreamHandle` values resolved inside the kernel.
- `OperationFamily` is an open value, so a module declares its own family
  (`Contoso.Flavors`) instead of borrowing a built-in one.
- Extension-facing capabilities were decomposed: `IPackageControlCapability` and
  `IKernelInstrumentationControlCapability` now exchange abstraction documents and
  stream handles, and the kernel-internal fixture surfaces
  (`IPackageFixtureCapability`, `IKernelInstrumentationFixtureCapability`) keep the
  programmatic test host working. `IVulnerabilityCatalogCapability` replaces the
  vulnerability owner's direct snapshot access.
- `host.clock.read` (`IHostClockCapability`) is the narrow, serializable kernel
  capability the conformance module requests and the broker grants or denies.

**Tests first:**

- The `/flavors/index.json` conformance module runs through the real gateway.
- Fitness tests reject `OperationExecutionContext`, `TestPackage`,
  `StorageBackupManifest`, stores, paths, DI, ASP.NET, and kernel types in extension
  contract or capability signatures.
- Structural contract hashes and golden snapshots detect semantic drift.

**Done when:** The sample composes without kernel edits and every architecture fitness
gate is automatic. This is a conformance proof, not SDK publication or external
discovery.

**Stop condition:** If the sample cannot contribute its route and operation without
kernel edits, reframe the product as a modular monolith rather than claiming a
third-party extension platform.

**Rollback:** Revert the Step 11C conformance additions while retaining the Step 11A
route fixture; no public compatibility commitment exists.

### Step 11D: Establish scalability and backpressure baselines

**Goal:** Measure the abstraction cost and fix hot-path behavior before high-volume
resource extraction.

**Changes and provisional gates:**

- Measure current metadata-read latency and allocations, then ratify a gateway and
  capability overhead budget; the initial target is at most 5% p95 overhead.
- Exercise 512 concurrent flat-container reads. Overload must wait within a deadline
  or return typed `503` plus `Retry-After`, never an unhandled quota exception.
- Prove constant-memory streaming, declared limits for every handle type, and lease
  release on EOF, disposal, and cancellation.
- Establish embedded-host startup and 100-parallel-host baselines with no shared
  mutable state.
- Measure deterministic catalog resolution at 8, 50, and 200 manifests/routes.
- Bound health/readiness latency without synchronous full-storage enumeration.
- Audit hot reads through sampling or aggregation unless full auditing proves bounded
  allocation and latency; keep privileged mutations fully audited.
- Record CI/test growth and run the conformance sample.

**Done when:** Baselines are recorded, budgets are ratified from evidence, and hot
paths meet the approved budgets. Absolute values remain provisional until measured.

**Scope:** v1 is per-process and single-node, including many parallel embedded hosts.
Multi-node storage, distributed coordination, and remote sidecars are not implied.

**Rollback:** Performance changes remain separable from contract changes and can be
reverted while retaining the measured baseline.

### Step 12A: Add transactional extension state and checkpoints

**Goal:** Define durable required-state semantics before extracting operational
ownership.

**Changes:**

- Add namespaced schema versions and explicit migrations.
- Add optimistic concurrency tokens or ETags.
- Enforce per-record and per-owner quotas, bounded buffering and streaming, and
  bounded lock cardinality.
- Integrate required extension state with a kernel checkpoint and backup participant
  protocol.
- Stage and commit restore crash-safely.
- Require authoritative extension state to use the kernel store; external state is
  rebuildable only.

**Tests first:**

- Concurrency-token conflicts, migrations, quotas, bounded buffering, and lock
  cardinality.
- SQLite and file-content concurrent mutation during backup.
- Crash matrix for prepare, export, validation, restore staging, commit, and abort.
- One checkpoint is produced or backup reports explicit unavailability.

**Done when:** Package, publication, and required extension state can produce and
restore one consistent checkpoint.

**Rollback:** Preserve current storage backup format and owner until the new
checkpoint path is proven.

### Step 12B: Extract operations, health, backup, and restore

**Goal:** Validate privileged operations and coordinated state participation.

**Changes:**

- Move operation endpoint ownership into `NuTest.Operations`.
- Keep health aggregation and atomic checkpoint authority in the kernel.
- Use the Step 12A checkpoint contract.
- Mark projections rebuildable rather than authoritative backup state.

**Tests first:**

- Health, readiness, diagnostics, integrity, backup, restore, and recovery.
- Restore with missing, extra, incompatible, and newer extension state.
- Interrupted backup and restore.
- Windows lease and cleanup behavior.

**Done when:** Backup and restore cover one consistent required-state checkpoint and
operational endpoints have one extension owner.

**Rollback:** Existing storage backup implementation remains recoverable by reverting
the owner adapter.

## Phase 4: Extract NuGet read resources

### Step 13: Extract flat-container and symbol reads

**Goal:** Move the simplest high-volume protocol resource while proving streaming.

**Changes:**

- Create `NuGet.FlatContainer`.
- Use brokered metadata and streaming content handles.
- Use the authoritative resource-class visibility decision before every response.
- Preserve range, HEAD, cancellation, integrity, and transfer-limit behavior.

**Tests first:**

- Exact version enumeration and content behavior.
- Published/unlisted/quarantined/deleted matrix.
- Symbols.
- Large streaming packages and cancellation.
- Real restore.

**Done when:** Flat-container routes are owned solely by the official extension.

**Rollback:** Revert route ownership to the legacy adapter.

### Step 14: Extract registration

**Goal:** Move registration document creation and paging.

**Changes:**

- Create `NuGet.Registration`.
- Introduce typed document builders and namespaced contribution slots.
- Require flat-container URL production.
- Keep indexed, body-free metadata queries.

**Tests first:**

- Index, page, leaf, HEAD, bounds, rich metadata, package types, listed state, and
  symbols.
- Embedded-page versus paged behavior.
- No package-body reads for metadata operations.
- Real NuGet.Protocol metadata queries.

**Done when:** Registration has one official extension owner and can accept a test
document contributor.

**Rollback:** Revert to the legacy registration adapter.

### Step 15: Extract search

**Goal:** Move indexed querying and prove deterministic projection consistency.

**Changes:**

- Create `NuGet.Search`.
- Use brokered indexed queries and authoritative visibility post-filtering.
- Declare registration and flat-container URL dependencies.
- Preserve strong consistency for standard and embedded profiles.

**Tests first:**

- Totals, paging, stable ordering, prerelease, package type, listed state, and rich
  metadata.
- Immediate publish/unlist/quarantine/delete visibility.
- Concurrency and performance budgets.
- No package-body reads.

**Done when:** Search is extension-owned and remains read-your-writes consistent.

**Rollback:** Revert to the legacy search adapter.

## Phase 5: Extract package mutation and policy

### Step 16: Extract supply-chain policy participation

**Goal:** Separate policy decisions from authoritative package state.

**Changes:**

- Create `NuTest.SupplyChain` policy participants.
- Keep state transitions, publication visibility, and moderation authority in the
  kernel.
- Define required authoritative participants for production.
- Remove all fail-open paths.

**Tests first:**

- Signature, scanner, ownership, namespaces, quotas, moderation, recovery, and audit.
- Missing or failed policy participants prevent production readiness.
- Embedded deterministic scanner behavior.

**Done when:** Supply-chain policy is extensible without allowing extensions to
directly mutate package state.

**Rollback:** Restore the existing direct policy adapter.

### Step 17: Extract push, list, unlist, relist, and delete

**Goal:** Move package-management workflows only after read and policy boundaries are
proven.

**Changes:**

- Create `NuGet.PackageManagement`.
- Use nonreplaceable kernel mutation capabilities.
- Preserve immutable/idempotent duplicate behavior.
- Keep streaming and transactional publication in the kernel.

**Tests first:**

- NuGet push, duplicate push, symbols, unlist, relist, delete, ownership, scopes,
  throttling, transfer limits, and rollback.
- Immediate visibility across flat container, registration, and search.
- Restart and recovery after interrupted mutation.

**Done when:** Package-management routes are extension-owned, while authoritative
transactions remain kernel-owned.

**Rollback:** Revert ownership to the existing adapters; durable schema remains
compatible.

## Phase 6: Split assemblies and publish the supported SDK

### Step 18: Move official extensions into a separate assembly

**Goal:** Enforce architecture boundaries before supporting third parties.

**Changes:**

- Create `NuGet.TestServer.Extensions.Abstractions`.
- Create one initial `NuGet.TestServer.Extensions.Official` assembly.
- Move contracts and official implementations without changing profile behavior.
- Add project-reference and namespace boundary checks.
- Enforce a one-way assembly dependency graph and typed capability injection without
  kernel references to official implementations.

**Tests first:**

- Official extension assembly cannot reference kernel implementation namespaces.
- Kernel does not reference official implementation types.
- Standard and embedded profiles remain behaviorally identical.

**Done when:** The default server is composed from an official extension assembly
through abstractions.

**Rollback:** Recombine assemblies without changing contracts.

### Step 19: Stabilize manifests and SDK contracts

**Goal:** Decide what can be supported publicly.

**Changes:**

- Review every abstraction exercised by official extensions.
- Remove accidental or overly broad contracts.
- Version manifest, SDK, operation, and contribution contracts.
- Define deprecation and support policy.
- Add extension templates, validators, fakes, and contract-test packages.
- Require zero kernel-specific rendering escapes.
- Freeze the route/binding and route-reference projection contracts proven in
  Steps 11A through 11C.
- Require action-scoped serializable capabilities.
- Define structural contract identity with hashes and golden snapshots.
- Decide the SDK support window, package signing and publisher identity, replacement
  scope, manifest JSON versus code, and supported target frameworks.

**Tests first:**

- Oldest/newest supported contract compatibility.
- Manifest validation.
- Package conformance attestation.
- Signed attestation generation tied to structural contract identity.
- Replacement restrictions.

**Done when:** The SDK has passed all official extensions and is intentionally
supportable, every pre-publication decision is recorded, and the host can verify a
signed attestation against the selected contract version.

**Rollback:** Do not publish; contracts remain internal until approved.

### Step 20: Add trusted third-party in-process loading

**Goal:** Support the first external extension without sidecar complexity.

**Changes:**

- Add administrator-installed package discovery.
- Validate package identity, manifest, compatibility, conformance, routes,
  dependencies, and capabilities.
- Verify the signed conformance attestation against the package identity, manifest
  digest, and selected structural contract versions before activation.
- Load through a dedicated `AssemblyLoadContext`.
- Require restart for installation, update, enable, disable, and unload.

**Tests first:**

- Valid sample resource.
- Dependency/version failures.
- ID and route squatting.
- Denied capabilities.
- Dependency isolation.
- Startup and shutdown failures.

**Done when:** A sample out-of-repository extension adds a resource without kernel
changes.

**Rollback:** Disable external package discovery; official bundles continue working.

## Phase 7: Add isolation only when justified

### Step 21: Build sidecar transport and supervision

**Entry condition:** At least one concrete extension requires process isolation or a
different implementation language, and transport-neutral parity is demonstrated.

**Goal:** Add out-of-process execution without changing contracts.

**Changes:**

- Implement kernel-created named pipes and Unix sockets.
- Add bootstrap-token authentication and channel-bound capability credentials.
- Add version negotiation, stream handles, deadlines, quotas, health, circuit
  breaking, restart policy, and structured logs.
- Keep remote sidecars out of scope initially.

**Tests first:**

- Endpoint pre-creation and impersonation resistance.
- Protocol mismatch.
- Crash, hang, oversized payload, cancellation, stream interruption, and restart.
- Restricted identity behavior where supported.
- In-process and sidecar contract parity.

**Done when:** The same sample extension passes in both execution modes and cannot
crash or corrupt the host.

**Rollback:** Disable sidecar execution while retaining in-process support.

### Step 22: Implement Package Staging as the reference external extension

**Dependency:** Step 20. Step 21 is required only when the selected deployment needs
process isolation.

**Goal:** Prove a substantial independent workflow.

**Changes:**

- Implement the selected Package Staging resource and operations.
- Store staging groups in extension-owned state.
- Write staged content through bounded capabilities.
- Request publication through the kernel.
- Keep staging and supply-chain states orthogonal.

**Tests first:**

- Ownership, groups, paging, packages, symbols, quotas, expiration, restart, backup,
  publication, and failure recovery.
- Published-only NuGet visibility.
- In-process and sidecar execution where supported.

**Done when:** Package Staging ships without core database, storage-root, or secret
access.

**Rollback:** Remove the optional extension; standard server behavior is unchanged.

## Revised pull request sequence

Steps 1 through 11 are complete. Do not renumber their existing GitHub issues.

| Order | Deliverable | Depends on |
| --- | --- | --- |
| 11A | Typed dynamic routes and startup route generation | Step 11 |
| 11B | Kernel URL-reference projection | 11A |
| 11C | Separately compiled conformance proof and fitness gates | 11A, 11B |
| 11D | Scalability/backpressure baseline and hot-path fixes | 11C |

After 11A through 11D, parallel work is allowed only where ownership and files are
truly disjoint:

- **Lane A:** 12A transactional extension state, then 12B operations/health/backup/
  restore extraction.
- **Lane B:** Step 13 flat container. Then mechanically split shared registration and
  search code; Steps 14 and 15 may proceed in parallel only after that split and the
  Step 11B URL-projection gate.
- **Lane C:** Step 16 supply-chain policy.
- **Merge point:** Step 17 package management requires the completed read lane and
  policy lane.

Architecture fitness enforcement begins in Step 11C; the physical official assembly
split remains Step 18. Step 19 stabilizes the public SDK, Step 20 loads trusted
in-process extensions, and Step 21 remains consumer-gated. Step 22 depends on Step 20
and on Step 21 only if isolation is required.

## Progress checkpoints

### Checkpoint A: Safe internal kernel

After Step 8:

- No public SDK exists.
- External behavior is unchanged.
- Profiles, catalog, registry, capabilities, and instrumentation work internally.
- The migration can stop here without stranding users.

### Checkpoint B: Closed-world extension proof

After Step 11D:

- A separately compiled module contributes a real route and resource without kernel
  edits.
- Route generation, URL projection, rendering, capability boundaries, and
  performance budgets are enforced.
- If this proof fails, the product is described as a modular monolith.

### Checkpoint C: Complete official microkernel

After Step 18:

- All default feature owners live in the official extension assembly.
- Kernel invariants remain internal and authoritative.
- Standard CLI and embedded hosts preserve current behavior.

### Checkpoint D: Public platform

After Step 20:

- Third-party extensions can be loaded safely in-process.
- SDK compatibility commitments begin.
- Sidecars remain optional and evidence-driven.

## Stop conditions

Pause and revise the architecture if:

- Characterization tests cannot express required NuGet compatibility.
- Strong read-your-writes behavior cannot be preserved.
- Programmatic-host startup or request performance regresses beyond an approved
  budget.
- An official extension requires raw kernel implementation access.
- Capability contracts become broad aliases for unrestricted services.
- A capability signature contains an operation context, package implementation model,
  backup implementation manifest, store, path, dependency-injection object, or
  kernel type.
- A separately compiled route contribution requires kernel source edits.
- An owner requires `OperationHttpResult` or equivalent kernel-specific result state
  after Step 11C.
- Host-derived absolute URLs remain in extension contracts before registration or
  search extraction.
- Backup cannot produce one consistent required-state checkpoint.
- Sidecar contracts require different semantics from in-process contracts.
- Cross-platform CI exposes incompatible lifecycle or filesystem assumptions.

## Decision register and deadlines

Resolve these questions before the named gate:

### Before Step 12A

- What is the authoritative extension-state model?
- How do SQLite metadata and file content join one transaction/checkpoint?
- What are the exact per-record and per-owner quotas, concurrency-token semantics,
  migration rules, buffering limits, and lock-cardinality bounds?

### Before Step 18

- What one-way assembly dependency graph is enforceable?
- How are typed capabilities injected without kernel references to official
  implementations?

### Before Step 19

- Is the route/binding surface public in v1 or contributor-only?
- What replaces `OperationHttpResult`?
- What SDK version window is supported?
- How is structural contract identity hashed and snapshotted?
- What package signing and publisher identity are required?
- Which operations, if any, are replaceable? Does search alone justify replacement
  machinery?
- Are manifests JSON, code, or both?
- Which target frameworks are supported?
- Should v1 deliberately ship as an in-process extension platform first?
- What happens when an optional extension fails during startup?

### Before Step 21

- Which RPC technology is selected?
- How are stream-handle backpressure, expiry, cancellation, and lifecycle modeled?
- What authentication and sandboxing are required?
- Is transport local-only or remote-capable?
- How are errors, deadlines, and rendering kept semantically identical to in-process
  execution?

## Final definition of done

Migration is complete when:

1. `ServerApplication` contains kernel composition and gateway code, not feature
   endpoint implementations.
2. Every endpoint maps to one typed operation owned by an official or configured
   extension.
3. All official extensions use public SDK contracts and capability interfaces.
4. Standard CLI behavior passes the original compatibility baseline.
5. Embedded hosts remain deterministic, isolated, parallel-safe, and in-memory.
6. Production security, durability, moderation, backup, and recovery remain
   fail-closed.
7. A third-party extension can add a resource without kernel changes.
8. Package Staging can operate without privileged implementation access.
9. Sidecar support, if built, is authenticated, bounded, supervised, and optional.
10. Documentation clearly distinguishes kernel invariants, official extensions,
    optional extensions, and supported replacement points.
