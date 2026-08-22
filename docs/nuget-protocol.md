# NuGet protocol support

## Supported operations

The current implementation supports:

- V3 service-index discovery
- Package Base Address / flat-container downloads
- Base64 SHA-512 sidecars for package archives
- Registration indexes, pages, and leaf metadata
- Rich registration and search metadata from package archives and test state
- Package search with stable pagination totals and complete listed-version metadata
- Package and symbol-package push through standard NuGet clients
- Quarantine-first signature/scanner validation and durable moderation
- Published-only package, rich-metadata, search, registration, and symbol visibility
- Immutable/idempotent publication, ownership, namespaces, and quotas
- Package unlisting
- Package seeding and hard deletion through the control API
- Request recording
- Fixed-status and fixed-delay fault rules
- API-key-protected publishing and control operations
- Basic authentication for private sources
- Combined Basic and API-key authentication
- Persistent CLI package storage
- Embedded and cached nuget.org vulnerability data
- Local restore auditing and registration vulnerability metadata

CLI package state is persisted in the Local AppData storage directory. Servers
created with `NuGetTestServerHost.StartAsync()` remain isolated and in memory.
Fault rules and request history are always runtime-only.

## V3 capability matrix

The service index advertises only resources implemented by the server.

| Capability | Service-index type or route | Status | Client coverage |
| --- | --- | --- | --- |
| Service discovery | `GET/HEAD /v3/index.json` | Implemented | NuGet.Protocol and `dotnet` |
| Package versions, archives, nuspecs | `PackageBaseAddress/3.0.0` | Implemented | NuGet.Protocol and `dotnet restore` |
| Package hashes | `{id}.{version}.nupkg.sha512` | Implemented as Base64 SHA-512 of the exact archive | Raw Kestrel `GET`/`HEAD` |
| Registration indexes, pages, and leaves | `RegistrationsBaseUrl/3.6.0` | Implemented | NuGet.Protocol |
| Search | `SearchQueryService/3.0.0-beta` and `/3.5.0` | Implemented | NuGet.Protocol |
| Package publishing and unlisting | `PackagePublish/2.0.0` | Implemented | `dotnet nuget push` |
| Symbol-package publishing | `SymbolPackagePublish/4.9.0` | Implemented; `.snupkg` files are validated and persisted separately | `dotnet nuget push` automatic symbol upload |
| Vulnerability data | `VulnerabilityInfo/6.7.0` | Implemented | NuGet restore audit |
| Symbol download | No general NuGet V3 resource exists | Deferred and not advertised | N/A |
| Repository signatures | `RepositorySignatures/4.7.0` and later | Deferred and not advertised | N/A |

Registration metadata includes authors, owners, title, description, summary,
tags, project URL, embedded readme and icon paths, license expression/file/URL,
package types, repository details, dependencies, publication/listing state,
download count, deprecation reasons/message/alternate package, and
vulnerabilities. Search projects applicable fields plus per-version and total
download counts and verification state. Tests can set repository-owned metadata
without rewriting an archive; see
[repository-owned metadata](packages.md#repository-owned-metadata).

Repository signatures are intentionally deferred. A correct
`RepositorySignatures` resource requires HTTPS, X.509 signing certificates,
repository-signing every claimed package, and trust metadata matching those
actual signatures. This loopback test server can validate author/repository-signed
package content but has no repository-signing-key pipeline, so advertising an empty
or synthetic repository-signature resource would misrepresent package trust.

## Vulnerability auditing

No vulnerability-specific setup is required. Every server advertises
`VulnerabilityInfo/6.7.0` and serves its index and pages from local URLs:

```text
GET/HEAD /v3/vulnerabilities/index.json
GET/HEAD /v3/vulnerabilities/{snapshot-id}/{page-name}.json
```

The tool package contains a validated nuget.org baseline, so first startup and
NuGet audit work without network access. Registration metadata also includes
matching advisories for hosted package IDs and versions, enabling Package
Manager UI vulnerability details.

To select the local source explicitly for restore auditing, add it as an audit
source:

```xml
<auditSources>
  <clear />
  <add
    key="TestServer"
    value="http://127.0.0.1:54321/v3/index.json"
    allowInsecureConnections="true" />
</auditSources>
```

### Snapshot storage and refresh

CLI servers prefer a newer valid snapshot persisted through the kernel's
owner-namespaced transactional state store under:

```text
<storage>\extension-state
```

The vulnerability snapshot is optional, rebuildable state: the server starts
without it, adopts a legacy cache when one exists, otherwise serves the embedded
baseline and refreshes in the background. On first startup after upgrade, the
kernel provides the extension a bounded, owner-scoped logical view of the existing
`<storage>\vulnerabilities` cache without exposing a storage path. The extension
validates the legacy hashes and schema, selects the newest valid snapshot, and
writes it atomically to owner state. Invalid legacy entries are ignored; when none
are usable, the embedded baseline remains available and health reports degraded
with a warning.

After the server is ready, a stale snapshot is refreshed from nuget.org in the
background. A refresh downloads every referenced page with bounded timeouts and
sizes, validates the complete snapshot, persists integrity-protected state
atomically, and only then activates it. Failed or partial refreshes produce a
warning, report degraded-but-ready health, and leave the previous snapshot
available.

Programmatic servers created with `NuGetTestServerHost.StartAsync()` always use
the embedded snapshot and never refresh from the network, preserving
deterministic test behavior.

The durability guarantees of the underlying state store are described in
[architecture](architecture.md#transactional-extension-state).
