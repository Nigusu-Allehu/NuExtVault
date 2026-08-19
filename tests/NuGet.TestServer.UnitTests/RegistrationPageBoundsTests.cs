using NuGet.TestServer.Hosting;
using NuGet.TestServer.Packages;

namespace NuGet.TestServer.UnitTests;

public sealed class RegistrationPageBoundsTests
{
    private static readonly IReadOnlyList<TestPackage> Packages =
    [
        TestPackageBuilder.Create("Example.Package", "1.0.0").Build(),
        TestPackageBuilder.Create("Example.Package", "2.0.0-beta.1").Build(),
        TestPackageBuilder.Create("Example.Package", "2.0.0").Build()
    ];

    [Theory]
    [InlineData("1.0", "2.0.0", true)]
    [InlineData("1.0.0", "2.0.0-beta.1", false)]
    [InlineData("not-a-version", "2.0.0", false)]
    [InlineData("1.0.0", "not-a-version", false)]
    public void Matches_requires_the_normalized_first_and_last_versions(
        string lower,
        string upper,
        bool expected)
    {
        Assert.Equal(expected, RegistrationPageBounds.Matches(Packages, lower, upper));
    }

    [Fact]
    public void Matches_rejects_an_empty_package_page()
    {
        Assert.False(RegistrationPageBounds.Matches([], "1.0.0", "2.0.0"));
    }
}
