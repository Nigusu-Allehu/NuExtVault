# 8. Build, test, and release

Use .NET SDK 10.0 from the repository root. `NuExtVault.slnx` contains all
production, package, fixture, SDK-test, unit-test, and functional-test projects.

## Canonical validation

<!-- example-id: contrib-08-validation-commands; evidence: reference -->
```text
dotnet restore NuExtVault.slnx
dotnet build NuExtVault.slnx --no-restore --configuration Release --warnaserror
dotnet test NuExtVault.slnx --no-restore --no-build --configuration Release
```

Run the smallest relevant test first, then the full SDK, unit, and functional
suites. The functional suite exercises real loopback Kestrel, NuGet.Protocol,
`dotnet restore`, `dotnet nuget push`, the packed CLI, trusted loading, and
Package Staging. Compatibility-sensitive changes must run
[`ProtocolCompatibilityBaselineTests`](../../tests/NuExtVault.FunctionalTests/ProtocolCompatibilityBaselineTests.cs)
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

[`ScalabilityCharacterizationTests`](../../tests/NuExtVault.UnitTests/ScalabilityCharacterizationTests.cs)
run only when `NUEXTVAULT_RUN_PERF=1`. They characterize gateway overhead,
allocations, embedded startup, 100 parallel hosts, readiness, audit cost, and
catalog sizes. Compare only equivalent runtime, OS, architecture, configuration,
and methodology. The checked-in Step 11D JSON is historical evidence, not a
portable SLA.

Snapshot updates are compatibility decisions. Review operation, route, resource,
capability, SDK API, canonical manifest, and structural snapshots deliberately.

## Packaging and consumer smoke

Only `src\NuExtVault.Cli` is released publicly. It produces the .NET 10 global
tool package `NuExtVault` and the command `nuextvault`. The SDK, TestKit,
official extensions, Package Staging, and fixtures remain unpublished; their
local package tests continue to validate extension boundaries.

Use these commands to exercise the exact tool package from an isolated local
feed:

<!-- example-id: contrib-08-local-tool-pack-install; evidence: executable -->
```powershell
dotnet pack .\src\NuExtVault.Cli\NuExtVault.Cli.csproj --configuration Release -p:TreatWarningsAsErrors=true --output "{{ARTIFACTS}}"
@"
<configuration>
  <packageSources>
    <clear />
    <add key="ExactPackage" value="{{ARTIFACTS}}" />
  </packageSources>
</configuration>
"@ | Set-Content "{{NUGET_CONFIG}}"
dotnet tool install --tool-path "{{TOOLS}}" NuExtVault --configfile "{{NUGET_CONFIG}}" --version 1.0.0 --no-cache
```

For a faster contributor loop without packaging, run
`dotnet run --project .\src\NuExtVault.Cli -- start`.

[`PackagingContractTests`](../../tests/NuExtVault.Extensions.Sdk.Tests/PackagingContractTests.cs)
inspect SDK/TestKit package contents and pack the independent consumer.
`DocumentationExampleTests` installs and exercises the CLI tool from a local
package source, while
[`CommandLineEndToEndTests`](../../tests/NuExtVault.FunctionalTests/CommandLineEndToEndTests.cs)
exercise the built CLI. Package Staging tests assemble loading
metadata and an ephemeral ES256 attestation, then perform real-host installation
and publication smoke.

Local packing produces local artifacts only. A raw Package Staging `.nupkg` is not
loader-ready without package metadata and a trusted attestation. No external
publication includes those extension packages.

## Cross-platform CI

[`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) restores, performs a
warning-as-error Release build, and tests the solution on `windows-latest`,
`ubuntu-latest`, and `macos-latest`. It excludes
`DocumentationContractTests` and `DocumentationExampleTests`, which have one
owner in the dedicated
[documentation workflow](../../.github/workflows/documentation.yml). That
workflow restores and builds the functional-test project with warnings as
errors, then validates both manuals and their examples on the same three
platforms. Both workflows upload failed TRX results.

The general matrix retains SDK/TestKit packaging contracts and Package Staging
loading/functional smoke. The documentation matrix owns link/navigation,
stable-ID, evidence/drift, SDK compilation, root quick-start, and CLI
pack/install checks. Avoid shell-only setup, path separator, filename-casing,
and locked-file assumptions. Hosted cross-platform checks remain required before the first live release.

## Review, rollback, and release

Review scope, proposal approval, tests-first evidence, one-owner invariants,
route freezing, authority boundaries, capability bounds, public version changes,
compatibility, failure/restart behavior, and rollback. Architecture fitness
tests support—not replace—review.

Normal code-only rollback reverts the PR. Optional external extensions can be
removed from configured roots and the host restarted. State or backup changes
require a tested backup, previous executable/image, clean restore target, and
explicit downgrade behavior.

`.github/workflows/release.yml` runs only for `v*` tags or manual dispatch. It
restores, performs a warning-as-error Release build, runs the full solution,
packs once, verifies that the requested version exactly matches the project
version, inspects and installs the exact package from an isolated feed, starts
real Kestrel, probes readiness, uninstalls the tool, and uploads the validated
package. Its publish job uses the protected `nuget.org` environment and exchanges
GitHub OIDC for a short-lived credential through `NuGet/login@v1`; no long-lived
NuGet API key is stored.

Every action in the release workflow is pinned to a reviewed full commit SHA;
the adjacent version comment records the corresponding upstream release.
Dependabot checks GitHub Actions weekly and groups action updates into a review
PR. Review upstream release notes and the old-to-new commit diff, then rerun the
publication workflow contracts and local package smoke before accepting a pin
update. Never replace a release-workflow SHA with a mutable tag.

Before the first release, an administrator must create the `nuget.org` GitHub
environment with required reviewers and prevent self-review where the repository
plan supports it. Restrict environment deployment refs to the `main` branch and
protected `v*` tags. Add the `NUGET_USER` environment secret containing the
NuGet.org profile name, and register a NuGet.org Trusted Publishing policy for
owner `Nigusu-Allehu`, repository `NuExtVault`, workflow file `release.yml`, and
environment `nuget.org`. Review repository history and complete Windows, Ubuntu,
and macOS checks before creating `v1.0.0` or manually dispatching version
`1.0.0` from `main`.

The workflow independently fails closed before packaging: manual dispatch is
accepted only from `refs/heads/main`, while a tag release fetches `origin/main`
and proves that the tagged commit is contained in it. The OIDC-capable publish
job requires the resulting verified output in addition to environment approval.

NuGet.org versions are immutable. A bad release cannot be overwritten: stop
further releases, unlist the affected version on NuGet.org, publish a corrected
new version, and document the impact. Reverting this repository or deleting a
Git tag does not remove an already published package.

---

[Contributor manual](README.md) | **Previous:** [Development workflow](07-development-workflow.md) | **Next:** [Index](README.md)
