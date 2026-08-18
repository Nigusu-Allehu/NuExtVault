# NuGet Test Server

[![CI](https://github.com/Nigusu-Allehu/NuTestServer/actions/workflows/ci.yml/badge.svg)](https://github.com/Nigusu-Allehu/NuTestServer/actions/workflows/ci.yml)

A lightweight local NuGet V3 server for deterministic integration and
end-to-end testing. It runs on real Kestrel, persists CLI package state locally,
uses isolated in-memory state for programmatic test servers, and provides
test-only APIs for resetting state, injecting failures, and inspecting requests.
It also serves a local nuget.org vulnerability snapshot for offline audit and
Package Manager UI scenarios.

## Requirements

- .NET SDK 10.0 or later

## Build and test

```powershell
dotnet build NuGet.TestServer.slnx
dotnet test NuGet.TestServer.slnx
```

CI runs warning-free Release builds and the complete unit and functional suites
on Windows, Ubuntu, and macOS.

The test suite includes:

- Unit tests for package creation, storage, search, unlisting, and fault matching.
- Functional tests against a real loopback Kestrel server.
- End-to-end tests using NuGet.Protocol, `dotnet restore`, and `dotnet nuget push`.
- Authentication tests for API-key publishing and private Basic-authenticated feeds.
- Vulnerability schema, cache-integrity, registration, and real restore-audit tests.
- Functional tests that start and probe the packaged CLI.

## Start the server

Run the CLI directly from the repository:

```powershell
dotnet run --project .\src\NuGet.TestServer.Cli -- start
```

The CLI selects an available loopback port and prints the endpoints:

```text
Source:      http://127.0.0.1:54321/v3/index.json
Mode:        Test
Control API: http://127.0.0.1:54321/__test
Health:      http://127.0.0.1:54321/__test/health
Liveness:    http://127.0.0.1:54321/health/live
Readiness:   http://127.0.0.1:54321/health/ready
Storage:     C:\Users\<user>\AppData\Local\nuget-test-server
Vulnerabilities: 2026-08-18T17:36:11.6736167+00:00 (<snapshot-id>)
```

Use a fixed port when needed:

```powershell
dotnet run --project .\src\NuGet.TestServer.Cli -- start --port 5000
```

Seed every `.nupkg` in a directory during startup:

```powershell
dotnet run --project .\src\NuGet.TestServer.Cli -- start --data .\packages
```

CLI packages persist across restarts under:

```text
%LOCALAPPDATA%\nuget-test-server
```

On non-Windows systems, the path is based on .NET's
`Environment.SpecialFolder.LocalApplicationData`. Override it for CI or isolated
development:

```powershell
nuget-test-server start --storage .\.nuget-test-server
```

Stop the server with Ctrl+C.

### Use production-safe mode

Production-safe mode removes the test control surface while retaining the NuGet
protocol endpoints:

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

A reverse proxy can expose the loopback listener, but the server cannot verify
the proxy's public TLS, network policy, forwarded-host handling, or identity
controls. Operators are responsible for those boundaries. This mode provides
endpoint reduction and rejects unsafe application defaults; it does not add the
broader remote identity and transport features tracked separately.

## Operate and deploy

### Health, logs, metrics, and tracing

Use separate probes so a failed storage dependency does not look like a crashed
process:

| Endpoint | Authentication | Meaning |
| --- | --- | --- |
| `GET /health/live` | Anonymous | The process is running. |
| `GET /health/ready` | Anonymous | Configured storage exists and is writable. Returns HTTP 503 when it is not. |
| `GET /health/storage` | Write/control credential | Package count, storage bytes, available disk bytes, cached snapshot count, and the snapshot retention limit. |

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

Cached vulnerability snapshots retain the three newest valid entries. Packages
have no automatic retention policy; unlisting does not reclaim their files, and
hard deletion is intentionally unavailable in production mode. Monitor
`FreeBytes`, define an alert threshold appropriate for the volume, and manage
package retention through a reviewed offline storage procedure.

### Run the supported container image

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

### Run as a service

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

### Back up and restore

Backup and restore are offline commands. Stop the server first so package writes
and vulnerability refreshes cannot race the archive:

```powershell
nuget-test-server backup `
  --storage C:\NuGetTestServer\data `
  --output C:\Backups\nuget-test-server-2026-08-18.zip
```

The archive contains the persisted `packages` and `vulnerabilities` trees plus a
versioned manifest with every file's length and SHA-256 hash. Credentials,
runtime request history, and fault rules are not stored. Copy backups to
separate durable storage and apply the organization's encryption, access, and
retention policy.

Restore only into storage that does not already contain `packages` or
`vulnerabilities`:

```powershell
nuget-test-server restore `
  --input C:\Backups\nuget-test-server-2026-08-18.zip `
  --storage C:\NuGetTestServer\recovered
```

Restore rejects unsafe paths, missing files, unsupported manifests, and any
length or SHA-256 mismatch before activating recovered data. After restore,
start against the recovered directory, wait for `/health/ready`, fetch the
service index, and restore a known package through a real NuGet client.

### Upgrade, rollback, and disaster recovery

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

## Install the CLI as a local .NET tool

Create the tool package:

```powershell
dotnet pack .\src\NuGet.TestServer.Cli -o .\artifacts
```

Install it into a repository-local tool directory:

```powershell
dotnet tool install `
  --tool-path .\.tools `
  NuGet.TestServer.Cli `
  --add-source .\artifacts `
  --version 1.0.0
```

Run it:

```powershell
.\.tools\nuget-test-server start
```

## Configure NuGet

Local development uses HTTP on loopback. Current NuGet clients require HTTP sources to explicitly allow insecure connections:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add
      key="TestServer"
      value="http://127.0.0.1:54321/v3/index.json"
      allowInsecureConnections="true" />
  </packageSources>
</configuration>
```

Replace `54321` with the port printed by the server.

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

CLI servers prefer a newer valid snapshot cached under:

```text
<storage>\vulnerabilities
```

After the server is ready, a stale snapshot is refreshed from nuget.org in the
background. A refresh downloads every referenced page with bounded timeouts and
sizes, validates the complete snapshot, records source and SHA-256 integrity
metadata, and atomically activates it. Failed or partial refreshes produce a
warning and leave the previous snapshot available. The three most recent cached
snapshots are retained.

Programmatic servers created with `NuGetTestServerHost.StartAsync()` always use
the embedded snapshot and never refresh from the network, preserving
deterministic test behavior.

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

## Authentication

Authentication behavior is inferred from the supplied credentials. There is no
separate `--auth` option.

| Options | Feed behavior |
| --- | --- |
| No credentials | Anonymous reads and writes |
| API key only | Public reads; API key required for push, unlist, and control |
| Username and password | Basic authentication required for all package and control operations |
| Username, password, and API key | Basic authentication for reads; Basic and API key required for writes and control |

### Simulate NuGet.org

Make restore, search, and download public while protecting publishing:

```powershell
$env:NUGET_TEST_SERVER_API_KEY = "publish-key"

nuget-test-server start `
  --api-key-env NUGET_TEST_SERVER_API_KEY
```

Push with the standard NuGet API-key option:

```powershell
dotnet nuget push .\Example.Package.1.0.0.nupkg `
  --source TestServer `
  --api-key $env:NUGET_TEST_SERVER_API_KEY `
  --configfile .\NuGet.config
```

### Simulate a private source

Require Basic authentication for service discovery, restore, search, download,
push, unlist, and the control API:

```powershell
$env:NUGET_TEST_SERVER_PASSWORD = "test-password"

nuget-test-server start `
  --username test-user `
  --password-env NUGET_TEST_SERVER_PASSWORD
```

Configure NuGet credentials:

```powershell
dotnet nuget add source `
  http://127.0.0.1:54321/v3/index.json `
  --name TestServer `
  --username test-user `
  --password $env:NUGET_TEST_SERVER_PASSWORD `
  --store-password-in-clear-text `
  --valid-authentication-types basic `
  --allow-insecure-connections
```

### Require separate read and publishing credentials

```powershell
nuget-test-server start `
  --username test-user `
  --password-env NUGET_TEST_SERVER_PASSWORD `
  --api-key-env NUGET_TEST_SERVER_API_KEY
```

The username/password protects all requests. Push, unlist, and control requests
must additionally send `X-NuGet-ApiKey`.

Supported secret inputs:

- `--password-env <name>` and `--api-key-env <name>`
- `--password-stdin` and `--api-key-stdin`
- `--password <value>` and `--api-key <value>` for local convenience
- `--generate-api-key`

Literal command-line secrets produce a warning because other processes and CI
logs may expose command arguments. Credentials are held only for the running
process and are not persisted with package storage.

When `--username` is supplied without a password option in an interactive
terminal, the CLI prompts for the password without echoing it. Use
`--generate-api-key` to create a high-entropy runtime key; it is printed once
and becomes invalid when the process exits.

### Configure authentication programmatically

Public reads with API-key-protected writes:

```csharp
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Hosting;

var authentication = AuthenticationConfiguration.Create(
    username: null,
    password: null,
    apiKey: "publish-key");

await using var server =
    await NuGetTestServerHost.StartAsync(authentication);
```

A private source:

```csharp
var authentication = AuthenticationConfiguration.Create(
    username: "test-user",
    password: "test-password",
    apiKey: null);

await using var server =
    await NuGetTestServerHost.StartAsync(authentication);
```

## Add packages

### Push with the .NET CLI

The server accepts standard NuGet push requests. With no credentials configured,
push is anonymous. With an API key configured, pass it through `--api-key`.
Private feeds additionally require their configured Basic credentials.

```powershell
dotnet nuget push .\packages\Example.Package.1.0.0.nupkg `
  --source TestServer `
  --api-key test `
  --configfile .\NuGet.config
```

Pushing the same package ID and version again returns `409 Conflict`.

### Seed a directory

Place existing `.nupkg` files in one directory and start with `--data`:

```powershell
nuget-test-server start --data .\packages
```

### Add generated packages in tests

Reference `src\NuGet.TestServer\NuGet.TestServer.csproj`, then:

```csharp
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

await using var server = await NuGetTestServerHost.StartAsync();

var package = TestPackageBuilder
    .Create("Example.Package", "1.0.0")
    .WithDescription("Package used by an integration test")
    .WithDependency("Dependency.Package", "[2.0.0, 3.0.0)")
    .WithFile("lib/net10.0/example.txt", "content")
    .Build();

await server.Packages.AddAsync(package);

Uri source = server.ServiceIndexUrl;
```

Each in-process server binds to a random loopback port and owns isolated package, fault, and request state.

## Use the control API

The `/__test` control endpoints are test-only and are never advertised in the
NuGet service index. Production-safe mode maps only `/__test/health`; all
control endpoints below are absent.

When authentication is configured, the control API uses the same write policy:

- API-key feeds require `X-NuGet-ApiKey`.
- Private feeds require Basic authentication.
- Private feeds with a separate publishing key require both.
- `GET /__test/health` always remains anonymous.

For an API-key feed, add the header to `curl` examples:

```powershell
curl http://127.0.0.1:54321/__test/state `
  -H "X-NuGet-ApiKey: $env:NUGET_TEST_SERVER_API_KEY"
```

For a private feed, use `curl -u test-user:test-password`.

### Inspect state

```powershell
curl http://127.0.0.1:54321/__test/state
curl http://127.0.0.1:54321/__test/packages
curl http://127.0.0.1:54321/__test/requests
curl http://127.0.0.1:54321/__test/faults
```

### Reset the server

Reset packages, faults, and request history:

```powershell
curl -X POST http://127.0.0.1:54321/__test/reset
```

In an in-process test:

```csharp
await server.ResetAsync();
```

### Unlist, relist, or delete a package

```powershell
curl -X POST http://127.0.0.1:54321/__test/packages/Example.Package/1.0.0/unlist
curl -X POST http://127.0.0.1:54321/__test/packages/Example.Package/1.0.0/list
curl -X DELETE http://127.0.0.1:54321/__test/packages/Example.Package/1.0.0
```

The NuGet protocol `DELETE /package/{id}/{version}` unlists a package. The
control API `DELETE` removes it from the active store, including persisted CLI
storage.

### Inject deterministic failures

The following rule fails the next two matching package-download requests with HTTP 503:

```powershell
curl -X POST http://127.0.0.1:54321/__test/faults `
  -H "Content-Type: application/json" `
  -d '{
    "id": "fail-download-twice",
    "method": "GET",
    "pathContains": "/flatcontainer/example.package/1.0.0/",
    "statusCode": 503,
    "remainingMatches": 2,
    "delay": "00:00:00"
  }'
```

The same behavior can be configured in process:

```csharp
using System.Net;
using NuGet.TestServer.Faults;

await server.Faults.AddAsync(new FaultRule(
    Id: "fail-download-twice",
    Method: "GET",
    PathContains: "/flatcontainer/example.package/1.0.0/",
    StatusCode: HttpStatusCode.ServiceUnavailable,
    RemainingMatches: 2,
    Delay: TimeSpan.Zero));
```

After exercising the client, inspect `server.Requests.GetAsync()` or `GET /__test/requests` to verify attempts, response codes, durations, and matched fault rules.

## Supported NuGet operations

The current implementation supports:

- V3 service-index discovery
- Package Base Address / flat-container downloads
- Inline registration metadata
- Package search
- Package push
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

## Repository layout

```text
src/
  NuGet.TestServer/       Server, protocol endpoints, store, and in-process API
  NuGet.TestServer.Cli/   Command-line .NET tool

tests/
  NuGet.TestServer.UnitTests/
  NuGet.TestServer.FunctionalTests/
```

## Contributing workflow

Repository agents and contributors follow the workflow in
[`AGENTS.md`](AGENTS.md):

1. Propose the design and test plan.
2. Wait for explicit approval.
3. Add failing unit and functional tests.
4. Implement the approved behavior.
5. Validate the complete suite and package.
6. Update this README last.

## Important limitations

- This is test infrastructure, not a production package feed.
- Programmatic test-server storage is in memory; the CLI persists packages locally.
- The server uses anonymous HTTP by default unless credentials are supplied.
- Multi-user authorization, scoped keys, HTTPS, advanced network faults, symbols,
  and repository signatures are not yet implemented.
- The server binds to `127.0.0.1` unless its hosting configuration is changed.
