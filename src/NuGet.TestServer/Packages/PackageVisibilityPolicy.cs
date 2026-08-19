namespace NuGet.TestServer.Packages;

internal enum PackageLifecycleState
{
    Staged,
    Quarantined,
    Published,
    Unlisted,
    Deleted,
    Recovered
}

internal enum PackageResourceClass
{
    ExactContent,
    VersionEnumeration,
    Registration,
    Search,
    Symbols,
    Administrative
}

internal sealed class PackageVisibilityPolicy
{
    public static PackageVisibilityPolicy Instance { get; } = new();

    private PackageVisibilityPolicy()
    {
    }

    public bool CanRead(TestPackage package, PackageResourceClass resourceClass)
    {
        ArgumentNullException.ThrowIfNull(package);
        return CanRead(GetState(package.ModerationState, package.IsListed), resourceClass);
    }

    public bool CanRead(
        PackageModerationState moderationState,
        bool listed,
        PackageResourceClass resourceClass) =>
        CanRead(GetState(moderationState, listed), resourceClass);

    public bool CanRead(PackageLifecycleState state, PackageResourceClass resourceClass)
    {
        if (!Enum.IsDefined(state) || !Enum.IsDefined(resourceClass))
        {
            return false;
        }

        if (resourceClass == PackageResourceClass.Administrative)
        {
            return true;
        }

        return state switch
        {
            PackageLifecycleState.Published => true,
            PackageLifecycleState.Unlisted => resourceClass != PackageResourceClass.Search,
            _ => false
        };
    }

    public PackageLifecycleState GetState(
        PackageModerationState moderationState,
        bool listed) =>
        moderationState switch
        {
            PackageModerationState.Published => listed
                ? PackageLifecycleState.Published
                : PackageLifecycleState.Unlisted,
            PackageModerationState.Quarantined or PackageModerationState.Rejected =>
                PackageLifecycleState.Quarantined,
            PackageModerationState.Deleted => PackageLifecycleState.Deleted,
            _ => PackageLifecycleState.Quarantined
        };

    public PackageModerationState GetPersistedModerationState(PackageLifecycleState state) =>
        state switch
        {
            PackageLifecycleState.Published or PackageLifecycleState.Unlisted =>
                PackageModerationState.Published,
            PackageLifecycleState.Deleted => PackageModerationState.Deleted,
            _ => PackageModerationState.Quarantined
        };
}
