using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NuGet.TestServer.Extensions.Sdk;

public readonly record struct SdkContractVersion(int Major, int Minor, int Patch)
{
    internal int CompareTo(SdkContractVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    internal static bool TryParse(string? value, out SdkContractVersion version)
    {
        version = default;
        if (value is null)
        {
            return false;
        }

        var parts = value.Split('.');
        if (parts.Length != 3 ||
            !parts.All(part => part.Length > 0 &&
                               (part.Length == 1 || part[0] != '0') &&
                               part.All(char.IsAsciiDigit)) ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new SdkContractVersion(major, minor, patch);
        return true;
    }
}

public readonly record struct ManifestSchemaVersion(int Value)
{
    public static implicit operator int(ManifestSchemaVersion version) => version.Value;
}

public readonly record struct OperationContractVersion(int Value);

public readonly record struct ContributionContractVersion(int Value);

public readonly record struct RouteContractVersion(int Value);

public readonly record struct CapabilityContractVersion(int Value);

public readonly record struct StructuralContractVersion(int Value);

public readonly record struct SdkContractIdentity(string Value);

public readonly record struct OperationIdentity
{
    public OperationIdentity(string value)
    {
        Value = StableIdentity.Required(value, nameof(value));
    }

    public string Value { get; }
}

public readonly record struct ContributionIdentity
{
    public ContributionIdentity(string value)
    {
        Value = StableIdentity.Required(value, nameof(value));
    }

    public string Value { get; }
}

public readonly record struct RouteIdentity
{
    public RouteIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
}

public readonly record struct CapabilityIdentity
{
    public CapabilityIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }
}

public enum CapabilityRequirement
{
    Required,
    Optional
}

public sealed record CapabilityRequest
{
    public CapabilityRequest(CapabilityIdentity identity, CapabilityRequirement requirement)
    {
        Identity = identity;
        Requirement = requirement;
    }

    internal CapabilityRequest(string name, bool IsRequired)
        : this(
            new CapabilityIdentity(name),
            IsRequired ? CapabilityRequirement.Required : CapabilityRequirement.Optional)
    {
    }

    public CapabilityIdentity Identity { get; }

    public CapabilityRequirement Requirement { get; }

    internal string Name => Identity.Value;

    internal bool IsRequired => Requirement == CapabilityRequirement.Required;
}

public sealed record ExtensionIdentity(string Id, string Version, string Publisher);

public sealed record SdkCompatibilityRange(
    SdkContractVersion Minimum,
    SdkContractVersion MaximumExclusive);

public sealed record ContractVersionSet(
    ManifestSchemaVersion Manifest,
    OperationContractVersion Operation,
    ContributionContractVersion Contribution,
    RouteContractVersion Route,
    CapabilityContractVersion Capability,
    StructuralContractVersion Structural);

public sealed record OperationDeclaration(
    OperationIdentity Identity,
    OperationContractVersion Version,
    string RequestContract,
    string ResponseContract,
    OperationOwnership Ownership,
    bool AllowReplacement);

public enum OperationOwnership
{
    New
}

public sealed record ContributionDeclaration(
    ContributionIdentity Identity,
    string Kind,
    ContributionContractVersion Version)
{
    /// <summary>
    /// The route this contribution projects, when the extension declares more than one
    /// route. The kernel resolves the reference and projects the absolute URL; the
    /// contribution never carries a host-derived address.
    /// </summary>
    public RouteIdentity? Route { get; init; }
}

/// <summary>
/// How the kernel binds the request payload for one declared route. The kernel reads a
/// bounded body before the binder runs and hands a stream route a non-buffering,
/// kernel-issued <see cref="StreamHandle"/>.
/// </summary>
public enum RouteBodyBinding
{
    None = 0,
    Bounded,
    Stream
}

/// <summary>
/// The authoritative extension state one extension owns. The kernel registers the
/// namespaced schema with its transactional store before the host listens, so an
/// extension can never write state the kernel does not know how to checkpoint,
/// migrate, quota, or restore.
/// </summary>
public sealed record ExtensionStateDeclaration(
    string SchemaName,
    int SchemaVersion,
    bool Required);

public sealed class RouteDeclaration
{
    public RouteDeclaration(
        RouteIdentity identity,
        OperationIdentity operation,
        RouteContractVersion version,
        ImmutableArray<string> methods,
        string path,
        long maximumRequestBytes,
        long maximumResponseBytes)
        : this(
            identity,
            operation,
            version,
            methods,
            path,
            maximumRequestBytes,
            maximumResponseBytes,
            "read",
            "none",
            30_000)
    {
    }

    internal RouteDeclaration(
        RouteIdentity identity,
        OperationIdentity operation,
        RouteContractVersion version,
        ImmutableArray<string> methods,
        string path,
        long maximumRequestBytes,
        long maximumResponseBytes,
        string access,
        string head,
        int timeoutMilliseconds,
        RouteBodyBinding? body = null,
        ImmutableArray<string> headers = default)
    {
        Identity = identity;
        Operation = operation;
        Version = version;
        Methods = methods;
        Path = path;
        MaximumRequestBytes = maximumRequestBytes;
        MaximumResponseBytes = maximumResponseBytes;
        Access = access;
        Head = head;
        TimeoutMilliseconds = timeoutMilliseconds;
        DeclaredBody = body;
        Headers = headers.IsDefault ? [] : headers;
    }

    public RouteIdentity Identity { get; }

    public OperationIdentity Operation { get; }

    public RouteContractVersion Version { get; }

    public ImmutableArray<string> Methods { get; }

    public string Path { get; }

    public long MaximumRequestBytes { get; }

    public long MaximumResponseBytes { get; }

    internal string Access { get; }

    internal string Head { get; }

    internal int TimeoutMilliseconds { get; }

    /// <summary>
    /// The body binding declared by the manifest, or <c>null</c> when the manifest does
    /// not declare one and the kernel infers it from <see cref="MaximumRequestBytes"/>.
    /// </summary>
    internal RouteBodyBinding? DeclaredBody { get; }

    /// <summary>The request headers a binder for this route may read.</summary>
    internal ImmutableArray<string> Headers { get; }

    /// <summary>
    /// The effective body binding: a declared value, otherwise <see cref="RouteBodyBinding.None"/>
    /// for body-free routes and <see cref="RouteBodyBinding.Bounded"/> for the rest.
    /// </summary>
    public RouteBodyBinding Body =>
        DeclaredBody ??
        (MaximumRequestBytes <= 0 ? RouteBodyBinding.None : RouteBodyBinding.Bounded);
}

public static class ExtensionSdkVersions
{
    public const string ConformanceSuiteV1 = "NuGet.TestServer.Extensions.Conformance/v1";

    public static SdkContractIdentity Identity { get; } =
        new("NuGet.TestServer.Extensions.Sdk");

    public static SdkContractVersion Current { get; } = new(1, 3, 0);

    public static SdkContractVersion OldestSupported { get; } = new(1, 0, 0);

    public static ManifestSchemaVersion ManifestV1 { get; } = new(1);

    public static OperationContractVersion OperationV1 { get; } = new(1);

    public static ContributionContractVersion ContributionV1 { get; } = new(1);

    public static RouteContractVersion RouteV1 { get; } = new(1);

    public static CapabilityContractVersion CapabilityV1 { get; } = new(1);

    public static StructuralContractVersion StructuralV1 { get; } = new(1);

    public static bool IsSupported(SdkContractVersion version) =>
        version.Major == Current.Major &&
        version.CompareTo(OldestSupported) >= 0 &&
        version.CompareTo(Current) <= 0;
}

public static class ExtensionSdkCompatibility
{
    public static readonly TimeSpan MinimumSupportDuration = TimeSpan.FromDays(365);
    public const int MinimumPriorMinorReleases = 2;
    public const bool SameMajorRequired = true;
}

public sealed class BoundedDocument
{
    public BoundedDocument(
        byte[] content,
        long maximumLength,
        string contentType)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (maximumLength < 0 || content.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLength),
                "The document content exceeds its declared maximum length.");
        }

        Content = content;
        MaximumLength = maximumLength;
        ContentType = contentType;
    }

    public ReadOnlyMemory<byte> Content { get; }

    public int ContentLength => Content.Length;

    public long MaximumLength { get; }

    public string ContentType { get; }
}

public sealed class OutboundHttpRequest;

public interface IOutboundHttpCapability
{
    ValueTask<BoundedDocument> SendAsync(
        OutboundHttpRequest request,
        CancellationToken cancellationToken);
}

public sealed class CapabilityGrantSet : IExtensionCapabilities
{
    private readonly string _extensionId;
    private readonly ImmutableHashSet<CapabilityIdentity> _grants;

    private CapabilityGrantSet(
        string extensionId,
        ImmutableHashSet<CapabilityIdentity> grants)
    {
        _extensionId = extensionId;
        _grants = grants;
    }

    public static CapabilityGrantSet Create(
        string extensionId,
        IEnumerable<CapabilityIdentity> grants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extensionId);
        ArgumentNullException.ThrowIfNull(grants);
        return new CapabilityGrantSet(extensionId, grants.ToImmutableHashSet());
    }

    public TCapability GetRequired<TCapability>(CapabilityRequest request)
        where TCapability : class
    {
        if (!TryResolve(request, out TCapability? capability))
        {
            throw new CapabilityDeniedException(_extensionId, request.Identity.Value);
        }

        return capability!;
    }

    public bool TryGet<TCapability>(
        CapabilityRequest request,
        out TCapability? capability)
        where TCapability : class
    {
        if (request.Requirement != CapabilityRequirement.Optional)
        {
            throw new ArgumentException(
                "TryGet may be used only for optional capabilities.",
                nameof(request));
        }

        return TryResolve(request, out capability);
    }

    private bool TryResolve<TCapability>(
        CapabilityRequest request,
        out TCapability? capability)
        where TCapability : class
    {
        capability = default;
        if (!_grants.Contains(request.Identity))
        {
            return false;
        }

        object? instance = typeof(TCapability) == typeof(IHostClockCapability)
            ? SystemClockCapability.Instance
            : null;
        if (instance is null)
        {
            return false;
        }

        capability = (TCapability)instance;
        return true;
    }

    private sealed class SystemClockCapability : IHostClockCapability
    {
        internal static SystemClockCapability Instance { get; } = new();

        public ValueTask<DateTimeOffset> GetUtcNowAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(DateTimeOffset.UtcNow);
        }
    }
}

public enum ReplacementPolicy
{
    Disabled,
    Never
}

public static class OperationReplacementPolicies
{
    public static ReplacementPolicy Default { get; } = ReplacementPolicy.Disabled;

    public static ReplacementPolicy IdentityMutations { get; } = ReplacementPolicy.Never;

    public static ReplacementPolicy OwnershipMutations { get; } = ReplacementPolicy.Never;

    public static ReplacementPolicy For(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        return StableIdentity.BuiltInOperationIds.Contains(operationId)
            ? ReplacementPolicy.Never
            : ReplacementPolicy.Disabled;
    }
}

public sealed class OperationContributor
{
    private readonly string _extensionId;

    public OperationContributor(string extensionId)
    {
        _extensionId = StableIdentity.Required(extensionId, nameof(extensionId));
    }

    public OperationDeclaration Define<TRequest, TResponse>(
        OperationIdentity identity,
        OperationContractVersion version,
        Func<TRequest, CancellationToken, ValueTask<OperationResponse<TResponse>>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!identity.Value.StartsWith(_extensionId + ".", StringComparison.Ordinal) ||
            !StableIdentity.IsStable(identity.Value))
        {
            throw new ArgumentException(
                "An operation contributor may define only new stable IDs owned by its extension.",
                nameof(identity));
        }

        return new OperationDeclaration(
            identity,
            version,
            $"{typeof(TRequest).FullName}.v{version.Value}",
            $"{typeof(TResponse).FullName}.v{version.Value}",
            OperationOwnership.New,
            AllowReplacement: false);
    }
}

/// <summary>
/// The kernel-owned binding source behind <see cref="RouteBindingRequest"/>. It is
/// implemented by the kernel only; a binder never reaches the HTTP request context,
/// dependency injection, or endpoint routing through it.
/// </summary>
internal interface IRouteBindingSource
{
    bool TryGetRoute(string name, out string? value);

    bool TryGetQuery(string name, out string? value);

    string? FindHeader(string name);

    BoundedDocument ReadBody();

    StreamHandle BindBodyStream();
}

public sealed class RouteBindingRequest
{
    private readonly IRouteBindingSource _source;

    internal RouteBindingRequest(IRouteBindingSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    internal RouteBindingRequest(
        IReadOnlyDictionary<string, string> route,
        IReadOnlyDictionary<string, string> query,
        BoundedDocument body)
        : this(new DictionaryBindingSource(route, query, body))
    {
    }

    public string GetRoute(string name) =>
        _source.TryGetRoute(name, out var value) && value is not null
            ? value
            : throw new KeyNotFoundException($"Route value '{name}' is not available.");

    /// <summary>
    /// Reads a declared route value without throwing when the route does not declare it.
    /// </summary>
    public bool TryGetRoute(string name, out string? value) =>
        _source.TryGetRoute(name, out value);

    public string GetQuery(string name) =>
        _source.TryGetQuery(name, out var value) && value is not null
            ? value
            : throw new KeyNotFoundException($"Query value '{name}' is not available.");

    /// <summary>
    /// Reads a query value without throwing when the request omits it.
    /// </summary>
    public bool TryGetQuery(string name, out string? value) =>
        _source.TryGetQuery(name, out value);

    /// <summary>
    /// Reads one of the request headers the route declares. Undeclared headers are never
    /// visible, so a binder cannot read authorization or transport headers.
    /// </summary>
    public string? FindHeader(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _source.FindHeader(name);
    }

    /// <summary>
    /// Reads the bounded request body the kernel already read under the route's declared
    /// limit. Routes that declare <see cref="RouteBodyBinding.Stream"/> reject this call.
    /// </summary>
    public BoundedDocument ReadBody() => _source.ReadBody();

    /// <summary>
    /// Registers the unbuffered request body as kernel content and returns the
    /// kernel-issued handle. Package and symbol content never crosses the boundary as
    /// bytes; the kernel owns the stream, its limit, and its lifetime.
    /// </summary>
    public StreamHandle BindBodyStream() => _source.BindBodyStream();

    private sealed class DictionaryBindingSource(
        IReadOnlyDictionary<string, string> route,
        IReadOnlyDictionary<string, string> query,
        BoundedDocument body) : IRouteBindingSource
    {
        public bool TryGetRoute(string name, out string? value)
        {
            var found = route.TryGetValue(name, out var resolved);
            value = resolved;
            return found;
        }

        public bool TryGetQuery(string name, out string? value)
        {
            var found = query.TryGetValue(name, out var resolved);
            value = resolved;
            return found;
        }

        public string? FindHeader(string name) => null;

        public BoundedDocument ReadBody() => body;

        public StreamHandle BindBodyStream() =>
            throw new InvalidOperationException(
                "This route does not declare a streaming request body.");
    }
}

public interface IRouteBinderRegistry
{
    void Bind<TRequest>(
        RouteIdentity route,
        Func<RouteBindingRequest, CancellationToken, ValueTask<TRequest>> binder);
}

internal static partial class StableIdentity
{
    private static readonly Regex Pattern = StableIdentityRegex();

    internal static ImmutableHashSet<string> BuiltInOperationIds { get; } =
        OperationContracts.All
            .Select(contract => contract.Id.Value)
            .ToImmutableHashSet(StringComparer.Ordinal);

    internal static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    internal static bool IsStable(string value) => Pattern.IsMatch(value);

    [GeneratedRegex(
        "^[A-Za-z][A-Za-z0-9]*(?:[.-][A-Za-z0-9][A-Za-z0-9-]*)+$",
        RegexOptions.CultureInvariant)]
    private static partial Regex StableIdentityRegex();
}
