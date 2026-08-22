# Public Extension SDK v1

## Status and scope

This is the authoritative policy for the NuTestServer public extension SDK v1
surface stabilized by Microkernel Step 19, loaded by Step 20, and additively extended
to SDK `1.3.0` by Step 22. The implementation
and package projects exist in this repository, can be packed locally, and are
exercised by official and separately packaged test extensions. They have not been
published to NuGet.org or any other external feed.

Step 20 adds administrator-installed package discovery, trusted in-process loading,
validation, and activation through repeatable CLI package and trust roots. Discovery
is disabled by default and performs no network access. Sidecars remain deferred
until a concrete consumer requires process isolation or another implementation
language. Step 22 adds the independently packed `NuTest.PackageStaging` reference
extension and generic staged-content, atomic-publication, transactional-state,
route-header/body-binding, and state-manifest contracts. External publication remains
out of scope.

## Packages, assemblies, and target framework

The two public package and assembly identities are:

| Package and assembly | Version | Purpose |
| --- | --- | --- |
| `NuGet.TestServer.Extensions.Sdk` | `1.3.0` | Supported extension contracts, strict JSON manifest parser, schema, canonical identities, conformance checks, and attestation APIs |
| `NuGet.TestServer.Extensions.TestKit` | `1.1.0` | Typed manifest builder, capability fake, and conformance helper |

Both packages target only `net10.0`. This is intentional for v1: the server,
kernel, official extension assembly, CLI, functional tests, and independently
compiled fixtures all run on `net10.0`; multi-targeting would claim compatibility
that this repository does not build or test. The SDK package has no ASP.NET Core,
dependency-injection, database, NuGet.Protocol, storage, kernel, host, security, or
official-extension dependency. TestKit depends only on the SDK project.

The SDK package includes
`contentFiles/any/any/nutestserver/extension-manifest-v1.schema.json`. TestKit is a
separate assembly so its builders and fakes do not enlarge the runtime contract.

## Manifest and public contribution surface

`extension-manifest.json` is the deterministic declarative authority. Manifest
schema v1 requires:

- `$schema`, `schemaVersion`, extension `id`, `version`, and display `publisher`;
- an SDK compatibility range with `minimum` and `maximumExclusive`;
- independent manifest, operation, contribution, route, capability, and structural
  contract versions;
- complete arrays for operations, contributions, routes, and capability requests.

The parser rejects malformed JSON, comments, trailing commas, unknown members,
missing required members, unsupported schemas or SDK ranges, invalid versions,
duplicate identities, implicit capability requirements, and replacement requests.
It returns errors ordered by JSON path and error code. The JSON schema and strict
runtime parser are both part of the local SDK package.

`NuGet.TestServer.Extensions.TestKit.ManifestBuilder` is the typed authoring
equivalent. It deterministically orders operation, contribution, route, and
capability identities. A separately compiled reference project is available at
`tests\NuGet.TestServer.SdkFixture`; it packages its root
`extension-manifest.json`, implements `IExtensionModule`, binds
`/flavors/index.json`, registers a typed operation, and resolves a required
capability without referencing the kernel.

The supported v1 contributor points are typed module registration, new operation
ownership, typed route binding, and declared contributions. A contributor may own
only a newly introduced stable operation ID prefixed by its extension ID, for
example `Contoso.Flavors.GetIndex`. Exactly one active owner is permitted. Operation,
route, resource, contributor, and policy ordering is explicit and deterministic;
registration or filesystem discovery order is never a selector.

All existing built-in operations are nonreplaceable. Replacement is disabled for
every v1 extension operation, and the SDK exposes no override, takeover, or
replacement registration API. Authoritative identity, publication, moderation,
ownership, recovery, and package-management mutations are permanently
nonreplaceable in v1. A future major policy may consider other non-authoritative
operations, but v1 makes no such promise.

## Capability and boundary policy

Capabilities are denied by default and requested explicitly as `required` or
`optional`.

- Denial of a required capability fails validation or throws
  `CapabilityDeniedException`; it is never converted into success.
- Optional omission is observable only through `TryGet` on a request declared
  optional. `TryGet` rejects required requests.
- The kernel scopes capability handles to one host instance, extension identity,
  attested manifest digest, and immutable staged-content digest.
- Public capability methods are action-scoped, asynchronous `ValueTask` operations
  with cancellation and serializable contracts.
- Documents and stream handles declare positive bounds. Oversized or unbounded
  values are rejected.

The public SDK provides no `WebApplication`, ASP.NET request or routing object,
middleware hook, unrestricted `IServiceProvider`, database connection, store,
storage-root or filesystem path, raw secret, raw `Stream`,
`OperationExecutionContext`, package implementation model, backup implementation
manifest, kernel type, security implementation, rendering implementation, or
authority escape. Extensions cannot write authoritative package, publication,
moderation, ownership, identity, recovery, or package-management state directly.
Official extensions obey the same boundary.

## Version and contract negotiation

Manifest schema, SDK API, operation contract, contribution contract, route contract,
capability contract, and structural contract identities are independent typed
surfaces. Sidecar RPC would be an additional independent surface, but no sidecar
contract exists in Step 19.

The host-supported SDK range today is `1.0.0` through `1.3.0`, inclusive, and only
within major version 1. An SDK below `1.0.0`, above `1.3.0`, or in another major is
unsupported. A manifest range must include a host-supported selection, and every
declared contract version and structural identity must match that selection.
Unknown required fields, unsupported ranges, missing identities, or any version or
digest mismatch fail closed before activation. Step 20 performs this complete
negotiation before loading code.

Once these packages are published for the first time, a supported contract receives
at least 12 months of support and remains supported for at least two subsequent
minor releases. Those clocks do not begin merely because Step 19 can pack the
projects locally. Additive optional fields require a minor release. Breaking
semantic or required-field changes require a new major version.

## Canonical bytes and structural identity

The SDK owns the only canonical byte definitions used for manifest digests,
structural fingerprints, and attestation payloads:

- canonical JSON is unindented UTF-8 with fixed property order and no trailing
  newline;
- identity-bearing arrays and route methods use ordinal deterministic ordering;
- manifest canonicalization is idempotent;
- structural contract v1 is frozen as reviewed canonical UTF-8 text;
- SHA-256 identities are 64-character lowercase hexadecimal.

`CanonicalContractBytes`, `ExtensionManifestJson`, `StructuralContractFingerprint`,
and `ConformanceAttestation.CanonicalPayloadBytes` are the corresponding APIs.
Golden canonical bytes and SHA-256 files in
`tests\NuGet.TestServer.Extensions.Sdk.Tests` freeze these definitions. The manifest
digest, SDK structural fingerprint, and signed attestation therefore cannot choose
different ad hoc serialization rules.

## Publisher trust and conformance attestation

The authoritative production identity is the verified publisher signing key plus
the extension ID. The manifest's publisher display string is not authority.
Step 20 must derive the expected extension and package identity from validated
package metadata and the manifest, then resolve an explicit host trust root for that
publisher and key.

V1 attestations use only `ES256` with ECDSA P-256 and SHA-256. The envelope carries
an explicit algorithm and key ID. A host trust root binds publisher, key ID,
algorithm, and public key. No implicit or ambient trust root is allowed; a missing
root fails closed. Trust roots, keys, and clocks are injectable so tests can use
ephemeral fixtures. No signing key or production secret belongs in this repository.

The signed canonical payload binds:

- package ID and package version;
- publisher identity;
- canonical manifest SHA-256;
- selected SDK version;
- manifest, operation, contribution, route, capability, and structural contract
  versions;
- the SDK structural SHA-256;
- conformance suite identity;
- envelope issue and expiry times, algorithm, and key ID through the signing input.

V1 carries one reviewed aggregate SDK structural digest together with every
independent contract version; it does not claim a separate digest for each contract
surface.

Verification rejects payload or signature tampering, wrong package identity or
version, publisher mismatch, wrong manifest or structural digest, every independent
contract-version mismatch, wrong suite, wrong key or key ID, unsupported algorithm,
not-yet-valid or expired envelopes, noncanonical payloads, and absent trust. Step 19 provides signing and verification primitives and fixtures; Step 20 invokes
verification against explicit configured trust roots before activation.

## Dependencies, startup, and failure behavior

The current built-in catalog already orders dependencies deterministically and
rejects missing dependencies, incompatible half-open version ranges, and cycles.
The public manifest v1 does not expose package dependencies. Step 20 keeps loading
metadata in the separately versioned `extension-package.json`, where every dependency
has an explicit half-open version range. Deterministic graph validation rejects
missing, incompatible, duplicate, or cyclic packages before code loading.

A required request-path extension startup failure prevents readiness. Invalid
identity, ownership, route, contract, capability, trust, or attestation state fails
before listening. V1 does not promise optional-extension startup degradation,
resource omission, or a `Degraded` recovery lifecycle. Installation, update,
enablement, disablement, and unload require restart.

## Local build, pack, and migration

Pack the stabilized local artifacts from the repository root:

```powershell
dotnet pack src\NuGet.TestServer.Extensions.Sdk\NuGet.TestServer.Extensions.Sdk.csproj --configuration Release -p:TreatWarningsAsErrors=true --output artifacts\sdk
dotnet pack src\NuGet.TestServer.Extensions.TestKit\NuGet.TestServer.Extensions.TestKit.csproj --configuration Release -p:TreatWarningsAsErrors=true --output artifacts\sdk
dotnet pack tests\NuGet.TestServer.SdkFixture\NuGet.TestServer.SdkFixture.csproj --configuration Release -p:TreatWarningsAsErrors=true --output artifacts\sdk
```

These commands create local packages; they do not publish them. A deployable
extension package additionally needs Step 20 loading metadata and a signed
attestation, and is loaded only when the host is started with explicit extension and
trust roots.

Pre-Step-19 extension projects should:

1. replace references to
   `NuGet.TestServer.Extensions.Abstractions` with
   `NuGet.TestServer.Extensions.Sdk`;
2. target `net10.0` and update namespaces to
   `NuGet.TestServer.Extensions.Sdk`;
3. add a strict schema-v1 `extension-manifest.json`, using the packaged schema or
   `tests\NuGet.TestServer.SdkFixture` as the reference template;
4. declare independent contract versions, bounded routes, and explicit required or
   optional capabilities;
5. use TestKit's `ManifestBuilder`, fakes, and `ConformanceCheck` in tests;
6. define only stable, extension-namespaced new operation IDs and remove any
   replacement or implementation escape;
7. freeze canonical manifest/structural identities and verify an ES256 attestation
   with fixture trust roots.

Rollback before external publication is to stop producing the local SDK/TestKit
packages and restore the pre-Step-19 internal abstractions name. No runtime
discovery, persistent data, wire protocol, or deployed external package needs
migration because Step 19 introduced none.
