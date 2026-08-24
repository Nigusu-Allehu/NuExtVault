using System.IO.Compression;
using System.Security;
using System.Text;
using NuGet.Versioning;

namespace NuGet.TestServer.Packages;

public sealed class TestPackageBuilder
{
    private static readonly DateTimeOffset ZipEntryTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _id;
    private readonly NuGetVersion _version;
    private readonly List<(string Id, string Range)> _dependencies = [];
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private string _description = "Test package";
    private string _summary = string.Empty;
    private string _title = string.Empty;
    private string _authors = "NuGet Test Server";
    private string _tags = string.Empty;
    private string _projectUrl = string.Empty;
    private string _readme = string.Empty;
    private string _icon = string.Empty;
    private string _license = string.Empty;
    private string _licenseType = string.Empty;
    private readonly List<(string Name, string Version)> _packageTypes = [];
    private (string Type, string Url, string Commit, string Branch)? _repository;

    private TestPackageBuilder(string id, NuGetVersion version)
    {
        _id = id;
        _version = version;
    }

    public static TestPackageBuilder Create(string id, string version)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A package ID is required.", nameof(id));
        }

        if (!NuGetVersion.TryParse(version, out var parsedVersion))
        {
            throw new ArgumentException("A valid NuGet version is required.", nameof(version));
        }

        return new TestPackageBuilder(id, parsedVersion);
    }

    public TestPackageBuilder WithDescription(string description)
    {
        _description = description ?? throw new ArgumentNullException(nameof(description));
        return this;
    }

    public TestPackageBuilder WithAuthors(string authors)
    {
        _authors = authors ?? throw new ArgumentNullException(nameof(authors));
        return this;
    }

    public TestPackageBuilder WithSummary(string summary)
    {
        _summary = summary ?? throw new ArgumentNullException(nameof(summary));
        return this;
    }

    public TestPackageBuilder WithTitle(string title)
    {
        _title = title ?? throw new ArgumentNullException(nameof(title));
        return this;
    }

    public TestPackageBuilder WithProjectUrl(string projectUrl)
    {
        _projectUrl = projectUrl ?? throw new ArgumentNullException(nameof(projectUrl));
        return this;
    }

    public TestPackageBuilder WithReadme(string path, string content)
    {
        _readme = path ?? throw new ArgumentNullException(nameof(path));
        return WithFile(path, content);
    }

    public TestPackageBuilder WithIcon(string path, byte[] content)
    {
        _icon = path ?? throw new ArgumentNullException(nameof(path));
        return WithFile(path, content);
    }

    public TestPackageBuilder WithLicenseExpression(string expression)
    {
        _licenseType = "expression";
        _license = expression ?? throw new ArgumentNullException(nameof(expression));
        return this;
    }

    public TestPackageBuilder WithLicenseFile(string path, string content)
    {
        _licenseType = "file";
        _license = path ?? throw new ArgumentNullException(nameof(path));
        return WithFile(path, content);
    }

    public TestPackageBuilder WithPackageType(string name, string version = "")
    {
        _packageTypes.Add((name, version));
        return this;
    }

    public TestPackageBuilder WithRepository(
        string type,
        string url,
        string commit = "",
        string branch = "")
    {
        _repository = (type, url, commit, branch);
        return this;
    }

    public TestPackageBuilder WithTags(string tags)
    {
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        return this;
    }

    public TestPackageBuilder WithDependency(string id, string versionRange)
    {
        if (string.IsNullOrWhiteSpace(id) || !NuGetVersionRangeIsValid(versionRange))
        {
            throw new ArgumentException("A dependency requires a valid ID and version range.");
        }

        _dependencies.Add((id, versionRange));
        return this;
    }

    public TestPackageBuilder WithFile(string path, string content) =>
        WithFile(path, Encoding.UTF8.GetBytes(content));

    public TestPackageBuilder WithFile(string path, byte[] content)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A package path is required.", nameof(path));
        }

        _files[path.Replace('\\', '/')] = content.ToArray();
        return this;
    }

    public TestPackage Build()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, $"{_id}.nuspec", BuildNuspec());
            foreach (var file in _files)
            {
                WriteEntry(archive, file.Key, file.Value);
            }
        }

        return TestPackage.FromContent(output.ToArray());
    }

    private byte[] BuildNuspec()
    {
        var dependencies = _dependencies.Count == 0
            ? string.Empty
            : $"<dependencies>{string.Concat(_dependencies.Select(d =>
                $"<dependency id=\"{Escape(d.Id)}\" version=\"{Escape(d.Range)}\" />"))}</dependencies>";
        var license = string.IsNullOrEmpty(_license)
            ? string.Empty
            : $"<license type=\"{_licenseType}\">{Escape(_license)}</license>";
        var packageTypes = _packageTypes.Count == 0
            ? string.Empty
            : $"<packageTypes>{string.Concat(_packageTypes.Select(type =>
                $"<packageType name=\"{Escape(type.Name)}\" version=\"{Escape(type.Version)}\" />"))}</packageTypes>";
        var repository = _repository is null
            ? string.Empty
            : $"<repository type=\"{Escape(_repository.Value.Type)}\" url=\"{Escape(_repository.Value.Url)}\" commit=\"{Escape(_repository.Value.Commit)}\" branch=\"{Escape(_repository.Value.Branch)}\" />";

        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{Escape(_id)}</id>
                <version>{Escape(_version.ToFullString())}</version>
                <authors>{Escape(_authors)}</authors>
                <title>{Escape(_title)}</title>
                <description>{Escape(_description)}</description>
                <summary>{Escape(_summary)}</summary>
                <tags>{Escape(_tags)}</tags>
                <projectUrl>{Escape(_projectUrl)}</projectUrl>
                <readme>{Escape(_readme)}</readme>
                <icon>{Escape(_icon)}</icon>
                {license}
                {packageTypes}
                {repository}
                {dependencies}
              </metadata>
            </package>
            """;
        return Encoding.UTF8.GetBytes(xml);
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        entry.LastWriteTime = ZipEntryTimestamp;
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static bool NuGetVersionRangeIsValid(string range) =>
        NuGet.Versioning.VersionRange.TryParse(range, out _);
}
