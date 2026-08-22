namespace NuGet.TestServer.ExternalExtensionTestKit;

/// <summary>
/// Step 20 tests-first red phase helper. Builds a minimal, schema-v1-valid
/// `extension-manifest.json` for negative-path tests that must fail before the
/// entry assembly is ever loaded (trust root, attestation, traversal, size and
/// count limits, identity collisions, dependency graph, and squatting checks).
/// The manifest intentionally declares no operations/routes/resources unless
/// requested, since those tests only need a well-formed, hashable manifest.
/// </summary>
public static class MinimalManifestJson
{
    public static byte[] Build(
        string id,
        string version = "1.0.0",
        string publisher = "Contoso",
        string? requiredCapability = null,
        string? routeId = null,
        string? routePath = null,
        string? operationId = null,
        string? resourceId = null)
    {
        var capabilities = requiredCapability is null
            ? "[]"
            : $$"""[ { "name": "{{requiredCapability}}", "requirement": "required" } ]""";
        var opId = operationId ?? $"{id}.GetIndex";
        var operations = operationId is null && routeId is null
            ? "[]"
            : $$"""
                [
                  {
                    "id": "{{opId}}",
                    "version": 1,
                    "requestContract": "{{id}}.Request.v1",
                    "responseContract": "{{id}}.Response.v1",
                    "ownership": "new",
                    "allowReplacement": false
                  }
                ]
                """;
        var routes = routeId is null
            ? "[]"
            : $$"""
                [
                  {
                    "id": "{{routeId}}",
                    "operationId": "{{opId}}",
                    "methods": [ "GET" ],
                    "path": "{{routePath ?? $"/{id}/index.json"}}",
                    "access": "read",
                    "head": "mirrors-get",
                    "maximumRequestBytes": 0,
                    "maximumResponseBytes": 1048576,
                    "timeoutMilliseconds": 30000
                  }
                ]
                """;
        var contributions = resourceId is null
            ? "[]"
            : $$"""
                [
                  {
                    "id": "{{resourceId}}",
                    "kind": "service-resource",
                    "version": 1
                  }
                ]
                """;

        var json = $$"""
                     {
                       "$schema": "https://schemas.nutestserver.dev/extensions/manifest/v1",
                       "schemaVersion": 1,
                       "id": "{{id}}",
                       "version": "{{version}}",
                       "publisher": "{{publisher}}",
                       "sdk": { "minimum": "1.0.0", "maximumExclusive": "2.0.0" },
                       "contracts": {
                         "manifest": 1, "operation": 1, "contribution": 1,
                         "route": 1, "capability": 1, "structural": 1
                       },
                       "operations": {{operations}},
                       "contributions": {{contributions}},
                       "routes": {{routes}},
                       "capabilities": {{capabilities}}
                     }
                     """;
        return System.Text.Encoding.UTF8.GetBytes(json);
    }
}
