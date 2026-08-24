# 8. Build, test, and release

Use .NET SDK 10.0 from the repository root. `NuGet.TestServer.slnx` contains all
production, package, fixture, SDK-test, unit-test, and functional-test projects.

## Canonical validation

<!-- example-id: contrib-08-validation-commands; evidence: reference -->
```text
dotnet restore NuGet.TestServer.slnx
dotnet build NuGet.TestServer.slnx --no-restore --configuration Release --warnaserror
dotnet test NuGet.TestServer.slnx --no-restore --no-build --configuration Release
```

Run the smallest relevant test first, then the full SDK, unit, and functional
suites. The functional suite exercises real loopback Kestrel, NuGet.Protocol,
`dotnet restore`, `dotnet nuget push`, the packed CLI, trusted loading, and
Package Staging. Compatibility-sensitive changes must run
[`ProtocolCompatibilityBaselineTests`](../../tests/NuGet.TestServer.FunctionalTests/ProtocolCompatibilityBaselineTests.cs)
and the affected real-client scenarios.

The repository has no tracked `.editorconfig`, `Directory.Build.props`, or
formatting CI job. Match existing C# conventions and inspect `dotnet format`
output if used; do not claim it is an enforced repository command.

## Debugging

Use the [user manual quick
start](../user/01-installation-and-quick-start.md) with an explicit port and
unique storage directory. Probe `/health/live`, `/health/ready`, and
`/v3/index.json`. Rerun a failing class or fully qualified test and retain
temporary state only long enough to diagnose it. Never share one durable storage
root between concurrent processes.

## Performance and compatibility

[`ScalabilityCharacterizationTests`](../../tests/NuGet.TestServer.UnitTests/ScalabilityCharacterizationTests.cs)
run only when `NUGET_TESTSERVER_RUN_PERF=1`. They characterize gateway overhead,
allocations, embedded startup, 100 parallel hosts, readiness, audit cost, and
catalog sizes. Compare only equivalent runtime, OS, architecture, configuration,
and methodology. The checked-in Step 11D JSON is historical evidence, not a
portable SLA.

Snapshot updates are compatibility decisions. Review operation, route, resource,
capability, SDK API, canonical manifest, and structural snapshots deliberately.

## Packaging and consumer smoke

Release-pack these projects when their surfaces are affected:

- `src\NuGet.TestServer.Extensions.Sdk`;
- `src\NuGet.TestServer.Extensions.TestKit`;
- `tests\NuGet.TestServer.SdkFixture`;
- `src\NuGet.TestServer.Cli`;
- `src\NuGet.TestServer.Extensions.PackageStaging`.

Use `--configuration Release -p:TreatWarningsAsErrors=true --output <directory>`.
[`PackagingContractTests`](../../tests/NuGet.TestServer.Extensions.Sdk.Tests/PackagingContractTests.cs)
inspect SDK/TestKit package contents and pack the independent consumer.
`DocumentationExampleTests` installs and exercises the CLI tool from a local
package source, while
[`CommandLineEndToEndTests`](../../tests/NuGet.TestServer.FunctionalTests/CommandLineEndToEndTests.cs)
exercise the built CLI. Package Staging tests assemble loading
metadata and an ephemeral ES256 attestation, then perform real-host installation
and publication smoke.

Packing produces local artifacts only. A raw Package Staging `.nupkg` is not
loader-ready without package metadata and a trusted attestation. No external
publication workflow or production signing service exists.

## Cross-platform CI

[`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) restores, performs a
warning-as-error Release build, and tests the full solution on
`windows-latest`, `ubuntu-latest`, and `macos-latest`. Code changes covered by
that workflow should keep all three legs green. Avoid shell-only setup, path
separator, filename-casing, and locked-file assumptions.

The full solution includes SDK/TestKit packaging contracts, the documentation
CLI pack/install example, and Package Staging loading/functional smoke, so those
documented checks run in the current CI matrix. External publication, production
signing, and release automation remain absent. Cross-platform documentation CI
automation and reporting are tracked separately in issue #97; this change is
validated locally and does not claim a new CI gate.

## Review, rollback, and release

Review scope, proposal approval, tests-first evidence, one-owner invariants,
route freezing, authority boundaries, capability bounds, public version changes,
compatibility, failure/restart behavior, and rollback. Architecture fitness
tests support—not replace—review.

Normal code-only rollback reverts the PR. Optional external extensions can be
removed from configured roots and the host restarted. State or backup changes
require a tested backup, previous executable/image, clean restore target, and
explicit downgrade behavior.

The repository currently has no tracked package-publication or release workflow.
Supported release preparation ends at reviewed, locally packed artifacts plus
the CI evidence applicable to the change. First external publication requires a
separate approved design for coordinated versions, feed ownership, signing,
provenance, secrets, support-window activation, smoke tests, and
rollback/unlisting.

---

[Contributor manual](README.md) | **Previous:** [Development workflow](07-development-workflow.md) | **Next:** [Index](README.md)
