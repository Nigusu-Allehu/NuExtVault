namespace NuGet.TestServer.Authentication;

public enum NuGetAccessKind
{
    Anonymous,
    Read,
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
    public static NuGetAccessRequirement Control { get; } =
        new(NuGetAccessKind.Control);
}
