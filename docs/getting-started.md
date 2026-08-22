# Getting started

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

Stop the server with Ctrl+C.

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

Local development uses HTTP on loopback. Current NuGet clients require HTTP sources
to explicitly allow insecure connections:

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

## Use the server from a test project

Reference `src\NuGet.TestServer\NuGet.TestServer.csproj` and start an isolated,
in-process server:

```csharp
using NuGet.TestServer.Hosting;

await using var server = await NuGetTestServerHost.StartAsync();
Uri source = server.ServiceIndexUrl;
```

See the [programmatic API reference](api/hosting.md) and the
[examples](examples.md) for complete scenarios.

## Next steps

- [CLI reference](cli-reference.md)
- [Configuration and storage](configuration.md)
- [Authentication](authentication.md)
- [Control API](control-api.md)
