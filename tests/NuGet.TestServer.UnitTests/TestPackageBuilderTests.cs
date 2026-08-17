using NuGet.Packaging;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class TestPackageBuilderTests
{
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
}
