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

`GET /__test/health` remains available and reports `"mode":"production"`.
Other `/__test` routes are not mapped, including state and package controls,
reset, hard deletion, request inspection, and fault injection. Test mode remains
the default and retains all existing test controls.

Production mode refuses anonymous write configuration. It also refuses cleartext
HTTP on non-loopback listeners. The CLI always binds to loopback, where HTTP is
appropriate for a local tool and an API key or Basic credentials protect writes.
Library hosts may use HTTPS on a non-loopback listener when Kestrel certificates
are configured separately.

A reverse proxy can expose the loopback listener, but the server cannot verify
the proxy's public TLS, network policy, forwarded-host handling, or identity
controls. Operators are responsible for those boundaries. This mode provides
endpoint reduction and rejects unsafe application defaults; it does not add the
broader remote identity and transport features tracked separately.

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
