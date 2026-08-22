# Programmatic API reference

These types are the supported public API for test authors. Reference
`src\NuGet.TestServer\NuGet.TestServer.csproj` from a test project to use them.

| Namespace | Contents |
| --- | --- |
| `NuGet.TestServer.Hosting` | `NuGetTestServerHost`, control clients, `ServerMode`, `RuntimeStateConfiguration` |
| `NuGet.TestServer.Packages` | `TestPackageBuilder`, `TestPackage`, `PackageTransferLimits`, `SupplyChainOptions`, `IPackagePolicyScanner` |
| `NuGet.TestServer.Authentication` | `AuthenticationConfiguration`, `ProductionSecurityConfiguration`, `SecurityAuditEvent` |
| `NuGet.TestServer.Faults` | `FaultRule` |
| `NuGet.TestServer.Requests` | `RequestRecord` |

## NuGetTestServerHost

`public sealed class NuGetTestServerHost : IAsyncDisposable`

Starts a real Kestrel server on a random loopback port with isolated in-memory
state (unless a storage directory is supplied).

### Properties

| Member | Type | Description |
| --- | --- | --- |
| `BaseUrl` | `Uri` | Root URL of the running server. |
| `ServiceIndexUrl` | `Uri` | The V3 service index (`/v3/index.json`). |
| `ControlUrl` | `Uri` | Base URL of the `/__test` [control API](../control-api.md). |
| `Port` | `int` | Listening port. |
| `HttpClient` | `HttpClient` | Client pre-configured for `BaseUrl`. |
| `Packages` | `PackageControlClient` | Package seeding and lookup. |
| `Faults` | `FaultControlClient` | Fault-rule configuration. |
| `Requests` | `RequestControlClient` | Recorded request history. |
| `SecurityAudits` | `IReadOnlyList<SecurityAuditEvent>` | Authentication, authorization, throttling, and ownership events. |

### Lifetime

```csharp
public async Task ResetAsync(CancellationToken token = default)
public async ValueTask DisposeAsync()
```

`ResetAsync` clears packages, fault rules, and request history. Always dispose the
host; `await using` is the simplest form.

### Start overloads

Every overload returns `Task<NuGetTestServerHost>` and accepts a trailing
`CancellationToken token = default`.

| Signature | Behavior |
| --- | --- |
| `StartAsync()` | Anonymous test-mode server with default limits. |
| `StartAsync(AuthenticationConfiguration authentication)` | Applies the credential policy. |
| `StartAsync(PackageTransferLimits packageLimits)` | Custom transfer limits. |
| `StartAsync(RuntimeStateConfiguration runtimeState)` | Custom request-history and fault capacities. |
| `StartAsync(VulnerabilitySnapshot vulnerabilities)` | Serves a specific vulnerability snapshot. |
| `StartAsync(SupplyChainOptions supplyChain, IPackagePolicyScanner? scanner = null)` | Custom publication policy and scanner. |
| `StartAsync(string storageDirectory, PackageTransferLimits packageLimits)` | Persistent storage instead of in-memory state. |
| `StartAsync(AuthenticationConfiguration authentication, VulnerabilitySnapshot vulnerabilities)` | Combines both. |
| `StartAsync(AuthenticationConfiguration authentication, VulnerabilitySnapshot vulnerabilities, PackageTransferLimits packageLimits)` | Combines all three. |
| `StartAsync(AuthenticationConfiguration authentication, VulnerabilitySnapshot vulnerabilities, PackageTransferLimits packageLimits, RuntimeStateConfiguration runtimeState)` | Full test-mode configuration. |
| `StartAsync(ServerMode mode, AuthenticationConfiguration authentication)` | Selects test or production behavior. |
| `StartAsync(ServerMode mode, AuthenticationConfiguration authentication, string storageDirectory)` | Mode plus persistent storage. |
| `StartAsync(ServerMode mode, AuthenticationConfiguration authentication, VulnerabilitySnapshot vulnerabilities)` | Mode plus snapshot. |
| `StartAsync(ServerMode mode, AuthenticationConfiguration authentication, VulnerabilitySnapshot vulnerabilities, PackageTransferLimits packageLimits)` | Mode, snapshot, and limits. |
| `StartAsync(ServerMode mode, AuthenticationConfiguration authentication, VulnerabilitySnapshot vulnerabilities, PackageTransferLimits packageLimits, RuntimeStateConfiguration runtimeState)` | Full configuration. |
| `StartProductionAsync(ProductionSecurityConfiguration security, int maximumAuthenticationFailures = 5)` | Production-safe server with scoped identities. |

Programmatic servers always use the embedded vulnerability snapshot and never
refresh from the network, preserving deterministic test behavior.

## PackageControlClient

`server.Packages`

```csharp
ValueTask AddAsync(TestPackage package, CancellationToken token = default)
ValueTask<TestPackage?> FindAsync(string id, string version, CancellationToken token = default)
ValueTask<byte[]?> FindSymbolAsync(string id, string version, CancellationToken token = default)
ValueTask ResetAsync(CancellationToken token = default)
```

Packages added through this client are trusted seeds: they are recorded as
published without protocol validation.

## FaultControlClient

`server.Faults`

```csharp
ValueTask AddAsync(FaultRule rule, CancellationToken token = default)
void Reset()
```

Adding a rule when the configured fault capacity is full fails; the equivalent
HTTP request returns `409 Conflict`.

## RequestControlClient

`server.Requests`

```csharp
ValueTask<IReadOnlyList<RequestRecord>> GetAsync(CancellationToken token = default)
void Reset()
```

## FaultRule

```csharp
public sealed record FaultRule(
    string Id,
    string? Method,
    string? PathContains,
    HttpStatusCode StatusCode,
    int RemainingMatches,
    TimeSpan Delay);
```

A `null` `Method` or `PathContains` matches any value. `RemainingMatches` counts
down per matched request. See [fault injection](../control-api.md#inject-deterministic-failures).

## RequestRecord

```csharp
public sealed record RequestRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    string Method,
    string Path,
    int StatusCode,
    long DurationMilliseconds,
    string? FaultRuleId,
    string? AuthenticatedUser);
```

Bodies are never recorded and sensitive headers are redacted.

## TestPackageBuilder

`TestPackageBuilder.Create(string id, string version)` returns a fluent builder;
`Build()` produces a `TestPackage` containing a real `.nupkg` archive.

| Method | Purpose |
| --- | --- |
| `WithDescription(string description)` | Package description. |
| `WithAuthors(string authors)` | Authors field. |
| `WithSummary(string summary)` | Summary field. |
| `WithTitle(string title)` | Display title. |
| `WithProjectUrl(string projectUrl)` | Project URL. |
| `WithTags(string tags)` | Space- or semicolon-separated tags. |
| `WithReadme(string path, string content)` | Embedded readme file. |
| `WithIcon(string path, byte[] content)` | Embedded icon file. |
| `WithLicenseExpression(string expression)` | SPDX license expression. |
| `WithLicenseFile(string path, string content)` | Embedded license file. |
| `WithPackageType(string name, string version = "")` | Declared package type. |
| `WithRepository(...)` | Repository metadata. |
| `WithDependency(string id, string versionRange)` | Dependency entry. |
| `WithFile(string path, string content)` / `WithFile(string path, byte[] content)` | Arbitrary archive content. |
| `Build()` | Produces the `TestPackage`. |

## PackageTransferLimits

`public sealed record PackageTransferLimits`

| Member | Default |
| --- | ---: |
| `MaxRequestBodyBytes` | 128 MiB |
| `MaxPackageBytes` | 100 MiB |
| `MaxArchiveEntries` | 10,000 |
| `MaxArchiveEntryBytes` | 64 MiB |
| `MaxExpandedArchiveBytes` | 512 MiB |
| `TemporaryDirectory` | Process temporary directory |

`PackageTransferLimits.Default` exposes the defaults, and `Validate()` rejects
non-positive values.

```csharp
var limits = new PackageTransferLimits
{
    MaxRequestBodyBytes = 16 * 1024 * 1024,
    MaxPackageBytes = 12 * 1024 * 1024,
    MaxArchiveEntries = 1000,
    MaxArchiveEntryBytes = 8 * 1024 * 1024,
    MaxExpandedArchiveBytes = 64 * 1024 * 1024
};

await using var server = await NuGetTestServerHost.StartAsync(limits);
```

## RuntimeStateConfiguration

`public sealed class RuntimeStateConfiguration`

| Member | Default |
| --- | ---: |
| `RequestHistoryCapacity` | 10,000 |
| `FaultRuleCapacity` | 100 |

```csharp
await using var server = await NuGetTestServerHost.StartAsync(
    new RuntimeStateConfiguration(
        requestHistoryCapacity: 2000,
        faultRuleCapacity: 25));
```

## AuthenticationConfiguration

`public sealed class AuthenticationConfiguration`

| Member | Description |
| --- | --- |
| `AuthenticationConfiguration.Anonymous` | No credentials required. |
| `Create(string? username, string? password, string? apiKey)` | Infers the feed profile from the supplied credentials. |
| `CreateProduction(ProductionSecurityConfiguration security)` | Scope-based production identities. |
| `Profile` | The resolved `AuthenticationProfile`. |
| `RequiresBasicAuthentication`, `RequiresApiKeyForWrites` | Effective policy flags. |

Invalid combinations throw `AuthenticationConfigurationException`. See
[Authentication](../authentication.md) for the behavior of each combination.

## Supply-chain policy types

| Type | Purpose |
| --- | --- |
| `SupplyChainOptions` | Required signatures, per-identity and per-repository quotas, reserved namespaces. |
| `IPackagePolicyScanner` | Custom scan implementation invoked before publication. |
| `PackageScanResult` / `PackageScanOutcome` | Scan verdict (`Clean`, `Malicious`, `Inconclusive`). |
| `SafePackagePolicyScanner` | Default structural scanner. |
| `DeterministicPackagePolicyScanner` | Fixed per-package verdicts for tests. |

See [Package validation and moderation](../packages.md#validation-and-moderation).
