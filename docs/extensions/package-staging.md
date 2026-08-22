# Package Staging extension

`NuTest.PackageStaging` is an optional, independently packable extension in
`src/NuGet.TestServer.Extensions.PackageStaging`. It is absent from every default
profile; installing it requires an extension root, a trust root, and explicit grants
for `host.clock.read`, `extension-state.read`, `extension-state.write`,
`packages.content.write-staged`, and `publication.request`.

## Routes

Once installed it serves these administrator-only routes:

```text
PUT    /staging/groups/{groupId}
GET    /staging/groups
GET    /staging/groups/{groupId}
PUT    /staging/groups/{groupId}/packages
PUT    /staging/groups/{groupId}/packages/{packageId}/{version}/symbols
GET    /staging/groups/{groupId}/packages/{packageId}/{version}
POST   /staging/groups/{groupId}/packages/{packageId}/{version}/promote
POST   /staging/groups/{groupId}/packages/{packageId}/{version}/reject
POST   /staging/groups/{groupId}/expire
```

## Behavior

Uploads stream the request body straight into kernel-owned staged storage; the kernel
extracts and validates package and symbol identity and rejects malformed archives.
Staged packages are invisible to `/query`, `/registration`, and `/flatcontainer`
until promotion and visible immediately after it. Promotion runs through the kernel's
publication pipeline behind a recovery journal, so repeating a request with the same
`Idempotency-Key` replays the recorded result instead of publishing twice.
Expired staged leases are reclaimed while the host runs and before new quota
admission.

Every response carries a typed `outcome` string, for example:

```json
{ "outcome": "Succeeded", "packageId": "Contoso.Sample", "version": "1.2.3", "replayed": false, "detail": null }
```

## Backup and restore

Backups include staged bytes, extension state, and the publication journal.
Restore a backup containing required staging state with the same trusted package
configuration:

```powershell
nuget-test-server restore --input .\backup.zip --storage .\restored `
  --extension-root .\extensions `
  --extension-trust-root .\trust\nutest.json
```

See [Back up and restore](../operations.md#back-up-and-restore) for the general
procedure.
