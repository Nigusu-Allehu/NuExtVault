using NuGet.Versioning;

namespace NuExtVault.Extensions.Registration;

internal static class RegistrationPageBounds
{
    public static bool Matches(
        IReadOnlyList<string> normalizedVersions,
        string lower,
        string upper)
    {
        if (normalizedVersions.Count == 0 ||
            !NuGetVersion.TryParse(lower, out var lowerVersion) ||
            !NuGetVersion.TryParse(upper, out var upperVersion))
        {
            return false;
        }

        return string.Equals(
                   normalizedVersions[0],
                   Normalize(lowerVersion),
                   StringComparison.Ordinal) &&
               string.Equals(
                   normalizedVersions[^1],
                   Normalize(upperVersion),
                   StringComparison.Ordinal);
    }

    private static string Normalize(NuGetVersion version) =>
        version.ToNormalizedString().Split('+', 2)[0].ToLowerInvariant();
}
