# Examples

Complete scenarios that combine the CLI, the control API, and the programmatic
API. Each example is self-contained.

## 1. Restore from a local feed

```powershell
nuget-test-server start --port 5000 --data .\packages
```

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="TestServer"
         value="http://127.0.0.1:5000/v3/index.json"
         allowInsecureConnections="true" />
  </packageSources>
</configuration>
```

```powershell
dotnet restore .\MyApp.csproj --configfile .\NuGet.config
```

## 2. Publish with an API key

```powershell
$env:NUGET_TEST_SERVER_API_KEY = "publish-key"
nuget-test-server start --port 5000 --api-key-env NUGET_TEST_SERVER_API_KEY

dotnet nuget push .\Example.Package.1.0.0.nupkg `
  --source TestServer `
  --api-key $env:NUGET_TEST_SERVER_API_KEY `
  --configfile .\NuGet.config
```

Reads stay anonymous; push, unlist, and control requests require the key.

## 3. Integration test with a generated package

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

// Point the client under test at server.ServiceIndexUrl.
var response = await server.HttpClient.GetAsync("/v3/index.json");
response.EnsureSuccessStatusCode();
```

## 4. Verify retry behavior with an injected failure

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

// Exercise the client, then assert on the recorded attempts.
var requests = await server.Requests.GetAsync();
var attempts = requests
    .Where(record => record.Path.Contains("/flatcontainer/example.package/1.0.0/"))
    .ToList();
```

The same rule over HTTP:

```powershell
curl -X POST http://127.0.0.1:5000/__test/faults `
  -H "Content-Type: application/json" `
  -d '{"id":"fail-download-twice","method":"GET","pathContains":"/flatcontainer/example.package/1.0.0/","statusCode":503,"remainingMatches":2,"delay":"00:00:00"}'
```

## 5. Test a private, Basic-authenticated feed

```csharp
using NuGet.TestServer.Authentication;

var authentication = AuthenticationConfiguration.Create(
    username: "test-user",
    password: "test-password",
    apiKey: null);

await using var server = await NuGetTestServerHost.StartAsync(authentication);
```

The CLI equivalent:

```powershell
$env:NUGET_TEST_SERVER_PASSWORD = "test-password"
nuget-test-server start --username test-user --password-env NUGET_TEST_SERVER_PASSWORD
```

## 6. Assert vulnerability audit behavior

```csharp
await using var server = await NuGetTestServerHost.StartAsync();

// The embedded nuget.org baseline is served without network access.
var index = await server.HttpClient.GetAsync("/v3/vulnerabilities/index.json");
index.EnsureSuccessStatusCode();
```

Add the source to `<auditSources>` to make `dotnet restore` audit against it; see
[vulnerability auditing](nuget-protocol.md#vulnerability-auditing).

## 7. Simulate deprecation and download counts

```powershell
curl -X PUT http://127.0.0.1:5000/__test/packages/Example.Package/1.0.0/metadata `
  -H "Content-Type: application/json" `
  -d '{
    "owners": ["Alice"],
    "downloads": 42,
    "verified": true,
    "deprecation": {
      "reasons": ["Legacy"],
      "message": "Use Replacement.Package.",
      "alternatePackage": { "id": "Replacement.Package", "range": "[2.0.0,)" }
    }
  }'
```

Registration and search responses then project the deprecation, ownership, and
download metadata.

## 8. Reject a package with a deterministic scanner

```csharp
using NuGet.TestServer.Packages;

var scanner = new DeterministicPackagePolicyScanner(
    new Dictionary<string, PackageScanResult>
    {
        ["Contoso.Bad"] = new(PackageScanOutcome.Malicious, "Test verdict")
    });

await using var server = await NuGetTestServerHost.StartAsync(
    new SupplyChainOptions { RequireSignedPackages = false },
    scanner);
```

Pushing `Contoso.Bad` is rejected; inconclusive results stay quarantined and remain
invisible to restore, search, and registration.

## 9. Reset between tests

```csharp
await server.ResetAsync();
```

or

```powershell
curl -X POST http://127.0.0.1:5000/__test/reset
```

## 10. Back up and verify a restore

```powershell
nuget-test-server backup --storage .\.nuget-test-server --output .\backup.zip
nuget-test-server restore --input .\backup.zip --storage .\restored
nuget-test-server start --storage .\restored --port 5000
curl http://127.0.0.1:5000/health/ready
```
