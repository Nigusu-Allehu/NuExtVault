# 3. Programmatic testing

[User manual](README.md)

`NuGetTestServerHost` starts real Kestrel on an OS-selected loopback port while
keeping package, request, fault, profile, extension, and vulnerability state
host-scoped and independent of external networks by default.

## Start a host and generate a package

Reference the repository's `src/NuGet.TestServer/NuGet.TestServer.csproj` and
NuGet.Protocol 7.9.0 from a `net10.0` test or console project.

<!-- example-id: user-03-generated-package; evidence: executable -->
```csharp
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

await using var server = await NuGetTestServerHost.StartAsync();

var package = TestPackageBuilder.Create("Docs.Generated.Package", "1.0.0")
    .WithAuthors("Documentation tests")
    .WithDescription("Generated entirely in memory")
    .WithDependency("Docs.Dependency", "[2.0.0, 3.0.0)")
    .WithFile("lib/net10.0/message.txt", "hello")
    .Build();

await server.Packages.AddAsync(package);

var repository = Repository.Factory.GetCoreV3(server.ServiceIndexUrl.ToString());
var resource = await repository.GetResourceAsync<FindPackageByIdResource>()
    ?? throw new InvalidOperationException("PackageBaseAddress was not advertised.");
using var cache = new SourceCacheContext { NoCache = true, DirectDownload = true };
var versions = await resource.GetAllVersionsAsync(
    "docs.generated.package", cache, NullLogger.Instance, CancellationToken.None);

if (versions.Single().ToNormalizedString() != "1.0.0")
    throw new InvalidOperationException("The generated package was not visible.");
```

`AddAsync` bypasses HTTP upload but uses the host-scoped publication path. A
duplicate normalized identity throws `DuplicatePackageException`.

## Prove parallel isolation

<!-- example-id: user-03-parallel-isolation; evidence: executable -->
```csharp
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

await using var first = await NuGetTestServerHost.StartAsync();
await using var second = await NuGetTestServerHost.StartAsync();

await first.Packages.AddAsync(
    TestPackageBuilder.Create("Docs.Only.First", "1.0.0").Build());

if (first.Port == second.Port)
    throw new InvalidOperationException("Hosts shared a port.");
if (await second.Packages.FindAsync("Docs.Only.First", "1.0.0") is not null)
    throw new InvalidOperationException("Package state leaked between hosts.");
```

“Network-independent” means no external feed dependency, not an in-process HTTP
mock. Dispose hosts with `await using` or `DisposeAsync`; disposal stops Kestrel
and releases host resources. `ResetAsync()` clears packages, faults, and recorded
requests without replacing the host.

The builder supports package metadata and text/binary files, including authors,
description, summary, title, project URL, readme, icon, license, package type,
repository, tags, and ungrouped dependencies. It does not provide a
target-framework dependency-group builder.

**Previous:** [NuGet package workflows](02-package-workflows.md)  
**Next:** [Authentication and production-safe configuration](04-authentication-and-production.md)
