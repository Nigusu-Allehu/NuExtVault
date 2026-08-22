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
    ContributionContractVersion Version);

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
        int timeoutMilliseconds)
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
}

public static class ExtensionSdkVersions
{
    public const string ConformanceSuiteV1 = "NuGet.TestServer.Extensions.Conformance/v1";

    public static SdkContractIdentity Identity { get; } =
        new("NuGet.TestServer.Extensions.Sdk");

    public static SdkContractVersion Current { get; } = new(1, 2, 0);

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

public sealed class RouteBindingRequest
{
    private readonly IReadOnlyDictionary<string, string> _route;
    private readonly IReadOnlyDictionary<string, string> _query;
    private readonly BoundedDocument _body;

    internal RouteBindingRequest(
        IReadOnlyDictionary<string, string> route,
        IReadOnlyDictionary<string, string> query,
        BoundedDocument body)
    {
        _route = route;
        _query = query;
        _body = body;
    }

    public string GetRoute(string name) =>
        _route.TryGetValue(name, out var value)
            ? value
            : throw new KeyNotFoundException($"Route value '{name}' is not available.");

    public string GetQuery(string name) =>
        _query.TryGetValue(name, out var value)
            ? value
            : throw new KeyNotFoundException($"Query value '{name}' is not available.");

    public BoundedDocument ReadBody() => _body;
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
