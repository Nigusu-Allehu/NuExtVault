# NuExtVault contributor manual

This manual describes the current repository at Microkernel Step 22. Use compiled
source and executable tests as the authority for implemented behavior. The
[public SDK policy](../../design/public-extension-sdk-v1.md) defines supported
extension contracts. The architecture, migration plan, and implementation review
under [`design/`](../../design/README.md) explain rationale and history; they are
not additional APIs.

For installation and product usage, use the [user manual](../user/README.md).

## Chapters

1. [Architecture and compiled assemblies](01-architecture-and-assemblies.md)
2. [Request lifecycle](02-request-lifecycle.md)
3. [Extension composition](03-extension-composition.md)
4. [Capabilities and security](04-capabilities-and-security.md)
5. [Transactional state, backup, and recovery](05-state-backup-and-recovery.md)
6. [Public SDK and trusted loading](06-public-sdk-and-trusted-loading.md)
7. [Development workflow](07-development-workflow.md)
8. [Build, test, and release workflow](08-build-test-and-release.md)

## Evidence conventions

Each chapter labels public contracts, internal implementation, historical
rationale, and deferred behavior. Nontrivial claims link to source, tests,
structural snapshots, or an approved design section. Fenced examples have stable
IDs and are either executed unchanged with ephemeral substitutions or asserted
structurally by `DocumentationContractTests` and `DocumentationExampleTests`.
Executable examples run on Windows, Ubuntu, and macOS. Reference examples are
intentionally non-executable structural evidence; the manual currently has no
platform-specific examples.

Sidecars, network-feed discovery, dynamic extension reload, in-process sandboxing,
distributed operation, and external package publication are not implemented.

Start with [Chapter 1](01-architecture-and-assemblies.md).
