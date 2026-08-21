using System.Collections.Immutable;

namespace NuGet.TestServer.Extensions.Abstractions;

internal enum RouteReferenceTarget
{
    Endpoint,
    ResourceBase
}

internal enum RouteParameterKind
{
    Text,
    PackageId,
    PackageVersion
}

internal sealed record RouteParameterValue
{
    public RouteParameterValue(string name, RouteParameterKind kind, string value)
    {
        Name = ValidateName(name);
        Kind = kind;
        Value = ValidatePathValue(value);
    }

    public string Name { get; }

    public RouteParameterKind Kind { get; }

    public string Value { get; }

    public static RouteParameterValue Text(string name, string value) =>
        new(name, RouteParameterKind.Text, value);

    public static RouteParameterValue PackageId(string name, string value) =>
        new(name, RouteParameterKind.PackageId, value);

    public static RouteParameterValue PackageVersion(string name, string value) =>
        new(name, RouteParameterKind.PackageVersion, value);

    private static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name;
    }

    private static string ValidatePathValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(character =>
                char.IsControl(character) ||
                character is '/' or '\\' or '?' or '#'))
        {
            throw new ArgumentException(
                "Route parameter values must be one safe path segment.",
                nameof(value));
        }

        return value;
    }
}

internal sealed record RouteQueryValue
{
    public RouteQueryValue(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Route query values must not contain control characters.",
                nameof(value));
        }

        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }
}

internal sealed record RouteReference
{
    public RouteReference(
        string routeName,
        RouteReferenceTarget target,
        ImmutableArray<RouteParameterValue> parameters,
        ImmutableArray<RouteQueryValue> query,
        string? fragment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeName);
        if (parameters.IsDefault)
        {
            throw new ArgumentException("Route parameters must be initialized.", nameof(parameters));
        }

        if (query.IsDefault)
        {
            throw new ArgumentException("Route query values must be initialized.", nameof(query));
        }

        EnsureUnique(parameters.Select(parameter => parameter.Name), nameof(parameters));
        EnsureUnique(query.Select(parameter => parameter.Name), nameof(query));
        if (fragment is not null && fragment.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Route fragments must not contain control characters.",
                nameof(fragment));
        }

        RouteName = routeName;
        Target = target;
        Parameters = parameters;
        Query = query;
        Fragment = fragment;
    }

    public string RouteName { get; }

    public RouteReferenceTarget Target { get; }

    public ImmutableArray<RouteParameterValue> Parameters { get; }

    public ImmutableArray<RouteQueryValue> Query { get; }

    public string? Fragment { get; }

    public static RouteReference Endpoint(
        string routeName,
        params RouteParameterValue[] parameters) =>
        new(routeName, RouteReferenceTarget.Endpoint, [.. parameters], [], null);

    public static RouteReference Base(string routeName) =>
        new(routeName, RouteReferenceTarget.ResourceBase, [], [], null);

    private static void EnsureUnique(IEnumerable<string> names, string parameterName)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (names.Any(name => !unique.Add(name)))
        {
            throw new ArgumentException(
                "Route reference parameter names must be unique.",
                parameterName);
        }
    }
}
