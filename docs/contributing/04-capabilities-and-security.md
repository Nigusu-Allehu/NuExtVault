# 4. Capabilities and security

Capabilities authorize narrow actions; they do not transfer ownership of a
subsystem. Extensions never receive a database connection, storage root,
unrestricted service provider, raw secret, `HttpContext`, raw `Stream`, or a
direct write path to package, publication, moderation, ownership, or recovery
state.

## Requests and grants

A manifest requests each capability as `required` or `optional`; a profile grants
capability names separately. Required-but-ungranted requests fail composition.
Optional absence is observable only through `TryGet`; `GetRequired` fails with
`CapabilityDeniedException`. The [catalog](../../src/NuExtVault.Kernel/Hosting/ExtensionCatalog.cs)
and [capability tests](../../tests/NuExtVault.UnitTests/CapabilityBrokerTests.cs)
cover denial and isolation.

Handles are scoped to one host and extension. Trusted packages are additionally
bound to their validated manifest and staged-content digests. Interface/name
mismatches are denied even when the textual name is granted.

The currently broker-backed public capability interfaces are:

<!-- example-id: contrib-04-public-capabilities; evidence: reference -->
```text
IHostClockCapability
ITransactionalStateCapability
IStagedContentWriteCapability
IAtomicPackagePublicationCapability
```

`IOutboundHttpCapability` is exported, but the current runtime maps outbound HTTP
through an internal kernel interface. An exported contract is not proof that a
public module can acquire a runtime handle; rely on broker mappings and
conformance tests.

## Limits, cancellation, streams, and audit

Every broker call crosses a concurrency/queue gate and receives the request
cancellation token. Current defaults allow 64 active and 64 queued calls, with a
250 ms queue deadline. The broker type's standalone stream default is 256 MiB;
normal server composition overrides it with the larger configured HTTP-request
or compressed-package limit, currently 128 MiB. Configuration can provide other
validated values.

Streams use opaque handles, remain bounded and cancellable, and release leases on
EOF, disposal, exception, or cancellation. Documents declare positive bounds.
Saturation returns a typed unavailable outcome rather than bypassing policy.

The bounded capability audit records host, extension owner, operation,
capability, action, and outcome. Its 4,096-entry retention ring reports dropped
entries. This audit is distinct from production authentication/authorization
auditing. See [`CapabilityBroker`](../../src/NuExtVault.Kernel/Kernel/Capabilities/CapabilityBroker.cs)
and its [unit tests](../../tests/NuExtVault.UnitTests/CapabilityBrokerTests.cs).

## Authority and package visibility

A capability grant does not grant visibility. The kernel reapplies authoritative
resource-class visibility immediately before package data crosses the boundary:

- listed packages appear in exact content, versions, registration, search, and
  symbols;
- unlisted packages remain available by exact identity, symbols, and
  registration/version resources, but not search;
- quarantined, deleted, and unknown states receive no public grant.

[`PackageVisibilityPolicy`](../../src/NuExtVault.Kernel/Packages/PackageVisibilityPolicy.cs)
and its [complete matrix](../../tests/NuExtVault.UnitTests/PackageVisibilityPolicyTests.cs)
enforce fail-closed handling.

## Production restrictions

Embedded profiles reject network, secret, and sidecar grants. Production rejects
fault injection, request recording, test package control, instrumentation control,
secret resolution, and sidecar execution even if configuration requests them.
Production transport, identity, namespace ownership, and mutation scopes remain
kernel-owned. Capability/profile restrictions are covered by
[`CapabilityBrokerTests`](../../tests/NuExtVault.UnitTests/CapabilityBrokerTests.cs);
transport and identity behavior is covered by [production security functional
tests](../../tests/NuExtVault.FunctionalTests/ProductionSecurityTests.cs).

Invalid trust, attestation, ownership, routes, contracts, required grants, or
authoritative policy participants prevent startup. Request-time policy failures
never become success-shaped fallbacks.

## Trust caveat

The loader rejects traversal and forbidden host/kernel/official references, but a
trusted in-process assembly runs under the server operating-system identity.
Assembly-load contexts are not a sandbox. Install only administrator-approved
packages. Sidecars and untrusted-code isolation are not implemented.

---

[Contributor manual](README.md) | **Previous:** [Extension composition](03-extension-composition.md) | **Next:** [State, backup, and recovery](05-state-backup-and-recovery.md)
