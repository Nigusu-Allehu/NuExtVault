# Operate and deploy

## Health, logs, metrics, and tracing

Use separate probes so a failed storage dependency does not look like a crashed
process:

| Endpoint | Authentication | Meaning |
| --- | --- | --- |
| `GET /health/live` | Anonymous | The process is running. |
| `GET /health/ready` | Anonymous | Configured storage exists and is writable. Extension refresh failures are reported as degraded but remain ready; storage failures return HTTP 503. |
| `GET /health/storage` | Write/control credential | Package count, storage bytes, available disk bytes, the vulnerability owner-state record plus any legacy cache entries awaiting migration, and the owner-state retention limit of one. |

Production mode writes JSON console logs. Request completion and failures use
structured fields for method, path, status, elapsed time, and exceptions. Do not
log API keys, Basic credentials, or certificate passwords.

The server publishes built-in .NET `Meter` and `ActivitySource` data under
`NuGet.TestServer`. This is OpenTelemetry-compatible without requiring an
exporter in the application:

- `nuget.server.requests`, `nuget.server.errors`, and
  `nuget.server.request.duration`
- `nuget.server.packages` and `nuget.server.packages.published`
- `nuget.server.storage.failures`
- `nuget.request` server activities

An operator can attach the OpenTelemetry .NET automatic instrumentation and
configure its normal OTLP exporter. Include `NuGet.TestServer` in
`OTEL_DOTNET_AUTO_METRICS_ADDITIONAL_SOURCES` and
`OTEL_DOTNET_AUTO_TRACES_ADDITIONAL_SOURCES`. ASP.NET Core's built-in request
instrumentation remains available alongside these package and storage signals.

Vulnerability snapshot state is integrity-protected and atomically replaced as one
owner-scoped record. During upgrade, storage health also counts legacy
`vulnerabilities` cache directories until they are removed by an operator. Packages
have no automatic retention policy; unlisting does not reclaim their files, and hard
deletion is intentionally unavailable in production mode. Monitor
`FreeBytes`, define an alert threshold appropriate for the volume, and manage
package retention through a reviewed offline storage procedure.

## Run the supported container image

The repository `Dockerfile` builds a non-root ASP.NET Core image, stores state
under `/data`, and listens on HTTPS port 8080. Supply a PKCS#12 certificate,
writable persistent storage, and the publishing key:

```powershell
docker build -t nuget-test-server .

docker run --rm `
  --publish 8443:8080 `
  --volume nuget-test-server-data:/data `
  --volume "${PWD}\https:/https:ro" `
  --env NUGET_TEST_SERVER_API_KEY="<secret>" `
  --env ASPNETCORE_Kestrel__Certificates__Default__Password="<certificate-password>" `
  nuget-test-server
```

The certificate must be mounted as `/https/server.pfx`; use a CA-issued
certificate for shared environments. Ensure the persistent volume is writable by
the image's non-root `APP_UID`. Keep secrets in the orchestrator's secret store,
not in an image, compose file, or source control. Probe
`https://<host>:8443/health/live` and `/health/ready`.

## Run as a service

Install the packed tool or published CLI under `/opt/nuget-test-server`, create a
dedicated unprivileged account, and make `/var/lib/nuget-test-server` writable by
that account. A minimal systemd unit for a TLS-terminating reverse proxy on the
same host is:

```ini
[Unit]
Description=NuGet Test Server
After=network.target

[Service]
User=nuget-test-server
Group=nuget-test-server
EnvironmentFile=/etc/nuget-test-server.env
ExecStart=/opt/nuget-test-server/NuGet.TestServer.Cli start --production --port 5000 --storage /var/lib/nuget-test-server --api-key-env NUGET_TEST_SERVER_API_KEY
Restart=on-failure
NoNewPrivileges=true
PrivateTmp=true
ReadWritePaths=/var/lib/nuget-test-server

[Install]
WantedBy=multi-user.target
```

Restrict `/etc/nuget-test-server.env` to the service account. Bind the reverse
proxy only to this loopback listener, enforce public TLS and network policy at
the proxy, and forward a fixed host. Windows services should follow the same
model: dedicated identity, loopback or HTTPS listener, protected environment
secrets, writable data directory, and restart-on-failure.

## Back up and restore

Backup and restore are offline commands. Stop the server first so package writes
and vulnerability refreshes cannot race the archive:

```powershell
nuget-test-server backup `
  --storage C:\NuGetTestServer\data `
  --output C:\Backups\nuget-test-server-2026-08-18.zip
```

The archive contains persisted package data and namespaced `extension-state` (plus
the legacy `vulnerabilities` tree when present) together with a
versioned manifest with every file's length and SHA-256 hash. Version 2 manifests
also record every persisted extension state participant: extension ID, extension
version, schema name, schema version, whether the state is required, its record
count, and an integrity hash. Backup holds one exclusive storage lease, so package,
publication, and extension state come from the same offline checkpoint; when a
live server holds that lease, backup fails with an explicit unavailable error
instead of writing an inconsistent archive. Capture is read-only: it reads the
committed record tree exactly as it stands, streams each record through a fixed
buffer instead of loading the state set, and never imports version 1 records,
migrates a schema, or rewrites a participant descriptor. Completing a transaction
the store already committed is the one explicit exception, because the archive has
to contain the batch that commit made authoritative. The transactional store's
commit journals are control files rather than state, so they are never archived.
Credentials, runtime request history, and fault rules are not stored. Copy backups
to separate durable storage and apply the organization's encryption, access, and
retention policy.

Restore only into storage that does not already contain `packages`,
`extension-state`, or `vulnerabilities`:

```powershell
nuget-test-server restore `
  --input C:\Backups\nuget-test-server-2026-08-18.zip `
  --storage C:\NuGetTestServer\recovered
```

Restore rejects unsafe paths, missing files, unsupported manifests, and any
length or SHA-256 mismatch before activating recovered data. It also validates
the complete participant set first: a backup that requires an extension this
build does not provide, declares a newer schema version, or has no complete
migration path is rejected before anything is written, and state belonging to an
inactive extension is quarantined under `extension-state\quarantine` rather than
activated. A version 2 archive is validated in both directions, so it can neither
hide a participant it declares nor deliver participant state the manifest never
declared; staged records are streamed and bounded by the same per-record,
per-owner, and owner-count quotas the live store enforces. The version 1
downgrade mirror an archive carries beside that tree is held to the same
standard, because the next server start adopts mirror-only records of a
registered owner into the authoritative tree: for a declared owner, every mirror
record must project a committed record with the same hashed owner and key path
and the same envelope key and payload identity, and a mirror for a registered
owner the manifest never declared is rejected outright. All content is staged,
then committed through a single journal file;
an interrupted commit is completed on the next restore rather than leaving a
partial set. An archive that carries an extension-state commit journal is
rejected before anything is written, and the server refuses to start against a
state directory whose journal it did not write. Version 1 backups remain
restorable. After restore,
start against the recovered directory, wait for `/health/ready`, fetch the
service index, and restore a known package through a real NuGet client.

When the archive contains required state of a trusted extension, restore with the
same extension configuration:

```powershell
nuget-test-server restore --input .\backup.zip --storage .\restored `
  --extension-root .\extensions `
  --extension-trust-root .\trust\nutest.json
```

## Upgrade, rollback, and disaster recovery

1. Stop publishing, stop the service, create a backup, copy it off-host, and run
   a test restore into an empty directory.
2. Keep the previous binary or container digest. Upgrade only the executable or
   image; reuse the persistent data volume.
3. Start the new version, require successful liveness and readiness probes, then
   verify service-index discovery, a known package restore, publishing, and
   vulnerability audit before reopening traffic.
4. To roll back application code, stop the new version and restart the previous
   binary or image against the unchanged data. If storage was damaged or
   intentionally changed, restore the pre-upgrade archive into a clean directory
   instead of overlaying files.
5. For host or volume loss, provision a clean instance, restore the newest
   validated off-host archive, restore secrets and TLS configuration from their
   separate stores, start the service, run the same probes and NuGet checks, then
   switch traffic.

Record backup age, restore-test results, binary/container version, certificate
expiry, free space, and recovery time. This baseline deliberately does not add a
metadata database or migration system; durable metadata redesign remains a
separate concern.
