# Configuration and storage

## Package resource limits

Package uploads are streamed through bounded temporary files and validated before
they become visible. Package downloads are streamed from the active package
content. The defaults are:

| Limit | CLI option | Default |
| --- | --- | ---: |
| HTTP request body | `--max-request-bytes` | 128 MiB |
| Compressed package | `--max-package-bytes` | 100 MiB |
| Archive entries | `--max-archive-entries` | 10,000 |
| One expanded archive entry | `--max-entry-bytes` | 64 MiB |
| Total expanded archive content | `--max-expanded-bytes` | 512 MiB |

For example:

```powershell
nuget-test-server start `
  --max-request-bytes 67108864 `
  --max-package-bytes 52428800 `
  --max-archive-entries 5000 `
  --max-entry-bytes 16777216 `
  --max-expanded-bytes 268435456
```

Malformed packages return `400 Bad Request`. Request, package, entry-count,
entry-size, and expanded-size violations return `413 Payload Too Large`.
Canceled and rejected uploads remove their partial temporary files.

The same limits are available to in-process servers through
[`PackageTransferLimits`](api/hosting.md#packagetransferlimits).

## Storage layout

CLI packages persist transactionally across restarts under:

```text
%LOCALAPPDATA%\nuget-test-server
```

On non-Windows systems, the path is based on .NET's
`Environment.SpecialFolder.LocalApplicationData`. Override it for CI or isolated
development:

```powershell
nuget-test-server start --storage .\.nuget-test-server
```

Package bodies remain streamed, file-backed blobs under `<storage>\packages`.
SQLite metadata is stored in `<storage>\packages.db` with an explicit,
automatically migrated schema version. Push, listing changes, deletion, and
metadata publication are coordinated so interrupted operations are recovered on
the next startup; control-API resets use the same recoverable deletion protocol.
Existing filesystem-only package layouts are imported in place, including
`.unlisted` markers.

Persistent exact lookup, registration enumeration, and stable paged search run
against normalized, indexed SQLite metadata. The schema stores normalized package
identity, semantic version ordering, listing and prerelease state, package types,
repository metadata, content hashes, and a trigram full-text projection of package
ID, description, and tags. Search count, page selection, and version metadata share
one read transaction, so `totalHits` and the returned page represent one snapshot
while packages are being published. Metadata-only requests do not open `.nupkg`
bodies; package content is opened only for downloads, one-time import of an
untracked filesystem package, or a legacy hash migration. Downloads verify the
recorded SHA-256 first.

Only one server process may use a storage root at a time; a second process exits
with a clear diagnostic. Startup validates that every tracked blob exists with
the recorded length without reading package content, removes interrupted
temporary publications, and recovers complete untracked blobs by validating and
hashing them once. Programmatic servers created without a storage path continue
to use the isolated in-memory implementation and the same `IPackageStore`
semantics.

### Indexed storage performance targets

The deterministic Release-mode regression corpus contains 200 package versions
with 16 KiB bodies. CI enforces these budgets on that corpus:

| Area | Target |
| --- | ---: |
| Restart startup | under 5 seconds |
| Restart allocations on the startup thread | under 12 MiB |
| 100 indexed search page queries | under 5 seconds |
| Concurrent consistency | 8 readers during 1 writer, with stable ordered pages |

These are regression budgets for a local test server rather than production
service-level guarantees. Package body size does not affect metadata-only startup
or query allocations.

## Runtime request and fault state

Request history retains the newest 10,000 requests by sequence, and a server
accepts at most 100 fault rules by default. Old request records are evicted
deterministically; adding a fault rule at capacity returns HTTP 409.

Override the CLI defaults through standard ASP.NET Core configuration:

```powershell
$env:RuntimeState__RequestHistoryCapacity = "2000"
$env:RuntimeState__FaultRuleCapacity = "25"
nuget-test-server start
```

Configure an in-process server directly:

```csharp
await using var server = await NuGetTestServerHost.StartAsync(
    new RuntimeStateConfiguration(
        requestHistoryCapacity: 2000,
        faultRuleCapacity: 25));
```

`GET /__test/state` reports `requestCount`, `requestCapacity`,
`evictedRequestCount`, `faultCount`, and `faultCapacity`. Resetting the server
or deleting request history clears retained requests and the eviction count;
the reset request itself is not retained.

`Authorization`, `Proxy-Authorization`, `Cookie`, `Set-Cookie`,
`X-NuGet-ApiKey`, other API-key spellings, and names configured under
`RuntimeState:SensitiveHeaders` are replaced with `[REDACTED]` before bounded
request metadata is retained. Request history never stores bodies.

## Production-safe mode

Production-safe mode removes the test control surface while retaining the NuGet
protocol endpoints. The legacy single-key form remains available for local
loopback use:

```powershell
$env:NUGET_TEST_SERVER_API_KEY = "<secret>"
nuget-test-server start --production --api-key-env NUGET_TEST_SERVER_API_KEY
```

`GET /__test/health` remains available for compatibility and reports
`"mode":"production"`.
Other `/__test` routes are not mapped, including state and package controls,
reset, hard deletion, request inspection, and fault injection. Test mode remains
the default and retains all existing test controls.

Production mode refuses anonymous write configuration. It also refuses cleartext
HTTP on non-loopback listeners. The CLI binds to loopback by default, where HTTP
is appropriate for a local tool and an API key or Basic credentials protect
writes. `--url` can select another listener; a non-loopback production listener
must use HTTPS with a Kestrel certificate.

### Scoped production identities

For remote production use, configure scoped identities through an environment
configuration provider. Do not put the JSON or its credentials directly on the
command line:

```powershell
$env:NUGET_TEST_SERVER_IDENTITIES = @'
{
  "identities": [
    {
      "name": "contoso-publisher",
      "apiKeys": ["current-key", "previous-key-during-rotation"],
      "passwords": [],
      "scopes": ["read", "publish", "unlist"],
      "namespaces": ["Contoso."]
    },
    {
      "name": "feed-admin",
      "apiKeys": ["admin-key"],
      "passwords": [],
      "scopes": ["admin"],
      "namespaces": ["*"]
    }
  ]
}
'@

nuget-test-server start --production `
  --identity-config-env NUGET_TEST_SERVER_IDENTITIES `
  --trusted-proxy 127.0.0.1
```

`--identity-config-stdin` is also supported. `--identity-config` emits the same
process-listing warning as other literal secret options. Production identity
configuration cannot be combined with the legacy username, password, or API-key
options.

Each identity may have multiple API keys and Basic-auth passwords so credentials
can overlap during rotation. Secrets are immediately converted to individually
salted PBKDF2-SHA256 digests and are never retained in clear text by the runtime.
Identity names and credentials must be unique.

The available scopes are `read`, `publish`, `unlist`, `delete`, and `admin`.
`admin` grants every operation and namespace. A publisher must also match a
configured package ID prefix. The first successful publisher claims ownership of
the package ID; later versions, unlisting, and hard deletion are restricted to
that owner or an administrator. Ownership and moderation history are persisted in
`<storage>\supply-chain.db`, while package moderation state is also stored with
the first-class package metadata.
Hard deletion is available only with production identities at
`DELETE /package/{id}/{version}/hard`.

### Transport requirements

Production identities require end-to-end HTTPS or an explicitly trusted reverse
proxy. For a proxy, bind the server to loopback, list the proxy's exact IP with
`--trusted-proxy`, preserve the public `Host` header, and send exactly one
`X-Forwarded-Proto: https` value. Forwarded transport and client-address headers
are ignored unless the immediate peer is trusted. Requests that cannot prove a
secure transport receive `426 Upgrade Required`.

Authentication failures are atomically limited per validated client address, with
bounded tracking for address churn. Authentication, authorization, throttling,
and ownership events are emitted as structured records and appended to
`<storage>\security\audit.jsonl` for CLI servers. In-memory retention is capped
at 1,000 events; the audit file rotates at 10 MiB with one previous file retained.
