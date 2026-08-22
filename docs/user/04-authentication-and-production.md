# 4. Authentication and production-safe configuration

[User manual](README.md)

NuTestServer is test infrastructure, not a hardened hosted feed. The default
test profile is anonymous and includes destructive test controls. Production
mode removes those controls and requires authentication configuration, but
operators still own network isolation, certificates, secrets, monitoring, and
backups.

## Authentication profiles

The implemented profiles support anonymous test use, an API key for publication,
Basic authentication for private reads, both Basic and API key, and scoped
production identities. Prefer environment or standard-input identity
configuration; literal secret arguments produce warnings because process
listings and logs may expose them.

This is a non-executable foreground-command reference. Functional tests supervise the CLI
directly so credentials and shutdown remain deterministic.

<!-- example-id: user-04-production-start; evidence: reference -->
```powershell
$env:NUTEST_IDENTITIES = "{{IDENTITY_JSON}}"
& "{{TOOL_COMMAND}}" start --production --identity-config-env NUTEST_IDENTITIES `
  --trusted-proxy 127.0.0.1 --port "{{PORT}}" --storage "{{STORAGE}}"
```

Scoped identities require `--production` and secure transport evidence. Direct
HTTPS uses normal Kestrel certificate configuration; NuTestServer has no
certificate-provisioning CLI. A reverse proxy is trusted only by exact configured
IP, and the immediate peer must supply one `X-Forwarded-Proto: https` value.
Proxy chains, hostnames, and CIDR trust are not supported.

## Authorization

Scopes are `read`, `publish`, `unlist`, `delete`, and `admin`. `admin` implies
all scopes and namespaces. Namespace rules are case-insensitive package-ID
prefixes; `*` matches all. Published-package ownership is persisted.

| Result | Meaning |
| --- | --- |
| `401 Unauthorized` | Missing or invalid credentials |
| `403 Forbidden` | Valid identity lacks scope, namespace, ownership, or admin permission |
| `426 Upgrade Required` | A production identity request cannot prove HTTPS |
| `429 Too Many Requests` | Authentication attempt limit reached; honor `Retry-After` |

The default failed-authentication limiter permits five failures/in-flight
attempts per validated client over one minute. These values are not CLI
configuration.

## Production route boundary

Production omits state, reset, package-control, request-recording, and
fault-injection routes, including `/__test/packages` and descendants.
`/__test/health`, `/health/live`, and `/health/ready` remain available.

<!-- example-id: user-04-production-route-check; evidence: executable -->
```powershell
$headers = @{ "X-Forwarded-Proto" = "https" }
(Invoke-WebRequest "{{BASE_URL}}/__test/state" -Headers $headers -SkipHttpErrorCheck).StatusCode
(Invoke-WebRequest "{{BASE_URL}}/health/ready" -Headers $headers -SkipHttpErrorCheck).StatusCode
```

Expected statuses are `404` and `200` for a healthy production host.

## Package limits

Malformed packages return `400`. Configured request, compressed-package,
entry-count, entry-size, and expanded-size violations return `413`; rejected or
cancelled uploads remove partial temporary files. See the complete table in
[Chapter 8](08-troubleshooting-limits-and-compatibility.md).

Clear the identity environment variable after stopping the example:

<!-- example-id: user-04-cleanup; evidence: executable -->
```powershell
Remove-Item Env:NUTEST_IDENTITIES -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force "{{STORAGE}}" -ErrorAction SilentlyContinue
```

**Previous:** [Programmatic testing](03-programmatic-testing.md)  
**Next:** [Control API and deterministic faults](05-control-api-and-faults.md)
