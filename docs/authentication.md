# Authentication

Authentication behavior is inferred from the supplied credentials. There is no
separate `--auth` option.

| Options | Feed behavior |
| --- | --- |
| No credentials | Anonymous reads and writes |
| API key only | Public reads; API key required for push, unlist, and control |
| Username and password | Basic authentication required for all package and control operations |
| Username, password, and API key | Basic authentication for reads; Basic and API key required for writes and control |

## Simulate NuGet.org

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

## Simulate a private source

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

## Require separate read and publishing credentials

```powershell
nuget-test-server start `
  --username test-user `
  --password-env NUGET_TEST_SERVER_PASSWORD `
  --api-key-env NUGET_TEST_SERVER_API_KEY
```

The username/password protects all requests. Push, unlist, and control requests
must additionally send `X-NuGet-ApiKey`.

## Supplying secrets

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

For multi-identity, scope-based production configuration see
[Production-safe mode](configuration.md#production-safe-mode).

## Configure authentication programmatically

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
