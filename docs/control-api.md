# Control API

The `/__test` control endpoints are test-only and are never advertised in the
NuGet service index. [Production-safe mode](configuration.md#production-safe-mode)
maps only `/__test/health`; all control endpoints below are absent.

`POST /__test/packages` accepts `application/octet-stream` for memory-safe
package uploads. The existing JSON `{ "content": "<base64>" }` format remains
available for compatibility and is limited to 4 MiB of decoded package content;
use the binary format for larger packages.

## Authentication

When authentication is configured, the control API uses the same write policy:

- API-key feeds require `X-NuGet-ApiKey`.
- Private feeds require Basic authentication.
- Private feeds with a separate publishing key require both.
- `GET /__test/health` always remains anonymous.

For an API-key feed, add the header to `curl` examples:

```powershell
curl http://127.0.0.1:54321/__test/state `
  -H "X-NuGet-ApiKey: $env:NUGET_TEST_SERVER_API_KEY"
```

For a private feed, use `curl -u test-user:test-password`.

## Endpoints

| Endpoint | Purpose |
| --- | --- |
| `GET /__test/health` | Server mode and health. Always anonymous. |
| `GET /__test/state` | Request and fault counters and capacities. |
| `GET /__test/packages` | Packages currently in the active store. |
| `POST /__test/packages` | Seed a package from a binary or base64 body. |
| `PUT /__test/packages/{id}/{version}/metadata` | Set repository-owned metadata. |
| `POST /__test/packages/{id}/{version}/unlist` | Unlist a version. |
| `POST /__test/packages/{id}/{version}/list` | Relist a version. |
| `DELETE /__test/packages/{id}/{version}` | Hard-delete a version from the active store. |
| `GET /__test/requests` | Recorded request history. |
| `DELETE /__test/requests` | Clear recorded request history. |
| `GET /__test/faults` | Configured fault rules. |
| `POST /__test/faults` | Add a fault rule. |
| `DELETE /__test/faults` | Clear all fault rules. |
| `POST /__test/reset` | Reset packages, faults, and request history. |

## Inspect state

```powershell
curl http://127.0.0.1:54321/__test/state
curl http://127.0.0.1:54321/__test/packages
curl http://127.0.0.1:54321/__test/requests
curl http://127.0.0.1:54321/__test/faults
```

## Reset the server

Reset packages, faults, and request history:

```powershell
curl -X POST http://127.0.0.1:54321/__test/reset
```

In an in-process test:

```csharp
await server.ResetAsync();
```

## Unlist, relist, or delete a package

```powershell
curl -X POST http://127.0.0.1:54321/__test/packages/Example.Package/1.0.0/unlist
curl -X POST http://127.0.0.1:54321/__test/packages/Example.Package/1.0.0/list
curl -X DELETE http://127.0.0.1:54321/__test/packages/Example.Package/1.0.0
```

The NuGet protocol `DELETE /package/{id}/{version}` unlists a package. The
control API `DELETE` removes it from the active store, including persisted CLI
storage.

## Inject deterministic failures

The following rule fails the next two matching package-download requests with
HTTP 503:

```powershell
curl -X POST http://127.0.0.1:54321/__test/faults `
  -H "Content-Type: application/json" `
  -d '{
    "id": "fail-download-twice",
    "method": "GET",
    "pathContains": "/flatcontainer/example.package/1.0.0/",
    "statusCode": 503,
    "remainingMatches": 2,
    "delay": "00:00:00"
  }'
```

The same behavior can be configured in process:

```csharp
using System.Net;
using NuGet.TestServer.Faults;

await server.Faults.AddAsync(new FaultRule(
    Id: "fail-download-twice",
    Method: "GET",
    PathContains: "/flatcontainer/example.package/1.0.0/",
    StatusCode: HttpStatusCode.ServiceUnavailable,
    RemainingMatches: 2,
    Delay: TimeSpan.Zero));
```

After exercising the client, inspect `server.Requests.GetAsync()` or
`GET /__test/requests` to verify attempts, response codes, durations, and matched
fault rules.

## Request pipeline order

The kernel gateway exclusively owns raw-request matching, delay, short-circuiting,
redaction, and capture. The request order is:

```text
Kestrel transport/body-size limits
  -> diagnostics/tracing
  -> raw method/path fault match, delay, and optional injected response
  -> redacted request capture
  -> authentication, authorization, and authentication-failure throttling
  -> endpoint binding and operation limits
  -> registry dispatch
  -> response
```

Matching occurs before authentication and binding so tests can deliberately inject
the same response for malformed or unauthorized requests without reading a request
body. Kestrel limits remain outermost, diagnostics observes injected responses, and
request cancellation aborts fault delays. Request history never stores bodies and
[redacts sensitive headers](configuration.md#runtime-request-and-fault-state).

The internal official `NuTest.Control` extension owns the authenticated package,
fault, request-history, and reset operations in embedded and standard profiles. It
can act only through host-scoped package-control and kernel-instrumentation
capabilities. Production profiles neither select the extension nor grant those
capabilities, and invalid production compositions are rejected at startup.
