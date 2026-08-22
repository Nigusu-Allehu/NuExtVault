# NuGet Test Server documentation

A lightweight local NuGet V3 server for deterministic integration and end-to-end
testing.

## Start here

| Page | Contents |
| --- | --- |
| [Getting started](getting-started.md) | Requirements, build and test, starting the server, installing the CLI tool, configuring NuGet. |
| [Examples](examples.md) | End-to-end scenarios for restore, publish, faults, auth, moderation, and backup. |

## Using the server

| Page | Contents |
| --- | --- |
| [CLI reference](cli-reference.md) | Every command and option of `nuget-test-server`. |
| [Configuration and storage](configuration.md) | Transfer limits, storage layout, runtime state bounds, production-safe mode. |
| [Authentication](authentication.md) | Anonymous, API key, Basic, combined, and scoped production identities. |
| [Working with packages](packages.md) | Pushing, seeding, generating packages, metadata, validation and moderation. |
| [Control API](control-api.md) | The test-only `/__test` surface: state, reset, unlist, fault injection. |
| [NuGet protocol support](nuget-protocol.md) | Supported operations, V3 capability matrix, vulnerability auditing. |
| [Operate and deploy](operations.md) | Health probes, logs, metrics, container image, service hosting, backup and restore. |

## Reference

| Page | Contents |
| --- | --- |
| [Programmatic API](api/hosting.md) | `NuGetTestServerHost`, control clients, `TestPackageBuilder`, configuration types. |
| [Extensions](extensions/README.md) | Loading trusted extensions and granting capabilities. |
| [Extension SDK reference](extensions/sdk-reference.md) | Public contracts for building an extension. |
| [Package Staging extension](extensions/package-staging.md) | The optional staging feature and its routes. |
| [Architecture](architecture.md) | Repository layout, capability broker, service-index composition, state store. |
| [Limitations](limitations.md) | What this server intentionally does not do. |

Published design documents live in [`design/`](../design/README.md); the
contribution workflow is described in [`AGENTS.md`](../AGENTS.md).
