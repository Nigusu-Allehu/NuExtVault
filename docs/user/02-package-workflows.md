# 2. NuGet package workflows

[User manual](README.md)

This chapter keeps the surfaces distinct: `nuget-test-server` starts and seeds
the server, `dotnet nuget` pushes packages, `dotnet restore` consumes packages,
and HTTP protocol/control routes change package state.

## Prerequisites

Complete [Chapter 1](01-installation-and-quick-start.md). Set `{{BASE_URL}}` to
the printed origin and use a test package at `{{PACKAGE_PATH}}`.

## Seed packages at startup

`--data` imports top-level `.nupkg` files only. Existing normalized identities
are skipped. Seeding is an administrator-controlled shortcut and does not pass
through the HTTP publication policy.

The executable example supervises the CLI process, verifies the seeded identity,
and always stops it.

<!-- example-id: user-02-seed; evidence: executable -->
```powershell
$server = Start-Process "{{TOOL_COMMAND}}" -ArgumentList @(
  "start", "--port", "{{PORT}}", "--storage", "{{STORAGE}}", "--data", "{{SEED_DIRECTORY}}"
) -PassThru
try {
  foreach ($attempt in 1..100) {
    try {
      $response = Invoke-WebRequest "{{BASE_URL}}/flatcontainer/docs.seeded/index.json"
      if ($response.StatusCode -eq 200) { break }
    } catch {
      Start-Sleep -Milliseconds 100
    }
  }
  if ($response.StatusCode -ne 200) { throw "The seeded package was not available." }
  $response.StatusCode
} finally {
  if (-not $server.HasExited) { Stop-Process -Id $server.Id }
  $server | Wait-Process
}
```

## Push with the .NET CLI

Create an isolated NuGet configuration whose source is the printed service index.

<!-- example-id: user-02-nuget-config; evidence: reference -->
```xml
<configuration>
  <packageSources>
    <clear />
    <add key="TestServer" value="{{BASE_URL}}/v3/index.json" allowInsecureConnections="true" />
  </packageSources>
</configuration>
```

<!-- example-id: user-02-push; evidence: executable -->
```powershell
dotnet nuget push "{{PACKAGE_PATH}}" --source TestServer --api-key "{{API_KEY}}" --configfile "{{NUGET_CONFIG}}"
```

A first accepted push returns `201 Created`. Retrying identical bytes for the
same identity is idempotent and returns `200 OK`; different bytes for that
identity return `409 Conflict`.

## Restore

<!-- example-id: user-02-restore; evidence: executable -->
```powershell
dotnet restore "{{PROJECT_PATH}}" --configfile "{{NUGET_CONFIG}}" --packages "{{PACKAGES_DIRECTORY}}" --no-cache
```

The tested workflow resolves dependency graphs through real NuGet V3 resources.
An exact unlisted version remains restorable.

## List and change visibility

Inventory is a test-control HTTP call, not a `nuget-test-server` CLI command:

<!-- example-id: user-02-list; evidence: executable -->
```powershell
Invoke-RestMethod "{{BASE_URL}}/__test/packages"
```

Each item contains `id`, normalized `version`, `listed`, and `published`.
Unlisting uses the NuGet package-management HTTP route:

<!-- example-id: user-02-unlist; evidence: executable -->
```powershell
Invoke-WebRequest "{{BASE_URL}}/package/NuTest.Docs.Workflow/1.0.0" -Method Delete
```

It returns `204`, removes the version from search, and keeps exact content,
version enumeration, registration (`listed: false`), symbols, and exact restore.
Relisting is a test-control operation:

<!-- example-id: user-02-relist; evidence: executable -->
```powershell
Invoke-WebRequest "{{BASE_URL}}/__test/packages/NuTest.Docs.Workflow/1.0.0/list" -Method Post
```

## Delete

Choose the operation deliberately:

| Surface | Operation | Meaning |
| --- | --- | --- |
| Test control | `DELETE /__test/packages/{id}/{version}` | Physically removes a test package |
| Moderation | `POST /__admin/packages/{id}/{version}/delete?reason=...` | Records an administrative tombstone |
| Production package route | `DELETE /package/{id}/{version}/hard` | Authorized hard deletion |

The test-control form is:

<!-- example-id: user-02-delete; evidence: executable -->
```powershell
Invoke-WebRequest "{{BASE_URL}}/__test/packages/NuTest.Docs.Workflow/1.0.0" -Method Delete
```

Successful mutations return `204`; missing test-control targets return `404`.
Unlisting is reversible, while deletion is destructive. Back up durable storage
before destructive administration; see [Chapter 6](06-operations-and-recovery.md).

## Cleanup and security

Stop the server, remove its temporary storage, project, package cache, and
NuGet configuration. Prefer environment or standard-input credential options in
shared environments; literal secrets can appear in process listings and logs.
Control routes are absent in production except health.

**Previous:** [Installation and quick start](01-installation-and-quick-start.md)  
**Next:** [Programmatic testing](03-programmatic-testing.md)
