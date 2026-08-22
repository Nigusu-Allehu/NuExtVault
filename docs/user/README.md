# NuTestServer user manual

This manual describes behavior implemented and tested in the current repository.
NuTestServer is a lightweight NuGet V3 server for deterministic integration and
end-to-end tests. It is not a hosted, multi-node package service.

## Prerequisites

- .NET SDK 10.0 or later.
- PowerShell 7 (`pwsh`) for the executable command examples.
- Windows, Ubuntu, or macOS.
- A repository checkout unless a chapter explicitly starts from an already packed
  local tool or trusted extension package.

## Chapters

1. [Installation and quick start](01-installation-and-quick-start.md)
2. [NuGet package workflows](02-package-workflows.md)
3. [Programmatic testing](03-programmatic-testing.md)
4. [Authentication and production-safe configuration](04-authentication-and-production.md)
5. [Control API, request inspection, and deterministic faults](05-control-api-and-faults.md)
6. [Operations and recovery](06-operations-and-recovery.md)
7. [Trusted extensions and Package Staging](07-trusted-extensions-and-package-staging.md)
8. [Troubleshooting, limits, and compatibility](08-troubleshooting-limits-and-compatibility.md)

## How examples are verified

Every fenced example has a stable ID and an evidence classification.
`evidence: executable` means tests extract and run the displayed text, replacing
only ephemeral ports, paths, and generated credentials. `evidence: reference`
means the block is intentionally non-executable and tests assert its exact
structure against the implemented contract. Link, navigation, and example-ID
checks run with the complete test suite on Windows, Ubuntu, and macOS.

Start with [Chapter 1](01-installation-and-quick-start.md).
