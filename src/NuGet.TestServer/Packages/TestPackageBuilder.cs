using System.IO.Compression;
using System.Security;
using System.Text;
using NuGet.Versioning;

namespace NuGet.TestServer.Packages;

public sealed class TestPackageBuilder
{
    private readonly string _id;
    private readonly NuGetVersion _version;
    private readonly List<(string Id, string Range)> _dependencies = [];
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private string _description = "Test package";
    private string _authors = "NuGet Test Server";
    private string _tags = string.Empty;

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

        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{Escape(_id)}</id>
                <version>{Escape(_version.ToFullString())}</version>
                <authors>{Escape(_authors)}</authors>
                <description>{Escape(_description)}</description>
                <tags>{Escape(_tags)}</tags>
                {dependencies}
              </metadata>
            </package>
            """;
        return Encoding.UTF8.GetBytes(xml);
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static bool NuGetVersionRangeIsValid(string range) =>
        NuGet.Versioning.VersionRange.TryParse(range, out _);
}
