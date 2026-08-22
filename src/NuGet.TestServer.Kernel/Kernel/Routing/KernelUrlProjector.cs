using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Primitives;
using NuGet.Packaging;
using NuGet.TestServer.Extensions.Sdk;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;
using NuGet.Versioning;

namespace NuGet.TestServer.Kernel.Routing;

internal sealed record PublicUrlOrigin
{
    public PublicUrlOrigin(string scheme, string authority, string pathBase)
    {
        scheme = scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https"))
        {
            throw new RouteProjectionException(
                "route-reference.invalid-origin: The public scheme must be HTTP or HTTPS.");
        }

        if (string.IsNullOrWhiteSpace(authority) ||
            authority.Any(character =>
                char.IsWhiteSpace(character) ||
                char.IsControl(character) ||
                character is '@' or '/' or '\\' or '?' or '#') ||
            !Uri.TryCreate($"{scheme}://{authority}/", UriKind.Absolute, out var parsed) ||
            parsed.UserInfo.Length != 0 ||
            parsed.AbsolutePath != "/" ||
            parsed.Query.Length != 0 ||
            parsed.Fragment.Length != 0)
        {
            throw new RouteProjectionException(
                "route-reference.invalid-origin: The public authority is invalid.");
        }

        Scheme = scheme;
        Authority = parsed.Authority;
        PathBase = NormalizePrefix(pathBase);
    }

    public string Scheme { get; }

    public string Authority { get; }

    public string PathBase { get; }

    public static PublicUrlOrigin FromRequest(
        HttpContext context,
        TransportSecurityOptions transport)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transport);

        var scheme = context.Request.Scheme;
        var authority = context.Request.Host.Value ??
            throw new RouteProjectionException(
                "route-reference.invalid-origin: The request has no host.");
        var pathBase = context.Request.PathBase.Value ?? string.Empty;
        if (transport.IsTrustedProxy(context.Connection.RemoteIpAddress))
        {
            scheme = SingleForwardedValue(context, "X-Forwarded-Proto") ?? scheme;
            authority = SingleForwardedValue(context, "X-Forwarded-Host") ?? authority;
            pathBase = SingleForwardedValue(context, "X-Forwarded-Prefix") ?? pathBase;
        }

        return new PublicUrlOrigin(scheme, authority, pathBase);
    }

    private static string? SingleForwardedValue(HttpContext context, string headerName)
    {
        StringValues values = context.Request.Headers[headerName];
        if (values.Count == 0)
        {
            return null;
        }

        if (values.Count != 1 ||
            string.IsNullOrWhiteSpace(values[0]) ||
            values[0]!.Contains(',', StringComparison.Ordinal))
        {
            throw new RouteProjectionException(
                $"route-reference.invalid-forwarded-header: Header '{headerName}' must contain one value.");
        }

        return values[0];
    }

    private static string NormalizePrefix(string pathBase)
    {
        if (string.IsNullOrEmpty(pathBase) || pathBase == "/")
        {
            return string.Empty;
        }

        if (!pathBase.StartsWith('/') ||
            pathBase.Contains('\\', StringComparison.Ordinal) ||
            pathBase.Contains('?', StringComparison.Ordinal) ||
            pathBase.Contains('#', StringComparison.Ordinal) ||
            pathBase.Split('/').Any(segment => segment == "..") ||
            pathBase.Any(char.IsControl))
        {
            throw new RouteProjectionException(
                "route-reference.invalid-origin: The public path base is unsafe.");
        }

        return pathBase.TrimEnd('/');
    }
}

internal sealed class KernelUrlProjector
{
    private readonly ImmutableDictionary<string, KernelRouteEndpoint> _routes;

    public KernelUrlProjector(KernelRouteTable routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (!routes.IsFrozen)
        {
            throw new InvalidOperationException("URL projection requires a frozen route table.");
        }

        _routes = routes.Endpoints.ToImmutableDictionary(
            endpoint => endpoint.Descriptor.Name,
            StringComparer.Ordinal);
    }

    public string Project(RouteReference reference, PublicUrlOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(origin);
        if (!_routes.TryGetValue(reference.RouteName, out var endpoint))
        {
            throw Failure(
                "unknown-route",
                $"Route '{reference.RouteName}' is not active in this profile.");
        }

        var descriptor = endpoint.Descriptor;
        var template = descriptor.PathTemplate;
        var expectedParameters = descriptor.RouteParameters;
        if (reference.Target == RouteReferenceTarget.ResourceBase)
        {
            if (!descriptor.AllowsResourceBaseReference)
            {
                throw Failure(
                    "resource-base-not-permitted",
                    $"Route '{reference.RouteName}' does not permit resource-base projection.");
            }

            var parameterStart = template.IndexOf('{');
            template = parameterStart < 0 ? template : template[..parameterStart];
            expectedParameters = [];
        }

        ValidateParameters(reference, expectedParameters, descriptor);
        var path = template;
        foreach (var expected in expectedParameters)
        {
            var supplied = reference.Parameters.Single(parameter =>
                parameter.Name.Equals(expected.Name, StringComparison.OrdinalIgnoreCase));
            path = path.Replace(
                $"{{{expected.Name}}}",
                Uri.EscapeDataString(Normalize(supplied)),
                StringComparison.OrdinalIgnoreCase);
        }

        var query = reference.Query.Length == 0
            ? string.Empty
            : QueryString.Create(reference.Query.Select(value =>
                new KeyValuePair<string, string?>(value.Name, value.Value))).Value;
        var fragment = reference.Fragment is null
            ? string.Empty
            : $"#{Uri.EscapeDataString(reference.Fragment)}";
        return $"{origin.Scheme}://{origin.Authority}{origin.PathBase}{path}{query}{fragment}";
    }

    public JsonSerializerOptions CreateJsonOptions(Func<PublicUrlOrigin> origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new RouteReferenceJsonConverter(this, origin));
        return options;
    }

    private static void ValidateParameters(
        RouteReference reference,
        ImmutableArray<EndpointParameter> expected,
        EndpointDescriptor descriptor)
    {
        foreach (var parameter in expected)
        {
            var supplied = reference.Parameters.FirstOrDefault(value =>
                value.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (supplied is null)
            {
                throw Failure(
                    "missing-parameter",
                    $"Route '{reference.RouteName}' requires parameter '{parameter.Name}'.");
            }

            if (supplied.Kind != parameter.Kind)
            {
                throw Failure(
                    "parameter-type",
                    $"Route parameter '{parameter.Name}' requires '{parameter.Kind}' but received '{supplied.Kind}'.");
            }
        }

        var expectedNames = expected.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = reference.Parameters.FirstOrDefault(parameter =>
            !expectedNames.Contains(parameter.Name));
        if (extra is not null)
        {
            throw Failure(
                "extra-parameter",
                $"Route '{reference.RouteName}' does not declare parameter '{extra.Name}'.");
        }

        var queryNames = descriptor.QueryParameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extraQuery = reference.Query.FirstOrDefault(parameter =>
            !queryNames.Contains(parameter.Name));
        if (extraQuery is not null)
        {
            throw Failure(
                "extra-query",
                $"Route '{reference.RouteName}' does not declare query parameter '{extraQuery.Name}'.");
        }

        var missingQuery = descriptor.QueryParameters.FirstOrDefault(parameter =>
            parameter.IsRequired &&
            !reference.Query.Any(value =>
                value.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)));
        if (missingQuery is not null)
        {
            throw Failure(
                "missing-query",
                $"Route '{reference.RouteName}' requires query parameter '{missingQuery.Name}'.");
        }

        if (reference.Fragment is not null && !descriptor.AllowsFragmentReference)
        {
            throw Failure(
                "fragment-not-permitted",
                $"Route '{reference.RouteName}' does not permit fragments.");
        }
    }

    private static string Normalize(RouteParameterValue parameter) => parameter.Kind switch
    {
        RouteParameterKind.Text => parameter.Value,
        RouteParameterKind.PackageId when PackageIdValidator.IsValidPackageId(parameter.Value) =>
            parameter.Value.ToLowerInvariant(),
        RouteParameterKind.PackageId => throw Failure(
            "unsafe-parameter",
            $"Route parameter '{parameter.Name}' is not a valid package ID."),
        RouteParameterKind.PackageVersion when NuGetVersion.TryParse(parameter.Value, out var version) =>
            TestPackage.NormalizeVersion(version),
        RouteParameterKind.PackageVersion => throw Failure(
            "unsafe-parameter",
            $"Route parameter '{parameter.Name}' is not a valid package version."),
        _ => throw Failure(
            "parameter-type",
            $"Route parameter '{parameter.Name}' has an unsupported type.")
    };

    private static RouteProjectionException Failure(string code, string message) =>
        new($"route-reference.{code}: {message}");

    private sealed class RouteReferenceJsonConverter(
        KernelUrlProjector projector,
        Func<PublicUrlOrigin> origin) : JsonConverter<RouteReference>
    {
        public override RouteReference Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            throw new NotSupportedException("Projected route references are write-only.");

        public override void Write(
            Utf8JsonWriter writer,
            RouteReference value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(projector.Project(value, origin()));
    }
}

internal sealed class RouteProjectionException(string message) : InvalidOperationException(message);
