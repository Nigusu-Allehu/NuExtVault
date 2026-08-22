using NuGet.TestServer.Extensions.Sdk;

namespace NuGet.TestServer.Extensions.Registration;

internal static class RegistrationDocumentRenderer
{
    public static Dictionary<string, object?> RenderPage(RegistrationPageDocument page) => new()
    {
        ["@id"] = page.Id,
        ["@type"] = "catalog:CatalogPage",
        ["parent"] = page.Parent,
        ["count"] = page.Count,
        ["lower"] = page.Lower,
        ["upper"] = page.Upper,
        ["items"] = page.Items.Select(RenderLeaf).ToArray()
    };

    public static Dictionary<string, object?> RenderLeaf(RegistrationLeafDocument leaf)
    {
        var catalogEntry = new Dictionary<string, object?>
        {
            ["@id"] = leaf.Id,
            ["@type"] = "PackageDetails",
            ["id"] = leaf.Package.Id,
            ["version"] = leaf.Package.Version,
            ["authors"] = leaf.Authors,
            ["owners"] = leaf.Owners,
            ["downloads"] = leaf.Downloads,
            ["description"] = leaf.Description,
            ["summary"] = leaf.Summary,
            ["title"] = leaf.Title,
            ["tags"] = leaf.Tags,
            ["projectUrl"] = leaf.ProjectUrl,
            ["readme"] = leaf.Readme,
            ["icon"] = leaf.Icon,
            ["licenseExpression"] = leaf.LicenseExpression,
            ["licenseFile"] = leaf.LicenseFile,
            ["licenseUrl"] = leaf.LicenseUrl,
            ["packageTypes"] = leaf.PackageTypes
                .Select(type => new { name = type.Name, version = type.Version })
                .ToArray(),
            ["repository"] = leaf.Repository is null
                ? null
                : new
                {
                    type = leaf.Repository.Type,
                    url = leaf.Repository.Url,
                    commit = leaf.Repository.Commit,
                    branch = leaf.Repository.Branch
                },
            ["listed"] = leaf.Listed,
            ["published"] = leaf.Published,
            ["dependencyGroups"] = leaf.DependencyGroups
                .Select(group => new
                {
                    targetFramework = group.TargetFramework,
                    dependencies = group.Dependencies
                        .Select(dependency => new
                        {
                            id = dependency.Id,
                            range = dependency.Range
                        })
                        .ToArray()
                })
                .ToArray()
        };
        if (leaf.Deprecation is { } deprecation)
        {
            catalogEntry["deprecation"] = new
            {
                reasons = deprecation.Reasons,
                message = deprecation.Message,
                alternatePackage = deprecation.AlternatePackage is null
                    ? null
                    : new
                    {
                        id = deprecation.AlternatePackage.Id,
                        range = deprecation.AlternatePackage.Range
                    }
            };
        }

        if (leaf.Vulnerabilities.Length > 0)
        {
            catalogEntry["vulnerabilities"] = leaf.Vulnerabilities
                .Select(advisory => new
                {
                    advisoryUrl = advisory.AdvisoryUrl,
                    severity = advisory.Severity
                })
                .ToArray();
        }

        if (leaf.Extensions.Count > 0)
        {
            catalogEntry["extensions"] = leaf.Extensions.ToDictionary(
                item => item.Key,
                item => (object?)item.Value.Value,
                StringComparer.Ordinal);
        }

        return new Dictionary<string, object?>
        {
            ["@id"] = leaf.Id,
            ["@type"] = "Package",
            ["catalogEntry"] = catalogEntry,
            ["packageContent"] = leaf.PackageContent,
            ["registration"] = leaf.Registration
        };
    }
}
