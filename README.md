# NuGet Test Server

[![CI](https://github.com/Nigusu-Allehu/NuTestServer/actions/workflows/ci.yml/badge.svg)](https://github.com/Nigusu-Allehu/NuTestServer/actions/workflows/ci.yml)

A lightweight local NuGet V3 server for deterministic integration and
end-to-end testing. It runs on real Kestrel, persists CLI package state locally,
uses isolated in-memory state for programmatic test servers, and provides
test-only APIs for resetting state, injecting failures, and inspecting requests.
It also serves a local nuget.org vulnerability snapshot for offline audit and
Package Manager UI scenarios.

## Quick start

Requires .NET SDK 10.0 or later.

```powershell
dotnet run --project .\src\NuGet.TestServer.Cli -- start
```

The CLI selects an available loopback port and prints the service index, control
API, health endpoints, and storage root:

```text
Source:      http://127.0.0.1:54321/v3/index.json
Mode:        Test
Control API: http://127.0.0.1:54321/__test
Health:      http://127.0.0.1:54321/__test/health
```

From a test project, start an isolated in-process server instead:

```csharp
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

await using var server = await NuGetTestServerHost.StartAsync();

await server.Packages.AddAsync(
    TestPackageBuilder.Create("Example.Package", "1.0.0").Build());

Uri source = server.ServiceIndexUrl;
```

## What it does

- Serves the NuGet V3 protocol: discovery, flat container, registration, search,
  push, symbol push, unlisting, and vulnerability data.
- Simulates feed shapes: anonymous, API-key-protected, private Basic-authenticated,
  and scope-based production identities.
- Injects deterministic failures and records requests so clients can be tested
  against retries, timeouts, and error responses.
- Persists CLI package state transactionally, with backup, restore, and health
  probes for longer-lived local deployments.
- Composes its features as extensions over a kernel, with a public extension SDK.

## Documentation

Full documentation lives in [`docs/`](docs/README.md).

| Topic | Page |
| --- | --- |
| Install, build, run, configure NuGet | [Getting started](docs/getting-started.md) |
| End-to-end usage scenarios | [Examples](docs/examples.md) |
| Every command and option | [CLI reference](docs/cli-reference.md) |
| Limits, storage, production-safe mode | [Configuration and storage](docs/configuration.md) |
| Credentials and feed behavior | [Authentication](docs/authentication.md) |
| Pushing, seeding, metadata, moderation | [Working with packages](docs/packages.md) |
| Test-only `/__test` endpoints | [Control API](docs/control-api.md) |
| Supported protocol surface | [NuGet protocol support](docs/nuget-protocol.md) |
| Health, metrics, container, backup | [Operate and deploy](docs/operations.md) |
| `NuGetTestServerHost` and friends | [Programmatic API](docs/api/hosting.md) |
| Building and loading extensions | [Extensions](docs/extensions/README.md) |
| Kernel and composition internals | [Architecture](docs/architecture.md) |
| Known constraints | [Limitations](docs/limitations.md) |

## Build and test

```powershell
dotnet build NuGet.TestServer.slnx
dotnet test NuGet.TestServer.slnx
```

CI runs warning-free Release builds and the complete unit and functional suites on
Windows, Ubuntu, and macOS.

## Contributing

Repository agents and contributors follow the workflow in [`AGENTS.md`](AGENTS.md):

1. Propose the design and test plan.
2. Wait for explicit approval.
3. Add failing unit and functional tests.
4. Implement the approved behavior.
5. Validate the complete suite and package.
6. Update the documentation last.

Published design documents live in [`design/`](design/README.md).
