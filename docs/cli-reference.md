# CLI reference

The CLI tool is `nuget-test-server` (project `src/NuGet.TestServer.Cli`). From the
repository it can be run with:

```powershell
dotnet run --project .\src\NuGet.TestServer.Cli -- <command> [options]
```

## Commands

| Command | Purpose |
| --- | --- |
| `start` | Start the server. Used when no other command is supplied. |
| `backup` | Create an offline ZIP backup of a storage root. |
| `restore` | Restore a storage root from a ZIP backup. |

## `start`

### Server options

| Option | Argument | Default | Description |
| --- | --- | --- | --- |
| `--production` | flag | off | Run in production-safe mode: no test control surface, stricter access policy. |
| `--port` | `<port>` | `0` (an available port) | HTTP listening port. |
| `--url` | `<url>` | `http://127.0.0.1:{port}` | Listener base URL. |
| `--data` | `<directory>` | none | Seed every `.nupkg` in the directory at startup. |
| `--storage` | `<directory>` | Local application data root | Storage root for packages and persisted state. |
| `--trusted-proxy` | `<addresses>` | none | Comma-separated trusted reverse-proxy IP addresses. |

### Package transfer limits

| Option | Argument | Default | Description |
| --- | --- | --- | --- |
| `--max-request-bytes` | `<bytes>` | 128 MiB | Maximum HTTP request body size. |
| `--max-package-bytes` | `<bytes>` | 100 MiB | Maximum compressed package size. |
| `--max-archive-entries` | `<count>` | 10,000 | Maximum entries in a `.nupkg`. |
| `--max-entry-bytes` | `<bytes>` | 64 MiB | Maximum expanded size of one archive entry. |
| `--max-expanded-bytes` | `<bytes>` | 512 MiB | Maximum total expanded archive content. |

All limit values must be greater than zero. See
[Configuration and storage](configuration.md#package-resource-limits).

### Extension options

| Option | Argument | Repeatable | Description |
| --- | --- | --- | --- |
| `--extension-root` | `<directory>` | yes | Directory containing installed extension `.nupkg` files. |
| `--extension-trust-root` | `<file.json>` | yes | Trust-root JSON file for extension attestation signatures. |
| `--extension-grant` | `<capability>` | yes | Grant a capability to installed extensions, for example `extension-state.read`. |

Paths are platform-native; a single argument is never split on `;`, `:`, or `,`.
See [Loading trusted extensions](extensions/README.md#load-trusted-in-process-extensions).

### Authentication options

| Option | Argument | Description |
| --- | --- | --- |
| `--username` | `<username>` | Basic authentication user name. |
| `--password` | `<password>` | Literal password. Emits a process-listing warning. |
| `--password-env` | `<variable>` | Read the password from an environment variable. |
| `--password-stdin` | flag | Read the password from standard input. |
| `--api-key` | `<key>` | Literal API key. Emits a process-listing warning. |
| `--api-key-env` | `<variable>` | Read the API key from an environment variable. |
| `--api-key-stdin` | flag | Read the API key from standard input. |
| `--generate-api-key` | flag | Generate a high-entropy runtime key and print it once. |
| `--identity-config` | `<json>` | Literal production identity configuration. Emits a warning. |
| `--identity-config-env` | `<variable>` | Read identity configuration from an environment variable. |
| `--identity-config-stdin` | flag | Read identity configuration from standard input. |

Password options are mutually exclusive, API-key options are mutually exclusive,
and identity-configuration options are mutually exclusive with each other and with
`--username`, `--password*`, and `--api-key*`. When `--username` is supplied without
a password option in an interactive terminal, the CLI prompts for the password
without echoing it.

See [Authentication](authentication.md) for feed behavior per credential combination.

## `backup`

| Option | Argument | Required | Description |
| --- | --- | --- | --- |
| `--storage` | `<directory>` | no | Storage root to capture. Defaults to the standard root. |
| `--output` | `<archive.zip>` | yes | Destination archive path. |

```powershell
nuget-test-server backup `
  --storage C:\NuGetTestServer\data `
  --output C:\Backups\nuget-test-server-2026-08-18.zip
```

## `restore`

| Option | Argument | Required | Description |
| --- | --- | --- | --- |
| `--input` | `<archive.zip>` | yes | Archive to restore. |
| `--storage` | `<directory>` | no | Destination storage root; must not already contain package or state trees. |

```powershell
nuget-test-server restore `
  --input C:\Backups\nuget-test-server-2026-08-18.zip `
  --storage C:\NuGetTestServer\recovered
```

Restore also accepts the extension options above when the archive contains
required extension state. See [Back up and restore](operations.md#back-up-and-restore).
