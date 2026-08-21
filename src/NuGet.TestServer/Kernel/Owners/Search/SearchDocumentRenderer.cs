using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Kernel.Owners.Search;

internal static class SearchDocumentRenderer
{
    public static Dictionary<string, object?> Render(SearchResultDocument result) =>
        new()
        {
            ["@id"] = result.Id,
            ["@type"] = "Package",
            ["registration"] = result.Registration,
            ["id"] = result.Package.Id,
            ["version"] = result.Package.Version,
            ["description"] = result.Description,
            ["summary"] = result.Summary,
            ["title"] = result.Title,
            ["tags"] = result.Tags,
            ["authors"] = result.Authors,
            ["owners"] = result.Owners,
            ["projectUrl"] = result.ProjectUrl,
            ["totalDownloads"] = result.TotalDownloads,
            ["verified"] = result.Verified,
            ["packageTypes"] = result.PackageTypes
                .Select(type => new { name = type.Name, version = type.Version })
                .ToArray(),
            ["versions"] = result.Versions
                .Select(version => new Dictionary<string, object?>
                {
                    ["version"] = version.Version,
                    ["downloads"] = version.Downloads,
                    ["@id"] = version.Id
                })
                .ToArray()
        };
}
