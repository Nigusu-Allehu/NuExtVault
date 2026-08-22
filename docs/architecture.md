# Architecture

## Repository layout

```text
src/
  NuGet.TestServer.Extensions.Sdk/           Public extension contracts and schema
  NuGet.TestServer.Extensions.TestKit/       Builders, fakes, conformance helpers
  NuGet.TestServer.Kernel/                   Kernel and runtime: hosting, routing,
                                             security, capabilities, storage, state
  NuGet.TestServer.Extensions.Official/      Official feature extensions
  NuGet.TestServer.Extensions.PackageStaging/ Optional package staging extension
  NuGet.TestServer/                          Composition root and in-process API
  NuGet.TestServer.Cli/                      Command-line .NET tool

tests/
  NuGet.TestServer.Extensions.Sdk.Tests/
  NuGet.TestServer.SdkFixture/               Separately compiled public SDK fixture
  NuGet.TestServer.UnitTests/
  NuGet.TestServer.FunctionalTests/
  NuGet.TestServer.RouteFixture/             Separately compiled conformance module
```

The assembly dependency graph is one-way. The SDK contracts assembly depends on
nothing else, the kernel and the official extensions each depend only on the
contracts, and `NuGet.TestServer` is the only assembly that references both. It
selects the official extension bundle per host; the kernel has no compile-time
knowledge of it. Programmatic consumers keep referencing
`src\NuGet.TestServer\NuGet.TestServer.csproj`, and the CLI tool package ships all
four assemblies.

## Internal capability broker

Each server host creates its own internal capability broker. Built-in operation
owners receive only the actions declared by their resolved profile, such as bounded
package-content reads, publication mutations, moderation decisions, backup
invocation, or test-instrumentation configuration. They do not receive the root
service provider, database connections, storage-root paths, unrestricted network
access, or raw secrets.

Required capabilities are validated before the server listens. Optional capabilities
that are not granted are omitted and reported in startup diagnostics. Privileged
calls are scoped and audited by host instance, operation owner, and operation ID;
embedded hosts deny outbound HTTP, secret references, and sidecars, while production
profiles deny test instrumentation. This is an internal migration boundary and does
not change the public hosting API or NuGet routes.

## Internal service-index composition

`NuGet.ServiceIndex.Get` is owned by the internal official service-index feature.
Selected built-in resource owners contribute typed discovery metadata: resource type
and version, owning operation and route, access and readiness requirements, linked
resource requirements, URL-production links, stable comments, and projection order.
The kernel validates those contributions before listening and rejects ownership,
version, route, link, access, or readiness conflicts.

Resource owners never receive `HttpContext` and cannot mutate service-index JSON.
The kernel generates absolute URLs from the validated request origin after transport
security and trusted-proxy handling, projects only the supported typed fields, and
preserves the existing resource order and compatibility aliases. Adding another
internal resource consists of registering its typed contribution with its owner; the
service-index operation itself does not change.

Ownership of the built-in surface is divided as follows:

| Official extension | Owns |
| --- | --- |
| `NuGet.Vulnerabilities` | Vulnerability index/page operations and refresh lifecycle. |
| `NuGet.FlatContainer` | Flat-container version, content, nuspec, hash, and symbol reads; contributes `PackageBaseAddress`. |
| `NuGet.Registration` | Registration index, page, and leaf reads; contributes `RegistrationsBaseUrl` and accepts bounded, typed metadata contributions under exclusive namespaces. |
| `NuGet.Search` | `NuGet.Search.Query`, its body-free `GET`/`HEAD /query` route, and both advertised `SearchQueryService` resources through the bounded `packages.search.query` capability. |
| `NuGet.PackageManagement` | Push, symbol push, list, unlist, relist, and delete workflows through nonreplaceable action-scoped capabilities. |
| `NuTest.Operations` | Liveness, readiness, storage-health, diagnostics, backup, and restore operations through narrow kernel capabilities. |
| `NuTest.Control` | Authenticated package, fault, request-history, and reset control operations. |

Gateway authorization and all authoritative package mutations, policy, visibility,
recovery, and audit remain kernel-owned. Health aggregation, integrity, and atomic
checkpoint/restore authority also remain in the kernel.

## Transactional extension state

Extension state records carry a monotonic concurrency token that survives restart, a
schema name and version, and an integrity hash. A durable write is all-or-nothing:
every record and the owner descriptor are staged outside the authoritative tree and a
single commit journal publishes them together, so an interrupted or cancelled
write leaves the previous state complete and a crash after the commit point is
rolled forward to the complete batch. Roll-forward includes the version 1 mirror
that batch projects, so a downgrade immediately after a crash reads the recovered
value rather than the value the interrupted batch replaced.

Opening the store is bounded by participant descriptors and record headers: a record
payload is read when that record is read, or once when a migration has to rewrite it,
so start-up cost does not grow with the size of the persisted state. The store keeps
a version 1 mirror of every record so an earlier server build reads the same state,
and adopts version 1 records written before the transactional layout on first open.
State that predates the transactional layout has no persisted schema version, so it
is adopted at schema version 1 and travels through the complete migration path
instead of being declared current.

Adoption obeys the same key, record, and owner quotas a write obeys: a version 1
record that cannot fit them is refused with an explicit quota error instead of being
loaded. Adoption is one all-or-nothing admission per owner, so the aggregate record
count, owner bytes, and owner count the resulting owner would hold are validated
before the first record is persisted and a refusal leaves nothing behind. Opening the
store also validates the committed tree it loads against those same quotas, so an
over-quota or unreadable tree that a restore or an operator left behind fails the
open on every attempt rather than becoming the baseline a later write extends.
Version 1 state of an extension this build does not register is left untouched rather
than treated as a stale mirror.

Extensions whose state cannot be rebuilt declare it required, and the store, restore,
and backup validation each refuse to activate a set that is missing required state.
When a schema migration runs, persisted state owned by an extension this build does
not activate is moved to `<storage>\extension-state\quarantine` unchanged rather than
deleted, so it can be restored once that extension is active again.

## Published designs

The [`design/`](../design/README.md) directory contains the published design
documents behind this architecture, including
[`design/public-extension-sdk-v1.md`](../design/public-extension-sdk-v1.md) and the
microkernel migration plan.
