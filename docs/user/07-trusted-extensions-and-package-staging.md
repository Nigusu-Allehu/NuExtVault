# 7. Trusted extensions and Package Staging administration

[User manual](README.md)

## Trust boundary

External discovery is disabled by default. Trusted extensions execute
administrator-approved code in process. Dedicated collectible assembly-load
contexts provide dependency and shutdown isolation, **not a security sandbox**.
Every configured package is required; validation or activation failure prevents
the server from listening. Install, update, enable, disable, and unload require a
restart, and routes and grants are frozen before listening.

An extension root contains top-level `.nupkg` files. Each package must include:

<!-- example-id: user-07-package-layout; evidence: reference -->
```text
Contoso.Extension.1.2.3.nupkg
├── Contoso.Extension.nuspec
├── extension-manifest.json
├── extension-package.json
├── extension-attestation.json
└── lib/net10.0/Contoso.Extension.dll
```

A strict trust root binds the publisher and ES256 public key:

<!-- example-id: user-07-trust-root; evidence: reference -->
```json
{
  "publisher": "Contoso",
  "keyId": "contoso-extension-signing-2026",
  "algorithm": "ES256",
  "subjectPublicKeyInfoBase64": "<base64 DER SubjectPublicKeyInfo>"
}
```

Missing trust, malformed paths, invalid or expired attestations, dependency or
collision failures, forbidden references, missing required grants, and activation
failures all fail closed. Repeat path/grant options; one argument is never split
on `;`, `:`, or `,`.

## Package Staging

`NuExtVault.PackageStaging` is independently packable but absent from default
profiles. A clean repository pack does not create production loading metadata or
a signed attestation; administrators must supply an already attested package and
trust root. No public signing/install service or published package exists.

It requires five grants:

<!-- example-id: user-07-staging-grants; evidence: reference -->
```text
host.clock.read
extension-state.read
extension-state.write
packages.content.write-staged
publication.request
```

The current manifest declares its signed predecessor identity,
`NuTest.PackageStaging`. On first startup against a pre-rename store, the kernel
upgrades extension-state ownership, staged-content ownership, and publication-journal
idempotency/recovery ownership to `NuExtVault.PackageStaging` before listening. A
completed migration retains no runtime alias. If both identities contain data,
startup fails rather than merging them; restore a coherent backup or select the
intended storage root.

The following is a non-executable reference because this repository cannot generate an
administrator's production signing key or installable attestation. The functional
suite executes the same argument shape with an ephemeral signed fixture.

<!-- example-id: user-07-staging-start; evidence: reference -->
```powershell
& "{{TOOL_COMMAND}}" start --port "{{PORT}}" --storage "{{STORAGE}}" `
  --extension-root "{{EXTENSION_ROOT}}" `
  --extension-trust-root "{{TRUST_ROOT}}" `
  --extension-grant host.clock.read `
  --extension-grant extension-state.read `
  --extension-grant extension-state.write `
  --extension-grant packages.content.write-staged `
  --extension-grant publication.request
```

The administrator-only routes are:

<!-- example-id: user-07-staging-routes; evidence: reference -->
```text
PUT  /staging/groups/{groupId}
GET  /staging/groups
GET  /staging/groups/{groupId}
PUT  /staging/groups/{groupId}/packages
PUT  /staging/groups/{groupId}/packages/{packageId}/{version}/symbols
GET  /staging/groups/{groupId}/packages/{packageId}/{version}
POST /staging/groups/{groupId}/packages/{packageId}/{version}/promote
POST /staging/groups/{groupId}/packages/{packageId}/{version}/reject
POST /staging/groups/{groupId}/expire
```

Group IDs are 1–64 ASCII letters, digits, `-`, or `.`. Group creation accepts
`maximumPackages` and `ttlMinutes`; values are bounded to 1–50 packages and
1–1,440 minutes. Listing defaults to 50 and is bounded to 200.

Uploads stream into kernel-owned staged storage. Package identity comes from the
archive, not the URL. Staged packages remain absent from search, registration,
and flat-container resources until promotion, then become immediately visible.
Upload and promotion accept `Idempotency-Key`. A retry cannot republish an
already resolved package; it returns the typed `AlreadyResolved` outcome with
`replayed: true`.

Reject releases staged content and records `Rejected`; expire releases remaining
content, marks the group `Expired`, and blocks later uploads. Neither deletes
group history. Backups include staging state, bytes, and publication journals.
Restore them with the same trusted package configuration, then supply all grants
again when starting.

The SDK (`1.3.0`) and TestKit (`1.1.0`) target `net10.0` and are locally packable,
not externally published. There is no network extension feed, hot reload,
sidecar, security sandbox, multi-node coordination, or optional degradation.

**Previous:** [Operations and recovery](06-operations-and-recovery.md)  
**Next:** [Troubleshooting, limits, and compatibility](08-troubleshooting-limits-and-compatibility.md)
