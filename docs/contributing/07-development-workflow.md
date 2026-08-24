# 7. Development workflow

[`AGENTS.md`](../../AGENTS.md) is mandatory. Use this workflow for every feature,
bug fix, protocol change, extension, capability, operation, contract, persistence,
or recovery change.

## 1. Classify and research

Inspect current owners, routes, profiles, public contracts, tests, and snapshots.
Identify whether the change is internal, observable NuGet/CLI behavior, an
official or external extension, ownership, capability, public contract, or
persistence/recovery work.

For a migration step, inspect current `main`, open migration pull requests, and
merged migration history. Select only the next dependency-ready step and name
its number and title in the proposal; never build an upper step on a stale or
unmerged lower-step branch.

Public extensions start from the separate [SDK
fixture](../../tests/NuGet.TestServer.SdkFixture/FlavorsExtension.cs), not kernel
internals. Protocol work starts from [compatibility
characterization](../../tests/NuGet.TestServer.FunctionalTests/ProtocolCompatibilityBaselineTests.cs).

Documentation-only corrections do not require new tests. They still require a
proposal and explicit approval when they change documented behavior or project
policy.

## 2. Propose and obtain explicit approval

Before editing production code or tests, provide:

1. intended and explicitly unchanged behavior;
2. public and internal APIs affected;
3. operation ownership before and after;
4. compatibility effects on URLs, payloads, statuses, headers, ordering, casing,
   paging, HEAD, CLI, and read-your-writes;
5. authority, access, capability, quota, cancellation, streaming, audit, and
   failure analysis;
6. contract/version and persisted-data implications;
7. unit, functional, conformance, package, and cross-platform evidence;
8. migration and rollback;
9. documentation to update last.

The original task is not approval. Wait for an explicit approval and revise the
proposal when requested.

## 3. Write tests first

Add the smallest isolated test and the externally observable functional test.
Run them before production edits and confirm they fail for the intended missing
behavior—not compilation, setup, timing, or an unrelated defect.

Choose evidence by change type:

- ownership and graph: [`OperationRegistryTests`](../../tests/NuGet.TestServer.UnitTests/OperationRegistryTests.cs);
- route coverage: [`OperationRouteCoverageTests`](../../tests/NuGet.TestServer.UnitTests/OperationRouteCoverageTests.cs);
- capability/security: [`CapabilityBrokerTests`](../../tests/NuGet.TestServer.UnitTests/CapabilityBrokerTests.cs);
- assembly boundaries: [`ExtensionModuleFitnessTests`](../../tests/NuGet.TestServer.UnitTests/ExtensionModuleFitnessTests.cs);
- external composition: [`ExtensionModuleConformanceTests`](../../tests/NuGet.TestServer.FunctionalTests/ExtensionModuleConformanceTests.cs);
- trusted loading: [`ExternalExtensionFunctionalTests`](../../tests/NuGet.TestServer.FunctionalTests/ExternalExtensionFunctionalTests.cs);
- SDK/API/package: [`NuGet.TestServer.Extensions.Sdk.Tests`](../../tests/NuGet.TestServer.Extensions.Sdk.Tests);
- persistence/recovery: state hardening and [`StorageBackupTests`](../../tests/NuGet.TestServer.UnitTests/StorageBackupTests.cs).

Never weaken valid compatibility, streaming, concurrency, integrity, or security
assertions to make an implementation pass.

## 4. Implement the smallest complete change

Keep exactly one owner for each operation. New public operations use stable,
extension-prefixed IDs, typed serializable contracts, typed descriptors,
explicit access/HEAD/body/limit/version declarations, and replacement disabled.
Ownership transfers characterize the old owner and switch atomically.

New capabilities are narrow, action-scoped, asynchronous, serializable,
cancellable, bounded, deny-by-default, and audited. If an official extension
needs an unavailable action, add a suitable public capability rather than a
private service, store, path, DI, ASP.NET, secret, or authority escape.
Requests declare `required` or `optional` explicitly. An ungranted required
capability fails validation before startup, while optional absence is handled
only through `TryGet`. Every handle remains scoped to its host and extension
identity.

Unless explicitly approved, preserve NuGet wire behavior, immediate
read-your-writes, exact download of unlisted versions, search exclusion for
unlisted versions, fail-closed visibility, bounded streams, and isolated
programmatic hosts.

## 5. Version contracts deliberately

Manifest, SDK API, operation, contribution, route, capability, and structural
versions are independent. Additive optional fields require a minor release and
defined defaults. Breaking semantics or required fields require a major
contract version. Review public API and structural snapshot changes; never
regenerate them blindly.

## 6. Prove rollback

Define rollback before implementation. A reverted owner must not create two
owners. Disabling external discovery must leave official profiles functional.
Persisted changes need old-data readability or an explicit migration, deterministic
interruption recovery, backup/restore evidence, and defined downgrade behavior.
Rollback must not bypass policy, expose forbidden package state, or discard
authority.

## 7. Validate, then document

Run the new targeted test, related suites, full solution validation, packaging
and real-client scenarios affected by the change, then three-OS CI. Chapter 8
contains the canonical commands.

Update documentation last. Describe implemented behavior, keep public/internal/
history distinctions, and compile examples through the documentation harness.

---

[Contributor manual](README.md) | **Previous:** [Public SDK and trusted loading](06-public-sdk-and-trusted-loading.md) | **Next:** [Build, test, and release](08-build-test-and-release.md)
