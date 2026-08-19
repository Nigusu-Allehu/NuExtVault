using NuGet.TestServer.Packages;
using NuGet.Versioning;

namespace NuGet.TestServer.Hosting;

internal static class RegistrationPageBounds
{
    public static bool Matches(
        IReadOnlyList<TestPackage> packages,
        string lower,
        string upper)
    {
        if (packages.Count == 0 ||
            !NuGetVersion.TryParse(lower, out var lowerVersion) ||
            !NuGetVersion.TryParse(upper, out var upperVersion))
        {
            return false;
        }

        return string.Equals(
                   packages[0].NormalizedVersion,
                   TestPackage.NormalizeVersion(lowerVersion),
                   StringComparison.Ordinal) &&
               string.Equals(
                   packages[^1].NormalizedVersion,
                   TestPackage.NormalizeVersion(upperVersion),
                   StringComparison.Ordinal);
    }
}
