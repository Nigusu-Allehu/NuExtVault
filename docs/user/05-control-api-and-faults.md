# 5. Control API, request inspection, and deterministic faults

[User manual](README.md)

`/__test` controls exist only in embedded and standard test profiles, are not
advertised in the NuGet service index, and are omitted in production. Only
`GET /__test/health` remains in production.

## Routes

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/__test/state` | Counts and configured capacities |
| `POST` | `/__test/reset` | Clear packages, faults, and requests |
| `GET`, `POST` | `/__test/packages` | List or upload packages |
| `DELETE` | `/__test/packages/{id}/{version}` | Hard-delete a test package |
| `POST` | `/__test/packages/{id}/{version}/list` | Relist |
| `POST` | `/__test/packages/{id}/{version}/unlist` | Unlist |
| `PUT` | `/__test/packages/{id}/{version}/metadata` | Update metadata |
| `GET`, `DELETE` | `/__test/requests` | Inspect or clear request history |
| `GET`, `POST`, `DELETE` | `/__test/faults` | Inspect, add, or clear faults |

Reset and both clear operations return `204`.

## Inject two deterministic failures

<!-- example-id: user-05-fault-rule; evidence: executable -->
```powershell
$rule = @{
  id = "docs-05-fail-index-twice"
  method = "GET"
  pathContains = "/v3/index.json"
  statusCode = 503
  remainingMatches = 2
  delay = "00:00:00"
} | ConvertTo-Json
$null = Invoke-RestMethod "{{BASE_URL}}/__test/faults" -Method Post -ContentType "application/json" -Body $rule
1..3 | ForEach-Object {
  (Invoke-WebRequest "{{BASE_URL}}/v3/index.json" -SkipHttpErrorCheck).StatusCode
}
```

<!-- example-id: user-05-fault-output; evidence: reference -->
```text
503
503
200
```

Method and `pathContains` matching are case-insensitive. A null method or path is
a wildcard; matching uses the URL path, not query text. Test-control requests
never consume faults. Overlapping rules are selected by ordinal rule ID. Each
match atomically decrements `remainingMatches`, and exhausted rules remain listed.
IDs are unique case-insensitively. Duplicate IDs and capacity overflow return
`409`.

## Inspect recorded evidence

<!-- example-id: user-05-request-history; evidence: executable -->
```powershell
Invoke-RestMethod "{{BASE_URL}}/__test/requests" |
  Where-Object path -eq "/v3/index.json" |
  Select-Object method,path,statusCode,faultRuleId
```

Records expose sequence, timestamp, method, path, status, duration, selected
fault ID, and authenticated user. Timestamps, durations, and sequence values are
variable. Request bodies are never captured. Sensitive headers are redacted
internally; capture is bounded.

Request history retains the newest records by sequence (default 10,000) and
reports evictions. Fault capacity defaults to 100. Faults and history are
host-scoped and runtime-only.

## Cleanup

<!-- example-id: user-05-cleanup; evidence: executable -->
```powershell
Invoke-WebRequest "{{BASE_URL}}/__test/faults" -Method Delete
Invoke-WebRequest "{{BASE_URL}}/__test/requests" -Method Delete
```

`POST /__test/reset` is more destructive because it also deletes all packages.
Faults short-circuit before authentication and body binding, so never expose a
test profile to untrusted networks.

**Previous:** [Authentication and production-safe configuration](04-authentication-and-production.md)  
**Next:** [Operations and recovery](06-operations-and-recovery.md)
