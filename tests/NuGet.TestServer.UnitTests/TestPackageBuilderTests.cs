using System.IO.Compression;
using System.Security.Cryptography;
using NuGet.Packaging;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class TestPackageBuilderTests
{
    private static readonly DateTime FixedZipTimestamp = new(1980, 1, 1);

    [Fact]
    public void Build_creates_a_readable_package_with_normalized_identity()
    {
        var package = TestPackageBuilder
            .Create("Example.Package", "1.0.0+build.7")
            .WithDescription("An example package")
            .WithDependency("Dependency.Package", "[2.0.0, 3.0.0)")
            .WithFile("lib/net10.0/example.txt", "content")
            .Build();

        Assert.Equal("Example.Package", package.Identity.Id);
        Assert.Equal("1.0.0", package.NormalizedVersion);

        using var reader = new PackageArchiveReader(new MemoryStream(package.Content));
        Assert.Equal("Example.Package", reader.GetIdentity().Id);
        Assert.Contains("lib/net10.0/example.txt", reader.GetFiles());
        Assert.Single(reader.GetPackageDependencies());
    }

    [Fact]
    public void Create_rejects_invalid_identity()
    {
        Assert.Throws<ArgumentException>(() => TestPackageBuilder.Create("", "1.0.0"));
        Assert.Throws<ArgumentException>(() => TestPackageBuilder.Create("Example", "not-a-version"));
    }

    [Fact]
    public void Build_preserves_rich_nuspec_metadata_and_computes_package_hash()
    {
        var package = TestPackageBuilder.Create("Example.Metadata", "1.0.0")
            .WithAuthors("Alice, Bob")
            .WithDescription("Description")
            .WithSummary("Summary")
            .WithTitle("Title")
            .WithProjectUrl("https://example.test/project")
            .WithReadme("README.md", "# Read me")
            .WithIcon("icon.png", [1, 2, 3])
            .WithLicenseExpression("MIT")
            .WithPackageType("DotnetTool", "1.0.0")
            .WithRepository("git", "https://example.test/repository.git", "abc123", "main")
            .Build();

        Assert.Equal("Summary", package.Summary);
        Assert.Equal("Title", package.Title);
        Assert.Equal(new Uri("https://example.test/project"), package.ProjectUrl);
        Assert.Equal("README.md", package.Readme);
        Assert.Equal("icon.png", package.Icon);
        Assert.Equal("MIT", package.LicenseExpression);
        Assert.Equal("DotnetTool", Assert.Single(package.PackageTypes).Name);
        Assert.Equal("git", package.Repository?.Type);
        Assert.Equal("abc123", package.Repository?.Commit);
        Assert.Equal(
            Convert.ToBase64String(SHA512.HashData(package.Content)),
            package.PackageHash);
    }

    [Fact]
    public void Build_is_deterministic_for_identical_inputs()
    {
        using var first = BuildDeterministicPackage();
        using var second = BuildDeterministicPackage();

        Assert.Equal(first.Content, second.Content);
        Assert.Equal(first.PackageHash, second.PackageHash);

        using var archive = new ZipArchive(new MemoryStream(first.Content), ZipArchiveMode.Read);
        Assert.All(
            archive.Entries,
            entry => Assert.Equal(FixedZipTimestamp, entry.LastWriteTime.DateTime));
    }

    private static TestPackage BuildDeterministicPackage() =>
        TestPackageBuilder.Create("Example.Deterministic", "1.0.0")
            .WithDependency("Dependency.Package", "[2.0.0, 3.0.0)")
            .WithFile("lib/net10.0/example.txt", "content")
            .Build();
}
