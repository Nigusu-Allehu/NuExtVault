# Microkernel Migration Plan

## Status

Approved migration plan. Each implementation step still requires its own focused
proposal and explicit approval under `AGENTS.md`.

Implementation status: Steps 1 through 11 are implemented. Step 11 has local
validation only; cross-platform CI remains a completion gate.

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
  - Maps protocol, moderation, health, and test-control endpoints.
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

### Step 12: Extract operations, health, backup, and restore

**Goal:** Validate privileged operations and coordinated state participation.

**Changes:**

- Move operation endpoint ownership into `NuTest.Operations`.
- Keep health aggregation and atomic checkpoint authority in the kernel.
- Adapt durable package and extension state to the checkpoint contract.
- Mark projections rebuildable rather than authoritative backup state.

**Tests first:**

- Health, readiness, diagnostics, integrity, backup, restore, and recovery.
- Restore with missing, extra, incompatible, and newer extension state.
- Interrupted backup and restore.
- Windows lease and cleanup behavior.

**Done when:** Backup and restore cover one consistent required-state checkpoint.

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

**Tests first:**

- Oldest/newest supported contract compatibility.
- Manifest validation.
- Package conformance attestation.
- Replacement restrictions.

**Done when:** The SDK has passed all official extensions and is intentionally
supportable.

**Rollback:** Do not publish; contracts remain internal until approved.

### Step 20: Add trusted third-party in-process loading

**Goal:** Support the first external extension without sidecar complexity.

**Changes:**

- Add administrator-installed package discovery.
- Validate package identity, manifest, compatibility, conformance, routes,
  dependencies, and capabilities.
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
different implementation language.

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

## Suggested pull request sequence

| PR | Deliverable | Depends on |
| --- | --- | --- |
| 1 | Compatibility baseline | Current main |
| 2 | Package state and visibility | 1 |
| 3 | Internal operation contracts | 2 |
| 4 | Profiles and embedded composition | 3 |
| 5 | Catalog and graph validation | 4 |
| 6 | Operation registry | 5 |
| 7 | Capability broker | 6 |
| 8 | Kernel test instrumentation | 7 |
| 9 | Service-index extension | 8 |
| 10 | Vulnerability extension | 9 |
| 11 | Control extension | 10 |
| 12 | Operations extension | 11 |
| 13 | Flat-container extension | 12 |
| 14 | Registration extension | 13 |
| 15 | Search extension | 14 |
| 16 | Supply-chain policy extension | 15 |
| 17 | Package-management extension | 16 |
| 18 | Official extension assembly | 17 |
| 19 | Public SDK stabilization | 18 |
| 20 | Third-party in-process loading | 19 |
| 21 | Sidecar execution, when justified | 20 |
| 22 | Package Staging | 20 or 21 |

PRs are intentionally sequential through the core extraction because each changes
operation ownership. Do not develop upper ownership changes from stale lower
branches.

## Progress checkpoints

### Checkpoint A: Safe internal kernel

After Step 8:

- No public SDK exists.
- External behavior is unchanged.
- Profiles, catalog, registry, capabilities, and instrumentation work internally.
- The migration can stop here without stranding users.

### Checkpoint B: Official extension proof

After Step 12:

- Low-risk official features are extension-owned.
- Background work, state, control, backup, and privileged operations are proven.
- Reassess contracts before extracting high-volume NuGet resources.

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
- Backup cannot produce one consistent required-state checkpoint.
- Sidecar contracts require different semantics from in-process contracts.
- Cross-platform CI exposes incompatible lifecycle or filesystem assumptions.

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
