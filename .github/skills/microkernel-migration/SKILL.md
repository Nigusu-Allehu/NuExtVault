---
name: microkernel-migration
description: Guides NuExtVault microkernel migration work. Use when proposing, implementing, reviewing, testing, restacking, or documenting any step that moves current server behavior into the kernel, operation registry, capability broker, profiles, official extensions, public extension SDK, sidecars, or Package Staging.
---

# NuExtVault microkernel migration

Follow this skill for every microkernel migration task.

## Authoritative documents

Read these files before proposing or changing code:

1. `AGENTS.md`
2. `design/microkernel-extension-architecture.md`
3. `design/microkernel-migration-plan.md`

`AGENTS.md` controls the development workflow. The architecture controls system
boundaries and invariants. The migration plan controls sequencing and completion
criteria.

If the documents conflict, stop and ask for a design correction. Do not silently
choose one interpretation.

## Determine the active migration step

Before planning:

1. Inspect current `main`, open migration PRs, and merged migration history.
2. Identify the last completed migration step.
3. Select only the next uncompleted step whose dependencies are merged.
4. Confirm that the task belongs to that step.
5. State the step number and name in the proposal and PR description.

Do not implement an upper step from a stale or unmerged lower-step branch. Do not
combine multiple migration steps into one PR unless the user explicitly approves a
revised plan explaining why they cannot remain independently attainable.

Steps 1 through 11 are complete through merged PR #62. The old Step 12 is paused.
The next eligible work is Step 11A, then 11B, 11C, and 11D. Do not begin Step 12A,
12B, or another extraction until all four prerequisites pass.

After Step 11D, use only the approved lanes:

- Lane A: 12A, then 12B.
- Lane B: 13, then a mechanical registration/search split, then 14 and 15 may run in
  parallel after the URL-projection gate.
- Lane C: 16.
- Step 17 waits for the read and policy lanes.

## Required workflow

For each step:

1. Inspect existing implementation and tests.
2. Write a focused proposal covering behavior, contracts, compatibility, security,
   migration, tests, and rollback.
3. Wait for explicit user approval.
4. Write unit and functional tests before production changes.
5. Confirm new tests fail for the expected reason.
6. Implement the smallest complete change for the approved step.
7. Run targeted tests, then full validation.
8. Update documentation last.
9. Report the exact completion and rollback criteria.

Never infer approval from a request to discuss, design, review, or estimate a step.

## One active owner rule

Every externally callable operation has exactly one active owner.

- An adapter may temporarily wrap legacy logic during migration.
- Two implementations must not both serve the same operation.
- Route ownership must not depend on registration order.
- Operation and concrete route-path conflicts fail startup.
- Removing a legacy owner requires characterization coverage and a tested rollback
  point.

## Kernel boundary

The kernel owns:

- Host-instance lifecycle and resolved profiles.
- Route binding, authentication, authorization, limits, cancellation, error mapping,
  and diagnostics.
- Capability enforcement and audit attribution.
- Package identity, content integrity, authoritative state, transactions,
  publication, visibility, moderation transitions, and recovery.
- Kernel-created test-instrumentation interception and request redaction.
- Consistent backup and restore checkpoints.

Extensions own features and typed operations. They may request kernel actions only
through capabilities.

Never give an extension:

- `WebApplication` or `IEndpointRouteBuilder`
- An unrestricted service provider
- A database connection or storage-root path
- Raw secrets
- Direct writes to authoritative package, publication, moderation, ownership, or
  recovery state
- Arbitrary middleware insertion
- Arbitrary JSON mutation

Official extensions follow the same rules as third-party extensions. If an official
extension needs an unavailable capability, propose a narrow public capability
instead of adding a private escape hatch.

Routes must come from typed, transport-neutral descriptors that declare method, path
template, parameter/body/stream binding, HEAD policy, access policy, limits,
operation ID, and contract versions. The kernel validates semantic collisions,
reserved prefixes, ownership, and contracts, then generates and freezes routes
before listening. Runtime route mutation is out of scope.

A separately compiled test module must be able to add `/flavors/index.json` with an
operation, binder or codec, resource, and requested capabilities without kernel
source changes. If it cannot, stop and reframe the architecture as a modular monolith.

## Compatibility invariants

Preserve these invariants in every step:

1. Existing NuGet URLs, payloads, status codes, headers, ordering, casing, paging,
   HEAD behavior, and client workflows remain compatible unless explicitly approved.
2. Standard and embedded profiles provide read-your-writes consistency.
3. Public protocol responses are filtered through authoritative resource-class
   visibility immediately before serialization.
4. Unlisted packages remain downloadable by exact ID/version and represented as
   unlisted in registration, but remain absent from search.
5. Quarantined, deleted, and recovered packages never leak through public resources.
6. Programmatic hosts remain deterministic, in-memory, parallel-safe, and
   network-independent by default.
7. Production security and integrity decisions fail closed.
8. Package and symbol streams remain bounded, cancellable, and non-buffering.

## Contract rules

- Use stable operation IDs.
- Keep request, response, and error contracts independent of ASP.NET Core, SQLite,
  and implementation types.
- Make contracts serializable and asynchronous so they can support future sidecars.
- Use route references plus typed parameters instead of base addresses or
  host-derived absolute URLs. The kernel performs trusted-proxy-aware URL projection.
- Version manifests, SDK APIs, RPC protocols, and operation contracts independently.
- Default operation replacement to disabled.
- Never make authoritative publication, moderation, ownership, identity, or recovery
  mutations replaceable in v1.
- Run replacement conformance tests during package validation or CI; startup verifies
  the attestation.

Do not publish the third-party SDK before the migration plan's SDK stabilization
step.

Before SDK publication, reach zero kernel-specific rendering escapes. Eliminate
`OperationHttpResult` use by owners or formalize a transport-neutral, versioned
rendering contract. Enforce structural contract hashes and golden snapshots.

## Capability rules

- Deny capabilities by default.
- Distinguish required and optional requests.
- Fail profile validation for an ungranted required capability.
- Scope handles to host instance and extension identity.
- Enforce cancellation, quotas, stream limits, and audit on every privileged call.
- Keep interfaces action-scoped. Reject signatures containing
  `OperationExecutionContext`, `TestPackage`, `StorageBackupManifest`, stores,
  filesystem paths, dependency injection, ASP.NET, or kernel types.
- Treat outbound HTTP, secret references, restore, moderation, publication, and test
  instrumentation as sensitive capabilities.
- Production profiles cannot grant test fault-injection or request-recording access.

## Extension state and events

- Keep required authoritative extension state in the kernel-provided transactional
  state store.
- Before Step 12B, implement schema versions and migrations, optimistic concurrency,
  per-record and per-owner quotas, bounded streaming and buffering, bounded lock
  cardinality, checkpoint participation, and crash-safe restore.
- Treat external stores only as rebuildable projections.
- Durable at-least-once events are not implemented. Add them only for a concrete
  projection consumer; when added, make consumers idempotent.
- Preserve normalized package-ID ordering for package events.
- Post-filter every projection-backed response through authoritative visibility.
- Do not use asynchronous projections for standard or embedded protocol reads unless
  the contract explicitly preserves read-your-writes behavior.

## Programmatic host rules

- Scope catalog, profile, grants, extension instances, routes, cancellation,
  diagnostics, and mutable state to one host instance.
- Process-wide caches may contain only immutable assembly bytes or metadata.
- Keep sidecars opt-in for programmatic hosts.
- Derive sidecar endpoint names from an unguessable host-instance identifier.
- Add or preserve parallel-host isolation tests whenever composition changes.

## Sidecar gate

Do not build sidecar execution until the migration plan's entry condition is met:
there must be a concrete extension requiring process isolation or another
implementation language.

When sidecars are in scope:

- The kernel creates and secures the IPC endpoint before spawning the child.
- Authenticate with a one-time bootstrap token.
- Bind capability credentials to the authenticated channel, extension identity,
  manifest digest, and host instance.
- Use chunked streams or kernel-issued stream handles for package content.
- Bound messages, concurrency, deadlines, restarts, and logs.
- Treat process isolation and security sandboxing as separate concerns.

The v1 lifecycle is restart-required and limited to
validated/started/ready/failed/stopped. Do not claim dynamic unload, durable events,
or full `Degraded` recovery. Sidecars remain gated on a concrete consumer and
transport-neutral parity.

## Scalability gates

Step 11D must establish a measured baseline before budgets are ratified. It executes
the currently applicable gateway, stream, host, catalog, health, audit, and CI gates.
The ratified budget also governs state and backup work when Step 12A introduces those
mechanisms. At minimum:

- Provisional gateway/capability overhead target of at most 5% p95 for metadata reads
  with controlled allocations.
- 512 concurrent flat-container reads return typed `503` plus `Retry-After` or wait
  within a deadline; quota exceptions never escape.
- Constant-memory streams enforce every declared limit and release leases on EOF,
  disposal, and cancellation.
- Embedded startup measurements and 100 parallel hosts with no shared mutable state.
- Deterministic catalog measurements at 8, 50, and 200 manifests/routes.
- Bounded health/readiness latency without synchronous full-storage enumeration.
- Step 12A state quota, buffering, concurrency-token, migration, and
  lock-cardinality tests.
- Sampled/aggregated hot-read auditing or proof of bounded full-audit overhead.
- Step 12A concurrent-mutation/crash backup matrix, plus Step 11D CI/test-growth
  tracking.

Treat v1 as per-process and single-node plus parallel embedded hosts. Multi-node
storage, distributed coordination, and remote sidecars are not implied goals.

## Tests required for every step

Run the smallest targeted tests first, then:

- Full unit tests.
- Full functional tests.
- Warning-as-error Release solution build.
- CLI pack.
- Real NuGet.Protocol scenarios affected by the step.
- `dotnet restore` and `dotnet nuget push` when protocol or package management changes.
- Durable restart/recovery tests when state changes.
- Security tests when routes, capabilities, policies, or profiles change.
- Parallel embedded-host tests when composition changes.
- Windows, Ubuntu, and macOS CI.

Do not weaken performance, concurrency, body-free metadata, streaming, integrity, or
cross-platform assertions to make a migration pass.

## Review checklist

During review, verify:

- The PR implements only its declared migration step.
- All dependencies are already merged.
- Exactly one owner exists per operation.
- No route or resource-link conflicts exist.
- Kernel invariants remain kernel-owned.
- No private extension escape hatch was introduced.
- Contracts contain no implementation types.
- Routes are descriptor-generated and URLs are kernel-projected.
- Owners have no kernel-specific rendering escape.
- Capability signatures are action-scoped and serializable.
- Required capabilities and access policies are explicit.
- Embedded and production profiles remain coherent.
- Tests prove compatibility, failure behavior, and rollback.
- Documentation describes implemented behavior only.

Reject the change if it creates a success-shaped fallback, silently degrades a
required policy, permits stale projections to expose forbidden packages, or makes
extension discovery order observable.

Also reject extraction work that starts before Steps 11A through 11D, owner-shaped
capabilities, static endpoint additions for extension routes, host-derived URLs in
extension contracts, or claims that deferred lifecycle, event, loading, sidecar, or
distributed behavior is implemented.

## Completion report

At the end of a migration step, report:

- Step number and name.
- Proposal approval reference.
- Operation ownership before and after.
- Contracts and capabilities added or changed.
- Compatibility and security behavior preserved.
- Tests and cross-platform CI results.
- Final commit and PR.
- Rollback method.
- The next eligible migration step.

Do not claim the step complete until its expected outcome is persistent, pushed, and
verified.
