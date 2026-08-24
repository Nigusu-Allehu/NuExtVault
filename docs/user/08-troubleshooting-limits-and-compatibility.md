# 8. Troubleshooting, limits, compatibility, and known constraints

[User manual](README.md)

## Common failures

| Symptom | Cause | Action |
| --- | --- | --- |
| Storage already in use | One process owns a storage root | Stop it or select a unique `--storage` path |
| Exit code 2 and port error | Port is outside `0..65535` | Use `--port 0` or a valid fixed port |
| `401` | Missing or invalid credentials | Match the configured profile |
| `403` | Missing scope, namespace, ownership, or admin permission | Correct authorization; do not retry unchanged |
| `426` | Production identity cannot prove HTTPS | Use HTTPS or one exact trusted proxy |
| `429` plus `Retry-After` | Authentication attempt limit reached | Wait, then fix credentials |
| `413` | Package/request/archive limit exceeded | Reduce input or deliberately raise the applicable limit |
| Readiness `503` | Storage or an extension is unavailable | Check permissions, capacity, integrity, and extension health |
| Package absent from search | Unlisted, quarantined, deleted, or not promoted | Inspect exact registration and administrative state |
| Exact restore succeeds but search omits it | Package is unlisted | This is intentional NuGet compatibility |
| Backup reports storage in use | Backup is offline | Stop the server first |
| Extension route missing | Discovery/configuration/restart is missing | Configure roots, trust, grants, then restart |

## Resource limits

<!-- example-id: user-08-limit-table; evidence: reference -->
```text
HTTP request body                 128 MiB
Compressed package               100 MiB
Archive entries                   10,000
One expanded archive entry        64 MiB
Total expanded archive content   512 MiB
Request history                   10,000
Fault rules                          100
```

Configure package transfer limits explicitly when needed:

This is a non-executable foreground-command reference. Contract tests compare
every displayed option and value with CLI parsing and package-limit behavior
covered by the functional suite.

<!-- example-id: user-08-limits; evidence: reference -->
```powershell
& "{{TOOL_COMMAND}}" start --port "{{PORT}}" --storage "{{STORAGE}}" `
  --max-request-bytes 67108864 `
  --max-package-bytes 52428800 `
  --max-archive-entries 5000 `
  --max-entry-bytes 16777216 `
  --max-expanded-bytes 268435456
```

Rejected and cancelled uploads remove partial temporary files. Package Staging
defaults to at most 50 packages and a 1,440-minute group TTL. Its kernel content
bounds and external-loader bounds are internal implementation limits, not CLI
settings.

## Compatibility

NuTestServer targets `net10.0` and requires the .NET 10 SDK to build. General CI
performs a warning-as-error Release build and runs unit and functional tests on
Windows, Ubuntu, and macOS. The dedicated documentation workflow runs the
manual contracts and examples on the same platforms. Automated evidence covers
real loopback Kestrel, NuGet.Protocol 7.9.0, `dotnet restore`,
`dotnet nuget push`, GET/HEAD behavior, ranges, ordering, casing, paging, and
immediate visibility transitions. This does not claim compatibility with every
historical NuGet client.

Default durable storage uses .NET's local application-data directory. Use an
explicit per-run `--storage` path for portable automation. Path options consume
one platform-native argument and do not split delimiter-separated lists.

## Known constraints

- One process and one node; no distributed storage or coordination.
- Programmatic hosts are isolated, in-memory, and external-network-independent
  by default, but still use real loopback Kestrel.
- Trusted extensions run in process and are not sandboxed.
- No extension network feed, hot reload, dynamic route mutation, or sidecars.
- SDK/TestKit and Package Staging artifacts are not externally published.
- Package Staging is opt-in and absent from default profiles.
- No automatic package-retention policy; unlisting does not reclaim package blobs.
- Backups exclude credentials, request history, and fault rules.

Use unique ports, storage, package caches, and credentials. Stop the owning
process before cleanup or backup. Never recursively delete a shared/default
storage root. Encrypt and restrict backup archives according to their package and
state sensitivity.

**Previous:** [Trusted extensions and Package Staging](07-trusted-extensions-and-package-staging.md)  
**Next:** [User manual index](README.md)
