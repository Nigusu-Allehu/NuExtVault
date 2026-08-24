# 6. Public SDK and trusted loading

The supported public extension surface is locally packable and `net10.0` only.

<!-- example-id: contrib-06-version-table; evidence: reference -->
```text
NuGet.TestServer.Extensions.Sdk 1.3.0 net10.0
NuGet.TestServer.Extensions.TestKit 1.1.0 net10.0
NuTest.PackageStaging 1.0.0 net10.0
```

The SDK contains runtime contracts, strict manifest parsing/schema, canonical
identities, conformance, and attestation APIs. TestKit depends only on the SDK
and provides `ManifestBuilder`, `FakeHostClock`, and `ConformanceCheck`. Package
Staging is an optional reference extension and depends only on the SDK.

## Manifests, versions, and fingerprints

`extension-manifest.json` is the declarative authority. It contains identity,
SDK compatibility, independent manifest/operation/contribution/route/capability/
structural versions, operations, contributions, routes, capabilities, and
optional state. The strict parser rejects malformed JSON, unknown or missing
members, invalid or duplicate identities, unsupported ranges, implicit
capability requirements, and replacement requests.

The manifest schema, SDK API, operation, contribution, route, capability, and
structural contracts evolve independently. The host currently accepts SDK
1.0.0 through 1.3.0 within major version 1. Version compatibility is necessary
but not sufficient: the reviewed structural SHA-256 must also match.

Canonical bytes are unindented UTF-8 with fixed property order, ordinal identity
ordering, no trailing newline, and lowercase SHA-256. Always canonicalize through
the SDK. Golden fixtures live in
[`NuGet.TestServer.Extensions.Sdk.Tests`](../../tests/NuGet.TestServer.Extensions.Sdk.Tests).

TestKit's `ManifestBuilder` currently does not author the optional state
declaration. Stateful examples must validate their authoritative JSON directly.

## A compiled SDK/TestKit example

This complete program is compiled against the real SDK, TestKit, and separately
compiled Flavors fixture, then executes conformance.

<!-- example-id: contrib-06-sdk-example; evidence: executable -->
```csharp
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Extensions.TestKit;
using NuGet.TestServer.SdkFixture;

var manifest = new ManifestBuilder()
    .WithIdentity("Contoso.Flavors", "1.2.3", "Contoso")
    .TargetSdk(
        new SdkContractVersion(1, 3, 0),
        new SdkContractVersion(2, 0, 0))
    .RequireCapability("host.clock.read")
    .Build();

if (manifest.Identity.Id != "Contoso.Flavors")
{
    throw new InvalidOperationException("Unexpected manifest identity.");
}

var conformance = ConformanceCheck.Validate(typeof(FlavorsExtension).Assembly);
if (!conformance.IsValid)
{
    throw new InvalidOperationException(
        string.Join(Environment.NewLine, conformance.Errors));
}
```

Conformance validates module presence and operation ownership. It does not
replace strict manifest, package, trust, loader, or real-host tests.

## Attestation and trusted loading

V1 attestations use ES256 (ECDSA P-256/SHA-256). The signed canonical payload
binds package identity/version, publisher, manifest digest, selected SDK and all
contract versions, structural digest, suite, validity interval, algorithm, and
key ID. Authority comes from an explicit trust root, not the manifest's display
publisher. Tests use ephemeral keys; never commit a signing key.

Discovery is disabled without explicit local roots and performs no network
access. The loader validates bounded package extraction, canonical paths,
ordinal-exact NuGet package ID/version against manifest identity, metadata, SDK
compatibility, trust, attestation, assembly references, and dependency order.
It retains bounded entry-assembly, private-dependency, and PDB bytes and computes
a closure digest over those exact verified bytes.

Validated packages use dedicated collectible `AssemblyLoadContext` instances
and the host SDK identity. Activation loads the retained verified byte buffers,
not reopened mutable staging paths, then binds package/manifest/module identity,
manifest digest, closure digest, and staged-content identity. Post-validation
path mutation therefore cannot change activated code. Collection after host
disposal is tested, but this is cleanup and dependency isolation—not hot reload,
dynamic enablement, or a security sandbox. Every configured package is required;
failure prevents startup. Configuration changes require restart. See
[`ExternalExtensions`](../../src/NuGet.TestServer/Hosting/ExternalExtensions.cs)
and [loader tests](../../tests/NuGet.TestServer.UnitTests/ExternalExtensionPackageLoaderTests.cs).

## Package Staging

`NuTest.PackageStaging` declares required state, nine new nonreplaceable
operations, administrator-only bounded routes, streaming uploads, a
service-resource contribution, and five required capabilities. It is absent from
all default profiles and must be installed with explicit extension, trust, and
grant configuration.

The extension receives only public handles. The kernel owns bytes, package
identity validation, leases, quotas, publication policy, visibility,
idempotency, and recovery. Functional tests prove default absence, authentication,
uploads, pre-promotion invisibility, immediate post-promotion visibility,
replay, quotas, concurrency, and restart. Focused unit and storage-backup tests
prove staged bytes, extension state, and the publication journal participate in
backup and restore.

## Not currently applicable

Package Staging did not require another language or process isolation, so the
sidecar entry condition remains unmet. There is no sidecar RPC or supervision
runtime. SDK, TestKit, and reference extension packages are locally packable but
not externally published; no feed discovery, automatic update, or production
signing service is claimed.

---

[Contributor manual](README.md) | **Previous:** [State, backup, and recovery](05-state-backup-and-recovery.md) | **Next:** [Development workflow](07-development-workflow.md)
