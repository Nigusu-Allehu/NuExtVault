# Working with packages

## Push with the .NET CLI

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

## Seed a directory

Place existing `.nupkg` files in one directory and start with `--data`:

```powershell
nuget-test-server start --data .\packages
```

## Add generated packages in tests

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

Each in-process server binds to a random loopback port and owns isolated package,
fault, and request state. See the
[programmatic API reference](api/hosting.md#testpackagebuilder) for the complete
builder surface.

## Repository-owned metadata

Tests can set repository-owned metadata without rewriting a package archive:

```http
PUT /__test/packages/{id}/{version}/metadata
Content-Type: application/json

{
  "owners": ["Alice", "Bob"],
  "downloads": 42,
  "verified": true,
  "deprecation": {
    "reasons": ["Legacy"],
    "message": "Use Replacement.Package.",
    "alternatePackage": {
      "id": "Replacement.Package",
      "range": "[2.0.0,)"
    }
  }
}
```

Downloads must be non-negative, deprecation reasons are limited to `Legacy`,
`CriticalBugs`, and `Other`, and alternate-package ranges must be valid NuGet
version ranges. This metadata persists with CLI package storage.

## Validation and moderation

Protocol pushes are quarantine-first. The server validates NuGet signatures and
invokes the configured `IPackagePolicyScanner` before changing a package to
`Published`. Invalid signatures and malicious scan results are rejected;
inconclusive results and scanner failures remain quarantined. Only published
packages are visible through flat-container content, versions, registration,
search, rich metadata, or symbol retrieval. The same filtering is restored from
durable state after restart.

`SupplyChainOptions` configures required signatures, per-identity and
per-repository package/byte quotas, and reserved package-ID namespaces.
Identical retries are idempotent; a different archive for an existing ID/version
conflicts because published versions are immutable. Trusted test-control seeds
are recorded as published without protocol validation.

Administrators can approve, reject, quarantine, or controlled-delete a version:

```http
POST /__admin/packages/{id}/{version}/approve?reason=reviewed
POST /__admin/packages/{id}/{version}/reject?reason=policy
POST /__admin/packages/{id}/{version}/quarantine?reason=investigation
POST /__admin/packages/{id}/{version}/delete?reason=retention
GET  /__admin/packages/{id}/{version}/validations
GET  /__admin/supply-chain/audit
```

Moderation, validation records, ownership, tombstones, and audit history survive
CLI restarts. Missing policy metadata fails closed by recovering durable package
blobs as quarantined.

Configure the policy in process:

```csharp
var supplyChain = new SupplyChainOptions
{
    RequireSignedPackages = false,
    MaximumPackagesPerIdentity = 100,
    NamespaceReservations = new Dictionary<string, string>
    {
        ["Contoso."] = "contoso-publisher"
    }
};

await using var server = await NuGetTestServerHost.StartAsync(
    supplyChain,
    new DeterministicPackagePolicyScanner(new Dictionary<string, PackageScanResult>
    {
        ["Contoso.Bad"] = new(PackageScanOutcome.Malicious, "Test verdict")
    }));
```

## Unlist, relist, and delete

The NuGet protocol `DELETE /package/{id}/{version}` unlists a package. The
[control API](control-api.md#unlist-relist-or-delete-a-package) `DELETE` removes
it from the active store, including persisted CLI storage. Hard deletion with
production identities is available at `DELETE /package/{id}/{version}/hard`.
