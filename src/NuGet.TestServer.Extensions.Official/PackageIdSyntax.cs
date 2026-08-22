using System.Text.RegularExpressions;

namespace NuGet.TestServer.Extensions.Official;

/// <summary>
/// Package identifier syntax. The official extensions validate identifiers without
/// taking a dependency on a packaging implementation library, so the extension assembly
/// stays on transport-neutral dependencies only. The rule is the NuGet package-ID rule:
/// dot- or hyphen-separated word segments, at most 100 characters.
/// </summary>
internal static partial class PackageIdSyntax
{
    public static bool IsValid(string packageId) =>
        packageId is not null && IdRegex().IsMatch(packageId);

    [GeneratedRegex(
        @"^\w+([_.-]\w+)*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 15000)]
    private static partial Regex IdRegex();
}
