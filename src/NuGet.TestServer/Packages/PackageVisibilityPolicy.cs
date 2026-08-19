namespace NuGet.TestServer.Packages;

internal readonly record struct PackageAuthorityFacts(
    PackageModerationState ModerationState,
    bool IsListed);

internal enum PackageResourceClass
{
    ExactContent,
    VersionEnumeration,
    Registration,
    Search,
    Symbols
}

internal sealed class PackagePublicGrantSet
{
    private readonly HashSet<PackageResourceClass> _resourceClasses;

    private PackagePublicGrantSet(IEnumerable<PackageResourceClass> resourceClasses)
    {
        _resourceClasses = resourceClasses.ToHashSet();
        if (_resourceClasses.Any(resourceClass => !Enum.IsDefined(resourceClass)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(resourceClasses),
                "Public resource grants must use defined resource classes.");
        }
    }

    public static PackagePublicGrantSet Create(
        IEnumerable<PackageResourceClass> resourceClasses)
    {
        ArgumentNullException.ThrowIfNull(resourceClasses);
        return new PackagePublicGrantSet(resourceClasses);
    }

    public bool Contains(PackageResourceClass resourceClass) =>
        Enum.IsDefined(resourceClass) && _resourceClasses.Contains(resourceClass);
}

internal sealed class PackageVisibilityPolicy
{
    private static readonly PackagePublicGrantSet NoPublicResources =
        PackagePublicGrantSet.Create([]);
    private static readonly PackagePublicGrantSet UnlistedResources =
        PackagePublicGrantSet.Create(
        [
            PackageResourceClass.ExactContent,
            PackageResourceClass.VersionEnumeration,
            PackageResourceClass.Registration,
            PackageResourceClass.Symbols
        ]);
    private static readonly PackagePublicGrantSet ListedResources =
        PackagePublicGrantSet.Create(
        [
            PackageResourceClass.ExactContent,
            PackageResourceClass.VersionEnumeration,
            PackageResourceClass.Registration,
            PackageResourceClass.Search,
            PackageResourceClass.Symbols
        ]);

    public static PackageVisibilityPolicy Instance { get; } = new();

    private PackageVisibilityPolicy()
    {
    }

    public bool CanRead(TestPackage package, PackageResourceClass resourceClass)
    {
        ArgumentNullException.ThrowIfNull(package);
        return CanRead(
            new PackageAuthorityFacts(package.ModerationState, package.IsListed),
            resourceClass);
    }

    public bool CanRead(
        PackageModerationState moderationState,
        bool listed,
        PackageResourceClass resourceClass) =>
        CanRead(new PackageAuthorityFacts(moderationState, listed), resourceClass);

    public bool CanRead(
        PackageAuthorityFacts facts,
        PackageResourceClass resourceClass) =>
        ResolvePublicGrants(facts).Contains(resourceClass);

    public PackagePublicGrantSet ResolvePublicGrants(PackageAuthorityFacts facts)
    {
        if (!Enum.IsDefined(facts.ModerationState) ||
            facts.ModerationState != PackageModerationState.Published)
        {
            return NoPublicResources;
        }

        return facts.IsListed ? ListedResources : UnlistedResources;
    }
}
