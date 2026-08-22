# Microkernel Implementation Review

## Scope and status

This review evaluates the selected microkernel architecture against implementation
evidence through merged PR #62 (Migration Steps 1 through 11) and a two-agent
architecture debate. It records decisions for the next migration phase; it does not
claim that proposed SDK, loading, lifecycle, event, sidecar, or distributed behavior
exists.

The implemented system is a useful closed built-in modular system. It is not yet a
genuinely independently loadable extension platform.

## What Steps 1 through 11 proved

The incremental strategy produced working, reversible boundaries:

- Protocol characterization protects externally observable NuGet behavior.
- One authoritative visibility decision protects public resource classes.
- Profiles and resolved extension graphs are scoped per host instance.
- Dependency, operation, route-string, resource, and capability validation is
  deterministic.
- The typed registry gives each operation one active owner.
- Capability grants are deny-by-default and calls are attributed.
- Request instrumentation remains kernel-owned while control operations configure it.
- Service-index resources are projected from typed contributions.
- Vulnerability and test-control operations can move behind internal extension
  ownership without changing their public behavior.
- The complete sequence passes Windows, Ubuntu, and macOS CI.

These results preserve product value at every rollback point. They argue against a
rewrite.

## What the implementation did not prove

The following was the state before Steps 11A through 11C. Steps 11A, 11B, and 11C have
since generated every route from typed descriptors, moved URL projection into the
kernel, replaced `OperationHttpResult` with the versioned transport-neutral
`OperationResult`, removed `OperationExecutionContext` from official extension owners,
decomposed the capabilities those owners consume, and proved that a separately compiled
module contributes a complete operation, route, binder, resource, and capability request
with no kernel source edits.

The manifests and catalog contain route strings, but ASP.NET endpoint classes still
perform static mapping. Registration, search, vulnerability, and service-index
contracts still expose base addresses or absolute URLs. Extracted owners can complete
kernel-specific `OperationHttpResult` state through `OperationExecutionContext`.
Capability code still has access to implementation-shaped models and services that
cannot become a public transport-neutral boundary unchanged.

Extension state does not yet define the transaction, migration, quota, concurrency,
lock-cardinality, checkpoint, and crash-safe restore semantics required for backup.
Durable at-least-once events and a full `Degraded` lifecycle are designs, not
implemented behavior. Filesystem discovery, assembly load contexts, dynamic unload,
sidecars, and SDK publication remain out of scope and unimplemented.

## Debate

### Challenger position

Pause feature extraction. Route binding remains closed-world even though manifests
look extensible; URL generation leaks transport facts into owner contracts; the
broker risks becoming a service locator made of owner-shaped facades; state and
backup semantics are underspecified; and sidecars cannot preserve parity while
rendering and stream lifetimes remain kernel-specific. Continuing extraction would
move more code across boundaries that are not yet credible as supported extension
contracts.

### Evolutionary advocate position

The migration found these gaps before publishing an SDK, which is the intended
benefit of internal extraction. Contracts remain internal and inexpensive to change.
Three-operating-system tests, compatibility characterization, per-step ownership,
and rollback points retain value regardless of the final public packaging model.
Focused correction PRs are safer and cheaper than discarding the implemented kernel
path.

### Decision

Do not rewrite. Pause the old Step 12 and require Steps 11A through 11D:

1. Generate startup-frozen routes from typed transport-neutral descriptors.
2. Replace base-address contracts with kernel-projected route references.
3. Prove separately compiled closed-world composition and enforce architecture
   fitness, including zero kernel-specific rendering escapes.
4. Establish scalability and backpressure baselines before high-volume extraction.

If `/flavors/index.json` cannot be contributed by a separately compiled test module
without kernel edits, describe NuTestServer as a modular monolith. Do not claim a
third-party extension platform.

## Selected decisions

- Preserve incremental migration and one-owner rollback points.
- Keep route tables startup-frozen; runtime route mutation is out of scope.
- Keep `WebApplication`, root DI, endpoint routing, storage paths, concrete stores,
  and kernel implementation types out of extension contracts.
- Let owners return route references and parameters; let the kernel perform
  trusted-proxy-aware absolute URL projection.
- Keep capabilities action-scoped and serializable. Owner-shaped capability facades
  are a stop condition.
- Require a transport-neutral versioned rendering contract or eliminate rendering
  result state before SDK publication and sidecars.
- Split Step 12 into transactional extension state/checkpoints and operational
  extraction.
- Keep required backup state in the kernel transactional store. External state is
  rebuildable only.
- Defer durable events until a real projection consumer requires them.
- Keep the v1 lifecycle restart-required and limited to
  validated/started/ready/failed/stopped.
- Target per-process, single-node operation and many parallel embedded hosts in v1.
- Prefer an in-process extension platform before sidecars unless a concrete consumer
  requires isolation or another language.

## Rejected or deferred alternatives

| Alternative | Disposition | Reason |
| --- | --- | --- |
| Rewrite the server around a new plugin runtime | Rejected | Existing steps preserve compatibility, pass cross-platform CI, and provide safe correction points. |
| Continue directly to old Step 12 | Rejected | It would deepen route, URL, rendering, capability, and state contracts before proving them. |
| Give extensions ASP.NET routing or root DI | Rejected | It bypasses kernel ownership, limits, deterministic validation, and future transport parity. |
| Runtime route installation or mutation | Deferred | It complicates cached service indexes, readiness, ownership, and rollback without a current requirement. |
| Publish the current internal contracts as an SDK | Rejected | They contain host URLs, kernel rendering state, and implementation-shaped capabilities. |
| Build durable events preemptively | Deferred | No projection consumer currently justifies the durability and ordering machinery. |
| Build sidecars immediately | Deferred | No concrete consumer exists and stream/rendering parity is unresolved. |
| Make multi-node operation a v1 goal | Rejected | It would add distributed storage and coordination scope unrelated to current embedded/server needs. |

## Scalability and flexibility risks

The abstraction path can add dispatch latency, allocations, audit volume, semaphore
contention, stream-lease leaks, startup cost, and catalog-validation cost. Extension
state can create unbounded buffers or per-key locks. Health endpoints can accidentally
turn into synchronous full-storage scans. Backup can report success from mixed
checkpoints during concurrent mutation.

Step 11D must measure the current baseline and then ratify budgets. The initial p95
gateway/capability overhead target of at most 5%, 512 concurrent flat-container
reads, 100 parallel embedded hosts, and catalog sizes of 8, 50, and 200 are
provisional test points, not universal performance claims. Overload must wait within
a deadline or produce typed `503` plus `Retry-After`. Streams must use constant
memory, enforce limits for every handle type, and release leases on EOF, disposal,
and cancellation. Hot read-path auditing should be sampled or aggregated unless full
auditing proves bounded overhead; privileged mutations remain fully audited.

Step 11D measured and fixed the applicable defects. On .NET 10.0.11/Windows x64
Release, the metadata dispatcher measured 10.06 microseconds p95 and 319 additional
bytes versus direct owner invocation; embedded composition measured 22.44 milliseconds
p95 and 0.94 MiB; 100 parallel compositions completed in 439 milliseconds with
307 KiB retained managed memory per host; 8/50/200-route catalogs measured
0.325/4.449/9.796 milliseconds p95; readiness with 1,000 inventory files measured
0.834 milliseconds p95; and full capability audit measured 0.204 microseconds and
91 bytes per call. The raw methodology and results are in
`design/baselines/microkernel-step-11d-windows.json`.

The original saturation path incremented an active-call counter and threw an unhandled
quota exception, which could become HTTP 500. Calls now use a bounded 64-entry queue,
wait at most 250 milliseconds, preserve cancellation, and return typed HTTP 503 with
`Retry-After: 1` on queue-full or deadline expiry. Stream, memory, and file handles
enforce declared bytes; leases release on EOF, disposal, exception, and abort. Health
readiness remains a bounded probe while explicit storage diagnostics retain full
inventory work. Full hot-read audit remains enabled because it met its budget; its
4,096-entry retention ring reports dropped-event counts.

Step 12A replaced the temporary state path with a quota-bounded transactional store,
restart-monotonic concurrency tokens, ordered migrations, fixed 64-stripe locking,
version 2 typed checkpoint participants, and journalled crash-safe restore. Required
state participates in the kernel checkpoint; external projections remain rebuildable.

With 11A through 11D complete, Lane A may begin at 12A, Lane B at Step 13, and Lane C
at Step 16. The product remains single-node/per-process with many isolated embedded
hosts; these results do not claim distributed or multi-node scalability.

## Revised sequencing

After Steps 11A through 11D:

- Lane A runs 12A transactional state/checkpoints, then 12B operations, health,
  backup, and restore.
- Lane B runs Step 13 flat container, mechanically separates shared registration and
  search code, then may run Steps 14 and 15 in parallel after URL projection. Steps 13
  and 15 are implemented: flat-container and symbol reads, search, their routes, and the
  `PackageBaseAddress` resource are owned by the official `NuGet.FlatContainer`
  and `NuGet.Search` extensions through the generic module seam; registration ownership
  is unchanged.
- Lane C runs Step 16 supply-chain policy.
- Step 17 package management waits for the read and policy lanes.
- Step 18 performs the physical official assembly split.
- Step 19 publishes no SDK until route, URL, rendering, capability, contract identity,
  support, signing, replacement, manifest, and target-framework decisions are made.
- Step 20 adds trusted in-process loading.
- Step 21 remains gated on a concrete consumer and transport-neutral parity.
- Step 22 depends on Step 20 and on Step 21 only when isolation is required.

### Step 13A implementation update

The Lane B split prerequisite separates registration and search contracts, endpoint
descriptors, package-query adapters, document builders/renderers, operation owners,
and focused tests. Both surfaces retain the existing `builtin.protocol` operation,
route, and service-resource owner; this step does not extract either feature.
Registration and search may depend on ownership-free package metadata primitives,
while architecture fitness tests reject dependencies on each other's implementation
namespace.

The split preserves kernel URL projection, authoritative visibility immediately before
reads, indexed read-your-writes behavior, body-free metadata routes, exact payload and
HEAD behavior, paging, ordering, filters, rich metadata, symbols, durable restart, and
parallel host isolation. It adds no public contract, persisted schema, capability,
loading, package policy, mutation, operations/state, or sidecar behavior. Reverting the
change restores the combined source layout without data or wire migration.

### Step 15 implementation update

Search is now contributed by the official `NuGet.Search` module. It is the single
owner of `NuGet.Search.Query`, the typed `/query` route, and both
`SearchQueryService` resources. The module consumes only the action-scoped,
transport-neutral `packages.search.query` capability and emits typed registration
route references; the resource declarations retain their registration and
flat-container dependencies for kernel URL projection.

The capability delegates to the existing synchronous indexed stores and reapplies the
authoritative search visibility decision before returning serializable metadata.
Consequently publish, unlist, quarantine, and delete changes remain immediately visible
without an asynchronous projection. Existing totals, paging, ordering, prerelease,
package-type, rich-metadata, HEAD, body-free, durable restart, parallel-host, and real
NuGet.Protocol behavior remain unchanged. No schema, registration owner, mutation,
policy, SDK/loading, or sidecar behavior changes.

## Step 12B implementation update

Lane A operational ownership now flows through the same generic module seam as a
separately compiled contribution. `NuTest.Operations` is the single owner of the
existing health, readiness, storage-health, diagnostics, backup, and restore operation
IDs and contributes all existing health routes through generated descriptors.

The former owner-shaped capability was split into query, checkpoint-export, and
checkpoint-restore capabilities. Their signatures use transport-neutral documents and
kernel-issued stream handles; they expose no execution context, storage manifest, store,
root path, ASP.NET type, or dependency-injection surface. The kernel still aggregates
health, resolves and bounds handles, validates version 2 participants and integrity, and
owns the atomic checkpoint/restore commit and recovery mutations.

## Step 16 implementation update

Lane C now extracts supply-chain policy participation into the internal official
`NuTest.SupplyChain` module. The module contributes authoritative signature, scanner,
ownership, namespace, and quota participants through the Step 11C generic seam.
Separate audited capabilities expose only signature inspection and scanning against
opaque, kernel-issued package handles. Ownership and quota facts remain
kernel-derived; extensions receive no authoritative mutation surface.

The kernel validates declared participants during profile resolution and validates
the completed active registry before readiness. Admission and validation use
deterministic all-must-allow aggregation with required participant IDs and minimum
counts. Missing, failed, timed-out, or abstaining authoritative participants fail
closed; caller cancellation still propagates. Lifecycle transitions, visibility,
moderation, recovery, transactions, and audit remain authoritative kernel behavior.
No Lane A state/checkpoint or Lane B protocol ownership changes are included.

## Open questions and deadlines

### Before Step 12A

- What state is authoritative, and how does it join SQLite metadata and file content
  at one checkpoint?
- What are the migration, concurrency-token, quota, buffering, streaming, and
  lock-cardinality semantics?

### Before Step 18

- What one-way assembly graph prevents kernel references to official
  implementations?
- How are typed capabilities injected across that graph?

### Before Step 19

- Is route/binding public in v1 or contributor-only?
- Answered in Step 11C: `OperationResult` replaces `OperationHttpResult` as one
  immutable, versioned, transport-neutral rendering contract. Remaining question: does
  the versioned contract need negotiation across SDK versions?
- What SDK version window is supported?
- Partially answered in Step 11C: structural contract identity is a canonical text plus
  a SHA-256 fingerprint with golden snapshots for operations, routes, resources, and
  capability candidates. Remaining question: which of those surfaces become public.
- What signing and publisher identity policy applies?
- What replacement scope is supportable, and does search alone justify replacement
  machinery?
- Are manifests JSON, code, or both?
- Which target frameworks are supported?
- Should v1 deliberately ship as in-process first?
- What are optional-extension startup failure semantics?

### Before sidecars

- Which RPC technology is used?
- How do stream-handle lifecycle, backpressure, expiry, and cancellation work?
- What authentication and sandboxing are required?
- Is transport local-only or remote-capable?
- How are errors, deadlines, URLs, and rendering kept identical to in-process
  execution?

## Recommendation

Approve the correction sequence and keep implementation issues after Step 11 paused
until Steps 11A through 11D pass. Update epic #50 and tracker issue descriptions only
when implementation proposals are approved; do not renumber or close existing
tracker issues as part of this documentation revision.
