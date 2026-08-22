# Extensions

The server is a microkernel: the kernel owns hosting, routing, security, storage,
and state, while features are supplied by extensions that declare a strict v1
manifest and receive only explicitly granted capabilities.

- [Extension SDK reference](sdk-reference.md) — public contracts and how to build
  an extension.
- [Package Staging extension](package-staging.md) — the optional staging feature.
- [Architecture](../architecture.md) — how official extensions compose the built-in
  surface.

## Pack the public extension SDK locally

The `NuGet.TestServer.Extensions.Sdk` and `NuGet.TestServer.Extensions.TestKit`
package/assembly contracts are stable at v1. Both target `net10.0`; the SDK package
includes the strict v1 manifest schema.

```powershell
dotnet pack src\NuGet.TestServer.Extensions.Sdk\NuGet.TestServer.Extensions.Sdk.csproj --configuration Release -p:TreatWarningsAsErrors=true --output artifacts\sdk
dotnet pack src\NuGet.TestServer.Extensions.TestKit\NuGet.TestServer.Extensions.TestKit.csproj --configuration Release -p:TreatWarningsAsErrors=true --output artifacts\sdk
```

These are local packages only and are not published externally. Projects using the
former `NuGet.TestServer.Extensions.Abstractions` reference should migrate to the SDK
and strict manifest described in
[`design/public-extension-sdk-v1.md`](../../design/public-extension-sdk-v1.md).

## Load trusted in-process extensions

External discovery is disabled by default. The CLI accepts repeatable,
platform-native paths; it does not split one argument on `;`, `:`, or `,`:

```powershell
dotnet run --project .\src\NuGet.TestServer.Cli -- start `
  --extension-root C:\NuTestServer\extensions `
  --extension-trust-root C:\NuTestServer\trust\contoso.json
```

Each extension root contains installed `.nupkg` files directly. Every package must
contain these root files and its implementation under `lib/net10.0`:

```text
Contoso.NuTestServer.Flavors.1.2.3.nupkg
├── Contoso.NuTestServer.Flavors.nuspec
├── extension-manifest.json
├── extension-package.json
├── extension-attestation.json
└── lib/net10.0/Contoso.NuTestServer.Flavors.dll
```

`extension-package.json` names the exact entry assembly and `IExtensionModule`
implementation type, plus explicit half-open dependency ranges. A trust-root file is
strict JSON:

```json
{
  "publisher": "Contoso",
  "keyId": "contoso-extension-signing-2026",
  "algorithm": "ES256",
  "subjectPublicKeyInfoBase64": "<base64 DER SubjectPublicKeyInfo>"
}
```

Every configured package is required. Missing trust, malformed paths, invalid or
expired attestations, dependency/collision/capability failures, forbidden assembly
references, or activation failures prevent startup; there is no optional
success-shaped fallback. Packages are copied into bounded immutable staging,
validated before code execution, and loaded in dedicated collectible assembly-load
contexts. These contexts provide dependency and shutdown isolation, **not** a
security sandbox.

Installation, update, enablement, disablement, and unload require a restart. Route,
operation, resource, profile, and capability tables are frozen before listening.
Embedded and programmatic hosts remain network-independent and load no external
packages unless explicitly configured. To roll back, remove the extension-root
options and restart; the official extension bundle continues unchanged.

Manifest v1 service-resource declarations may carry an optional `routeId` reference.
An extension that declares more than one route must name the route that backs its
service resource; the kernel resolves the reference and projects the absolute URL. An
extension with exactly one route may omit the reference.

## Grant capabilities

Capabilities stay denied by default. Grant an installed extension the capabilities
its manifest requires with repeated `--extension-grant` options:

```powershell
dotnet run --project .\src\NuGet.TestServer.Cli -- start `
  --extension-root C:\NuTestServer\extensions `
  --extension-trust-root C:\NuTestServer\trust\contoso.json `
  --extension-grant extension-state.read `
  --extension-grant extension-state.write
```

An extension whose required capability is not granted fails startup instead of
running with reduced privileges.
