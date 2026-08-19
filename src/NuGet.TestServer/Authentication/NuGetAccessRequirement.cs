namespace NuGet.TestServer.Authentication;

public enum NuGetAccessKind
{
    Anonymous,
    Read,
    Publish,
    Unlist,
    Delete,
    Admin,
    Write,
    Control
}

public sealed record NuGetAccessRequirement(NuGetAccessKind Kind)
{
    public static NuGetAccessRequirement Anonymous { get; } =
        new(NuGetAccessKind.Anonymous);
    public static NuGetAccessRequirement Read { get; } =
        new(NuGetAccessKind.Read);
    public static NuGetAccessRequirement Write { get; } =
        new(NuGetAccessKind.Write);
    public static NuGetAccessRequirement Publish { get; } =
        new(NuGetAccessKind.Publish);
    public static NuGetAccessRequirement Unlist { get; } =
        new(NuGetAccessKind.Unlist);
    public static NuGetAccessRequirement Delete { get; } =
        new(NuGetAccessKind.Delete);
    public static NuGetAccessRequirement Admin { get; } =
        new(NuGetAccessKind.Admin);
    public static NuGetAccessRequirement Control { get; } =
        new(NuGetAccessKind.Control);
}
