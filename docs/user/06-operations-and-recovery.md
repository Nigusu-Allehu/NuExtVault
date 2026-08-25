# 6. Operations: health, diagnostics, backup, restore, and recovery

[User manual](README.md)

## Health

Liveness reports process responsiveness and mode; it deliberately avoids storage
and inventory work.

<!-- example-id: user-06-liveness; evidence: executable -->
```powershell
Invoke-RestMethod "{{BASE_URL}}/health/live"
```

Readiness performs a bounded writable-storage probe and extension health check.
It returns `503` when dependencies are not ready and does not enumerate packages.

<!-- example-id: user-06-readiness; evidence: executable -->
```powershell
Invoke-WebRequest "{{BASE_URL}}/health/ready" -SkipHttpErrorCheck |
  Select-Object StatusCode,Content
```

`GET /health/storage` requires control authorization and may reveal absolute
paths, counts, capacity, and OS errors. It recursively inventories storage and is
not a frequent probe. `/__test/health` is the legacy liveness alias.

## Diagnostics

There is no diagnostics or metrics HTTP endpoint. Process-local counters reset
on restart. The meter and activity source is `NuExtVault`; embedding hosts
may attach listeners/exporters. NuExtVault does not configure an exporter.
Request logs include method, path, status, and elapsed time.

## Offline backup

Stop the server first: a live server owns the exclusive storage lease. The
output archive must not already exist.

<!-- example-id: user-06-backup; evidence: executable -->
```powershell
& "{{TOOL_COMMAND}}" backup --storage "{{STORAGE}}" --output "{{BACKUP}}"
```

Version 2 backups include package blobs and SQLite state, ownership, persisted
security audit logs, trash, vulnerability state, transactional extension state,
staged content, publication recovery state, and SHA-256 manifest data.
Credentials, TLS configuration, request history, and fault rules are excluded.
Treat archives as sensitive.

## Restore to clean storage

<!-- example-id: user-06-restore; evidence: executable -->
```powershell
& "{{TOOL_COMMAND}}" restore --input "{{BACKUP}}" --storage "{{RECOVERED}}"
```

Restore stages beside the target and validates paths, lengths, fixed-time hashes,
participant schemas, integrity, quotas, and free space before commit. The target
must be offline and clean. Version 1 archives remain readable. Restoring required
external-extension state also needs matching extension and trust roots; see
[Chapter 7](07-trusted-extensions-and-package-staging.md).

## Startup recovery

Durable metadata and blobs survive restart. Startup removes interrupted temporary
publications, validates and imports complete orphan package blobs, resolves pending
deletes, rolls committed extension-state journals forward, removes expired,
orphaned, or incomplete staged-content artifacts, and recovers Package Staging
publication before listening. Valid staged groups and content survive restart.
Missing or wrong-length tracked blobs fail startup. Only one process may own a
storage root.

The top-level restore commit journal is recovered by the next **restore command**,
not normal server startup. Never delete a recovered target reflexively; inspect it.
Rollback to an older binary is safe only when that binary supports the persisted
schemas. Otherwise restore a tested pre-upgrade backup into a different clean
directory rather than overlaying files.

**Previous:** [Control API and deterministic faults](05-control-api-and-faults.md)  
**Next:** [Trusted extensions and Package Staging](07-trusted-extensions-and-package-staging.md)
