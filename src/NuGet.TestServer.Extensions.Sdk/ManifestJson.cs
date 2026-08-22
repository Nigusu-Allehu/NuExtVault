using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NuGet.TestServer.Extensions.Sdk;

public sealed record ManifestValidationError(string Path, string Code, string Message);

public sealed class ManifestValidationResult
{
    internal ManifestValidationResult(
        ExtensionManifest? manifest,
        ImmutableArray<ManifestValidationError> errors)
    {
        Manifest = manifest;
        Errors = errors;
    }

    public ExtensionManifest? Manifest { get; }

    public ImmutableArray<ManifestValidationError> Errors { get; }

    public bool IsValid => Manifest is not null && Errors.IsEmpty;
}

public readonly record struct ManifestDigest(string Hex);

public static class ExtensionManifestJson
{
    private static readonly ImmutableHashSet<string> RootMembers = Set(
        "$schema",
        "schemaVersion",
        "id",
        "version",
        "publisher",
        "sdk",
        "contracts",
        "operations",
        "contributions",
        "routes",
        "capabilities",
        "state");

    /// <summary>
    /// Headers a route may never declare. Credential, transport, and proxy headers stay
    /// kernel-owned so a binder can never observe or re-interpret them.
    /// </summary>
    private static readonly ImmutableHashSet<string> ReservedHeaderNames =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "Authorization",
            "Cookie",
            "Proxy-Authorization",
            "Host",
            "X-NuGet-ApiKey");

    /// <summary>Every proxy-forwarding header family is reserved.</summary>
    private const string ReservedHeaderPrefix = "X-Forwarded-";

    private static bool IsReservedHeader(string header) =>
        ReservedHeaderNames.Contains(header) ||
        header.StartsWith(ReservedHeaderPrefix, StringComparison.OrdinalIgnoreCase);

    public static ManifestValidationResult Validate(ReadOnlyMemory<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                utf8Json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
        }
        catch (JsonException exception)
        {
            return Invalid(new ManifestValidationError(
                "$",
                "json.invalid",
                exception.Message));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid(Error("$", "json.invalid", "The manifest root must be an object."));
            }

            var root = document.RootElement;
            var errors = new List<ManifestValidationError>();
            UnknownMembers(root, RootMembers, "$", errors);
            Require(
                root,
                [
                    "$schema",
                    "schemaVersion",
                    "id",
                    "version",
                    "publisher",
                    "sdk",
                    "contracts",
                    "operations",
                    "contributions",
                    "routes",
                    "capabilities"
                ],
                "$",
                errors);
            if (errors.Any(error => error.Code == "manifest.required"))
            {
                return Invalid(errors);
            }

            var schemaVersion = Integer(root, "schemaVersion", "$.schemaVersion", errors);
            var schema = String(root, "$schema", "$.$schema", errors);
            if (schemaVersion != 1 ||
                !string.Equals(
                    schema,
                    ExtensionManifest.ManifestV1Schema,
                    StringComparison.Ordinal))
            {
                errors.Add(Error(
                    "$.schemaVersion",
                    "manifest.schema.unsupported",
                    "Only manifest schema version 1 is supported."));
            }

            var id = String(root, "id", "$.id", errors);
            var version = String(root, "version", "$.version", errors);
            var publisher = String(root, "publisher", "$.publisher", errors);
            if (!StableIdentity.IsStable(id))
            {
                errors.Add(Error("$.id", "manifest.identity.invalid", "The extension ID is not stable."));
            }

            if (!SdkContractVersion.TryParse(version, out _))
            {
                errors.Add(Error(
                    "$.version",
                    "manifest.version.invalid",
                    "Extension versions must use major.minor.patch."));
            }

            if (string.IsNullOrWhiteSpace(publisher))
            {
                errors.Add(Error(
                    "$.publisher",
                    "manifest.publisher.invalid",
                    "A publisher identity is required."));
            }

            var sdk = ParseSdk(root, errors);
            var contracts = ParseContracts(root, errors);
            var operations = ParseOperations(root, id, errors);
            var routes = ParseRoutes(root, operations, errors);
            var contributions = ParseContributions(root, id, routes, errors);
            var capabilities = ParseCapabilities(root, errors);
            var state = ParseState(root, errors);

            if (errors.Count > 0)
            {
                return Invalid(errors);
            }

            var manifest = new ExtensionManifest(
                new ManifestSchemaVersion(schemaVersion),
                new ExtensionIdentity(id, version, publisher),
                sdk!,
                contracts!,
                operations,
                contributions,
                routes,
                capabilities,
                state);
            return new ManifestValidationResult(manifest, []);
        }
    }

    public static ExtensionManifest Parse(ReadOnlyMemory<byte> utf8Json)
    {
        var result = Validate(utf8Json);
        if (!result.IsValid)
        {
            throw new FormatException(string.Join(
                Environment.NewLine,
                result.Errors.Select(error =>
                    $"{error.Path}: {error.Code}: {error.Message}")));
        }

        return result.Manifest!;
    }

    public static ReadOnlyMemory<byte> Canonicalize(ExtensionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Indented = false,
                       SkipValidation = false
                   }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", manifest.SchemaUri);
            writer.WritePropertyName("capabilities");
            writer.WriteStartArray();
            foreach (var capability in manifest.Capabilities
                         .OrderBy(value => value.Identity.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", capability.Identity.Value);
                writer.WriteString(
                    "requirement",
                    capability.Requirement == CapabilityRequirement.Required
                        ? "required"
                        : "optional");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("contracts");
            writer.WriteStartObject();
            writer.WriteNumber("capability", manifest.Contracts.Capability.Value);
            writer.WriteNumber("contribution", manifest.Contracts.Contribution.Value);
            writer.WriteNumber("manifest", manifest.Contracts.Manifest.Value);
            writer.WriteNumber("operation", manifest.Contracts.Operation.Value);
            writer.WriteNumber("route", manifest.Contracts.Route.Value);
            writer.WriteNumber("structural", manifest.Contracts.Structural.Value);
            writer.WriteEndObject();

            writer.WritePropertyName("contributions");
            writer.WriteStartArray();
            foreach (var contribution in manifest.Contributions
                         .OrderBy(value => value.Identity.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", contribution.Identity.Value);
                writer.WriteString("kind", contribution.Kind);
                if (contribution.Route is { } contributionRoute)
                {
                    writer.WriteString("routeId", contributionRoute.Value);
                }
                writer.WriteNumber("version", contribution.Version.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteString("id", manifest.Identity.Id);
            writer.WritePropertyName("operations");
            writer.WriteStartArray();
            foreach (var operation in manifest.Operations
                         .OrderBy(value => value.Identity.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("allowReplacement", operation.AllowReplacement);
                writer.WriteString("id", operation.Identity.Value);
                writer.WriteString(
                    "ownership",
                    operation.Ownership == OperationOwnership.New ? "new" : "unknown");
                writer.WriteString("requestContract", operation.RequestContract);
                writer.WriteString("responseContract", operation.ResponseContract);
                writer.WriteNumber("version", operation.Version.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteString("publisher", manifest.Identity.Publisher);
            writer.WritePropertyName("routes");
            writer.WriteStartArray();
            foreach (var route in manifest.Routes
                         .OrderBy(value => value.Identity.Value, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("access", route.Access);
                if (route.DeclaredBody is { } declaredBody)
                {
                    writer.WriteString(
                        "body",
                        declaredBody switch
                        {
                            RouteBodyBinding.None => "none",
                            RouteBodyBinding.Stream => "stream",
                            _ => "bounded"
                        });
                }
                writer.WriteString("head", route.Head);
                if (!route.Headers.IsDefaultOrEmpty)
                {
                    writer.WritePropertyName("headers");
                    writer.WriteStartArray();
                    foreach (var header in route.Headers.Order(StringComparer.Ordinal))
                    {
                        writer.WriteStringValue(header);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteString("id", route.Identity.Value);
                writer.WriteNumber("maximumRequestBytes", route.MaximumRequestBytes);
                writer.WriteNumber("maximumResponseBytes", route.MaximumResponseBytes);
                writer.WritePropertyName("methods");
                writer.WriteStartArray();
                foreach (var method in route.Methods.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(method);
                }
                writer.WriteEndArray();
                writer.WriteString("operationId", route.Operation.Value);
                writer.WriteString("path", route.Path);
                writer.WriteNumber("timeoutMilliseconds", route.TimeoutMilliseconds);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteNumber("schemaVersion", manifest.SchemaVersion.Value);
            writer.WritePropertyName("sdk");
            writer.WriteStartObject();
            writer.WriteString("maximumExclusive", manifest.Sdk.MaximumExclusive.ToString());
            writer.WriteString("minimum", manifest.Sdk.Minimum.ToString());
            writer.WriteEndObject();
            if (manifest.State is { } declaredState)
            {
                writer.WritePropertyName("state");
                writer.WriteStartObject();
                writer.WriteBoolean("required", declaredState.Required);
                writer.WriteString("schemaName", declaredState.SchemaName);
                writer.WriteNumber("schemaVersion", declaredState.SchemaVersion);
                writer.WriteEndObject();
            }
            writer.WriteString("version", manifest.Identity.Version);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static ManifestDigest ComputeDigest(ExtensionManifest manifest) =>
        ComputeDigest(Canonicalize(manifest));

    public static ManifestDigest ComputeDigest(ReadOnlyMemory<byte> canonicalManifest) =>
        new(Convert.ToHexStringLower(SHA256.HashData(canonicalManifest.Span)));

    private static SdkCompatibilityRange? ParseSdk(
        JsonElement root,
        List<ManifestValidationError> errors)
    {
        if (!Object(root, "sdk", "$.sdk", errors, out var sdk))
        {
            return null;
        }

        UnknownMembers(sdk, Set("minimum", "maximumExclusive"), "$.sdk", errors);
        Require(sdk, ["minimum", "maximumExclusive"], "$.sdk", errors);
        var minimumText = String(sdk, "minimum", "$.sdk.minimum", errors);
        var maximumText = String(sdk, "maximumExclusive", "$.sdk.maximumExclusive", errors);
        if (!SdkContractVersion.TryParse(minimumText, out var minimum) ||
            !SdkContractVersion.TryParse(maximumText, out var maximum) ||
            minimum.CompareTo(maximum) >= 0)
        {
            errors.Add(Error(
                "$.sdk",
                "manifest.version.invalid",
                "SDK bounds must be ordered major.minor.patch versions."));
            return null;
        }

        if (ExtensionSdkVersions.Current.CompareTo(minimum) < 0 ||
            ExtensionSdkVersions.Current.CompareTo(maximum) >= 0 ||
            minimum.Major != ExtensionSdkVersions.Current.Major)
        {
            errors.Add(Error(
                "$.sdk",
                "manifest.sdk.unsupported",
                "The manifest SDK range does not include this host SDK."));
        }

        return new SdkCompatibilityRange(minimum, maximum);
    }

    private static ContractVersionSet? ParseContracts(
        JsonElement root,
        List<ManifestValidationError> errors)
    {
        if (!Object(root, "contracts", "$.contracts", errors, out var contracts))
        {
            return null;
        }

        string[] members =
        [
            "manifest",
            "operation",
            "contribution",
            "route",
            "capability",
            "structural"
        ];
        UnknownMembers(contracts, Set(members), "$.contracts", errors);
        Require(contracts, members, "$.contracts", errors);
        var result = new ContractVersionSet(
            new ManifestSchemaVersion(Integer(contracts, "manifest", "$.contracts.manifest", errors)),
            new OperationContractVersion(Integer(
                contracts,
                "operation",
                "$.contracts.operation",
                errors)),
            new ContributionContractVersion(Integer(
                contracts,
                "contribution",
                "$.contracts.contribution",
                errors)),
            new RouteContractVersion(Integer(contracts, "route", "$.contracts.route", errors)),
            new CapabilityContractVersion(Integer(
                contracts,
                "capability",
                "$.contracts.capability",
                errors)),
            new StructuralContractVersion(Integer(
                contracts,
                "structural",
                "$.contracts.structural",
                errors)));
        if (result.Manifest.Value != 1 ||
            result.Operation.Value != 1 ||
            result.Contribution.Value != 1 ||
            result.Route.Value != 1 ||
            result.Capability.Value != 1 ||
            result.Structural.Value != 1)
        {
            errors.Add(Error(
                "$.contracts",
                "manifest.contract.unsupported",
                "Manifest v1 requires every structural contract at version 1."));
        }

        return result;
    }

    private static ImmutableArray<OperationDeclaration> ParseOperations(
        JsonElement root,
        string extensionId,
        List<ManifestValidationError> errors)
    {
        if (!Array(root, "operations", "$.operations", errors, out var values))
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<OperationDeclaration>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var path = $"$.operations[{index++}]";
            if (value.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Error(path, "manifest.type", "An operation must be an object."));
                continue;
            }

            string[] members =
            [
                "id",
                "version",
                "requestContract",
                "responseContract",
                "ownership",
                "allowReplacement"
            ];
            UnknownMembers(value, Set(members), path, errors);
            Require(value, members, path, errors);
            var id = String(value, "id", $"{path}.id", errors);
            var ownership = String(value, "ownership", $"{path}.ownership", errors);
            var allowReplacement = Boolean(
                value,
                "allowReplacement",
                $"{path}.allowReplacement",
                errors);
            if (!identities.Add(id))
            {
                errors.Add(Error(
                    $"{path}.id",
                    "manifest.identity.duplicate",
                    $"Operation identity '{id}' is duplicated."));
            }
            if (!StableIdentity.IsStable(id) ||
                !id.StartsWith(extensionId + ".", StringComparison.Ordinal) ||
                StableIdentity.BuiltInOperationIds.Contains(id))
            {
                errors.Add(Error(
                    $"{path}.id",
                    "operation.identity.not-owned",
                    "Contributors may declare only new stable operation IDs they own."));
            }
            if (!string.Equals(ownership, "new", StringComparison.Ordinal))
            {
                errors.Add(Error(
                    $"{path}.ownership",
                    "operation.ownership.invalid",
                    "Only new operation ownership is supported."));
            }
            if (allowReplacement)
            {
                errors.Add(Error(
                    $"{path}.allowReplacement",
                    "operation.replacement.disabled",
                    "Operation replacement is disabled."));
            }

            var version = Integer(value, "version", $"{path}.version", errors);
            if (version != 1)
            {
                errors.Add(Error(
                    $"{path}.version",
                    "operation.version.unsupported",
                    "Manifest v1 supports operation contract version 1."));
            }

            result.Add(new OperationDeclaration(
                new OperationIdentity(string.IsNullOrWhiteSpace(id) ? "invalid.operation" : id),
                new OperationContractVersion(version),
                String(value, "requestContract", $"{path}.requestContract", errors),
                String(value, "responseContract", $"{path}.responseContract", errors),
                OperationOwnership.New,
                allowReplacement));
        }

        return [.. result.OrderBy(value => value.Identity.Value, StringComparer.Ordinal)];
    }

    private static ImmutableArray<ContributionDeclaration> ParseContributions(
        JsonElement root,
        string extensionId,
        ImmutableArray<RouteDeclaration> routes,
        List<ManifestValidationError> errors)
    {
        if (!Array(root, "contributions", "$.contributions", errors, out var values))
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<ContributionDeclaration>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var path = $"$.contributions[{index++}]";
            if (value.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Error(path, "manifest.type", "A contribution must be an object."));
                continue;
            }

            string[] members = ["id", "kind", "version", "routeId"];
            UnknownMembers(value, Set(members), path, errors);
            Require(value, ["id", "kind", "version"], path, errors);
            var id = String(value, "id", $"{path}.id", errors);
            if (!identities.Add(id))
            {
                errors.Add(Error(
                    $"{path}.id",
                    "manifest.identity.duplicate",
                    $"Contribution identity '{id}' is duplicated."));
            }
            if (!StableIdentity.IsStable(id) ||
                !id.StartsWith(extensionId + ".", StringComparison.Ordinal))
            {
                errors.Add(Error(
                    $"{path}.id",
                    "contribution.identity.not-owned",
                    "Contributors may declare only new stable contribution IDs they own."));
            }

            var version = Integer(value, "version", $"{path}.version", errors);
            if (version != 1)
            {
                errors.Add(Error(
                    $"{path}.version",
                    "contribution.version.unsupported",
                    "Manifest v1 supports contribution contract version 1."));
            }

            string? routeId = null;
            if (value.TryGetProperty("routeId", out var routeIdValue))
            {
                routeId = String(value, "routeId", $"{path}.routeId", errors);
                if (routeIdValue.ValueKind == JsonValueKind.String &&
                    !routes.Any(route => string.Equals(
                        route.Identity.Value,
                        routeId,
                        StringComparison.Ordinal)))
                {
                    errors.Add(Error(
                        $"{path}.routeId",
                        "contribution.route.missing",
                        "A contribution route reference must name a route this extension declares."));
                }
            }

            result.Add(new ContributionDeclaration(
                new ContributionIdentity(string.IsNullOrWhiteSpace(id)
                    ? "invalid.contribution"
                    : id),
                String(value, "kind", $"{path}.kind", errors),
                new ContributionContractVersion(version))
            {
                Route = string.IsNullOrWhiteSpace(routeId) ? null : new RouteIdentity(routeId)
            });
        }

        return [.. result.OrderBy(value => value.Identity.Value, StringComparer.Ordinal)];
    }

    private static ImmutableArray<RouteDeclaration> ParseRoutes(
        JsonElement root,
        ImmutableArray<OperationDeclaration> operations,
        List<ManifestValidationError> errors)
    {
        if (!Array(root, "routes", "$.routes", errors, out var values))
        {
            return [];
        }

        var operationIds = operations
            .Select(operation => operation.Identity.Value)
            .ToHashSet(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<RouteDeclaration>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var path = $"$.routes[{index++}]";
            if (value.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Error(path, "manifest.type", "A route must be an object."));
                continue;
            }

            string[] members =
            [
                "id",
                "operationId",
                "methods",
                "path",
                "access",
                "head",
                "maximumRequestBytes",
                "maximumResponseBytes",
                "timeoutMilliseconds",
                "body",
                "headers"
            ];
            UnknownMembers(value, Set(members), path, errors);
            Require(
                value,
                [
                    "id",
                    "operationId",
                    "methods",
                    "path",
                    "access",
                    "head",
                    "maximumRequestBytes",
                    "maximumResponseBytes",
                    "timeoutMilliseconds"
                ],
                path,
                errors);
            var id = String(value, "id", $"{path}.id", errors);
            var operationId = String(value, "operationId", $"{path}.operationId", errors);
            if (!identities.Add(id))
            {
                errors.Add(Error(
                    $"{path}.id",
                    "manifest.identity.duplicate",
                    $"Route identity '{id}' is duplicated."));
            }
            if (!operationIds.Contains(operationId))
            {
                errors.Add(Error(
                    $"{path}.operationId",
                    "route.operation.missing",
                    "The route must refer to an operation declared by this extension."));
            }

            var methods = Strings(value, "methods", $"{path}.methods", errors);
            var routePath = String(value, "path", $"{path}.path", errors);
            var maximumRequestBytes = Integer(
                value,
                "maximumRequestBytes",
                $"{path}.maximumRequestBytes",
                errors);
            var maximumResponseBytes = Integer(
                value,
                "maximumResponseBytes",
                $"{path}.maximumResponseBytes",
                errors);
            var timeoutMilliseconds = Integer(
                value,
                "timeoutMilliseconds",
                $"{path}.timeoutMilliseconds",
                errors);
            var access = String(value, "access", $"{path}.access", errors);
            var head = String(value, "head", $"{path}.head", errors);
            if (methods.IsEmpty ||
                methods.Any(method => method is not ("DELETE" or "GET" or "HEAD" or "POST" or "PUT")) ||
                methods.Distinct(StringComparer.Ordinal).Count() != methods.Length)
            {
                errors.Add(Error(
                    $"{path}.methods",
                    "route.methods.invalid",
                    "Routes require unique supported uppercase methods."));
            }
            if (!routePath.StartsWith("/", StringComparison.Ordinal) ||
                routePath.Contains('?') ||
                routePath.Contains('#'))
            {
                errors.Add(Error(
                    $"{path}.path",
                    "route.path.invalid",
                    "Route paths must be absolute templates without query or fragment text."));
            }
            if (maximumRequestBytes < 0 ||
                maximumResponseBytes <= 0 ||
                timeoutMilliseconds is <= 0 or > 3_600_000)
            {
                errors.Add(Error(
                    path,
                    "route.limits.invalid",
                    "Route byte and timeout limits must be positive and bounded."));
            }
            if (access is not ("anonymous" or "read" or "write" or "publish" or "unlist" or
                "delete" or "admin" or "control") ||
                head is not ("none" or "mirrors-get"))
            {
                errors.Add(Error(
                    path,
                    "route.policy.invalid",
                    "Route access and HEAD policies must use supported v1 values."));
            }

            RouteBodyBinding? body = null;
            if (value.TryGetProperty("body", out _))
            {
                var declared = String(value, "body", $"{path}.body", errors);
                body = declared switch
                {
                    "none" => RouteBodyBinding.None,
                    "bounded" => RouteBodyBinding.Bounded,
                    "stream" => RouteBodyBinding.Stream,
                    _ => null
                };
                if (body is null)
                {
                    errors.Add(Error(
                        $"{path}.body",
                        "route.body.invalid",
                        "Route body bindings must be 'none', 'bounded', or 'stream'."));
                }
                else if (body != RouteBodyBinding.None && maximumRequestBytes <= 0)
                {
                    errors.Add(Error(
                        $"{path}.body",
                        "route.body.unbounded",
                        "A route that binds a body must declare a positive maximumRequestBytes."));
                }
            }

            var headers = value.TryGetProperty("headers", out _)
                ? Strings(value, "headers", $"{path}.headers", errors)
                : [];
            if (headers.Any(header =>
                    string.IsNullOrWhiteSpace(header) ||
                    header.Length > 64 ||
                    !header.All(character =>
                        char.IsAsciiLetterOrDigit(character) || character == '-')) ||
                headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length ||
                headers.Any(IsReservedHeader))
            {
                errors.Add(Error(
                    $"{path}.headers",
                    "route.headers.invalid",
                    "Declared headers must be unique, token-shaped, and never reserved."));
            }

            result.Add(new RouteDeclaration(
                new RouteIdentity(string.IsNullOrWhiteSpace(id) ? "invalid.route" : id),
                new OperationIdentity(string.IsNullOrWhiteSpace(operationId)
                    ? "invalid.operation"
                    : operationId),
                ExtensionSdkVersions.RouteV1,
                [.. methods.Order(StringComparer.Ordinal)],
                routePath,
                maximumRequestBytes,
                maximumResponseBytes,
                access,
                head,
                timeoutMilliseconds,
                body,
                [.. headers.Order(StringComparer.Ordinal)]));
        }

        return [.. result.OrderBy(value => value.Identity.Value, StringComparer.Ordinal)];
    }

    private static ExtensionStateDeclaration? ParseState(
        JsonElement root,
        List<ManifestValidationError> errors)
    {
        if (!root.TryGetProperty("state", out _))
        {
            return null;
        }

        if (!Object(root, "state", "$.state", errors, out var state))
        {
            return null;
        }

        string[] members = ["schemaName", "schemaVersion", "required"];
        UnknownMembers(state, Set(members), "$.state", errors);
        Require(state, members, "$.state", errors);
        var schemaName = String(state, "schemaName", "$.state.schemaName", errors);
        var schemaVersion = Integer(state, "schemaVersion", "$.state.schemaVersion", errors);
        var required = Boolean(state, "required", "$.state.required", errors);
        if (string.IsNullOrWhiteSpace(schemaName) ||
            schemaName.Length > 64 ||
            !schemaName.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '.'))
        {
            errors.Add(Error(
                "$.state.schemaName",
                "state.schema.invalid",
                "State schema names must be short, token-shaped identifiers."));
        }

        if (schemaVersion < 1)
        {
            errors.Add(Error(
                "$.state.schemaVersion",
                "state.schema.version-invalid",
                "State schema versions start at 1."));
        }

        return new ExtensionStateDeclaration(schemaName, schemaVersion, required);
    }

    private static ImmutableArray<CapabilityRequest> ParseCapabilities(
        JsonElement root,
        List<ManifestValidationError> errors)
    {
        if (!Array(root, "capabilities", "$.capabilities", errors, out var values))
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<CapabilityRequest>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var path = $"$.capabilities[{index++}]";
            if (value.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Error(path, "manifest.type", "A capability must be an object."));
                continue;
            }

            UnknownMembers(value, Set("name", "requirement"), path, errors);
            Require(value, ["name", "requirement"], path, errors);
            var name = String(value, "name", $"{path}.name", errors);
            if (!StableIdentity.IsStable(name))
            {
                errors.Add(Error(
                    $"{path}.name",
                    "manifest.capability.identity-invalid",
                    "Capability names must be stable dotted identities."));
            }
            if (!identities.Add(name))
            {
                errors.Add(Error(
                    $"{path}.name",
                    "manifest.identity.duplicate",
                    $"Capability identity '{name}' is duplicated."));
            }

            if (!value.TryGetProperty("requirement", out _))
            {
                errors.Add(Error(
                    $"{path}.requirement",
                    "manifest.capability.requirement-required",
                    "Every capability must explicitly be required or optional."));
                continue;
            }

            var requirementText = String(
                value,
                "requirement",
                $"{path}.requirement",
                errors);
            var requirement = requirementText switch
            {
                "required" => CapabilityRequirement.Required,
                "optional" => CapabilityRequirement.Optional,
                _ => (CapabilityRequirement?)null
            };
            if (requirement is null)
            {
                errors.Add(Error(
                    $"{path}.requirement",
                    "manifest.capability.requirement-invalid",
                    "Capability requirement must be 'required' or 'optional'."));
                continue;
            }

            result.Add(new CapabilityRequest(
                new CapabilityIdentity(string.IsNullOrWhiteSpace(name)
                    ? "invalid.capability"
                    : name),
                requirement.Value));
        }

        return [.. result.OrderBy(value => value.Identity.Value, StringComparer.Ordinal)];
    }

    private static void UnknownMembers(
        JsonElement value,
        ImmutableHashSet<string> known,
        string path,
        List<ManifestValidationError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
        {
            if (!seen.Add(member.Name))
            {
                errors.Add(Error(
                    $"{path}.{member.Name}",
                    "manifest.member.duplicate",
                    $"Member '{member.Name}' is declared more than once."));
            }
            if (!known.Contains(member.Name))
            {
                errors.Add(Error(
                    $"{path}.{member.Name}",
                    "manifest.unknown-member",
                    $"Member '{member.Name}' is not allowed."));
            }
        }
    }

    private static void Require(
        JsonElement value,
        IEnumerable<string> names,
        string path,
        List<ManifestValidationError> errors)
    {
        foreach (var name in names)
        {
            if (!value.TryGetProperty(name, out _))
            {
                errors.Add(Error(
                    $"{path}.{name}",
                    name == "requirement"
                        ? "manifest.capability.requirement-required"
                        : "manifest.required",
                    $"Required member '{name}' is missing."));
            }
        }
    }

    private static string String(
        JsonElement owner,
        string name,
        string path,
        List<ManifestValidationError> errors)
    {
        if (!owner.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            errors.Add(Error(path, "manifest.type", $"Member '{name}' must be a string."));
            return string.Empty;
        }

        return value.GetString() ?? string.Empty;
    }

    private static int Integer(
        JsonElement owner,
        string name,
        string path,
        List<ManifestValidationError> errors)
    {
        if (!owner.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
        {
            errors.Add(Error(path, "manifest.type", $"Member '{name}' must be an integer."));
            return 0;
        }

        return result;
    }

    private static bool Boolean(
        JsonElement owner,
        string name,
        string path,
        List<ManifestValidationError> errors)
    {
        if (!owner.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add(Error(path, "manifest.type", $"Member '{name}' must be a boolean."));
            return false;
        }

        return value.GetBoolean();
    }

    private static bool Object(
        JsonElement owner,
        string name,
        string path,
        List<ManifestValidationError> errors,
        out JsonElement value)
    {
        if (!owner.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Error(path, "manifest.type", $"Member '{name}' must be an object."));
            return false;
        }

        return true;
    }

    private static bool Array(
        JsonElement owner,
        string name,
        string path,
        List<ManifestValidationError> errors,
        out JsonElement value)
    {
        if (!owner.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Error(path, "manifest.type", $"Member '{name}' must be an array."));
            return false;
        }

        return true;
    }

    private static ImmutableArray<string> Strings(
        JsonElement owner,
        string name,
        string path,
        List<ManifestValidationError> errors)
    {
        if (!Array(owner, name, path, errors, out var values))
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<string>();
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String)
            {
                errors.Add(Error(
                    $"{path}[{index}]",
                    "manifest.type",
                    "Array values must be strings."));
            }
            else
            {
                result.Add(value.GetString() ?? string.Empty);
            }
            index++;
        }

        return [.. result];
    }

    private static ManifestValidationError Error(string path, string code, string message) =>
        new(path, code, message);

    private static ManifestValidationResult Invalid(params ManifestValidationError[] errors) =>
        Invalid((IEnumerable<ManifestValidationError>)errors);

    private static ManifestValidationResult Invalid(IEnumerable<ManifestValidationError> errors) =>
        new(
            null,
            [
                .. errors.OrderBy(error => error.Path, StringComparer.Ordinal)
                    .ThenBy(error => error.Code, StringComparer.Ordinal)
            ]);

    private static ImmutableHashSet<string> Set(params string[] values) =>
        values.ToImmutableHashSet(StringComparer.Ordinal);
}

public static class CanonicalContractBytes
{
    public static ReadOnlyMemory<byte> Manifest(ExtensionManifest manifest) =>
        ExtensionManifestJson.Canonicalize(manifest);

    public static ManifestDigest ManifestDigest(ExtensionManifest manifest) =>
        ExtensionManifestJson.ComputeDigest(manifest);

    public static ReadOnlyMemory<byte> StructuralContract(Assembly assembly) =>
        StructuralContractFingerprint.Create(assembly).CanonicalBytes;
}
