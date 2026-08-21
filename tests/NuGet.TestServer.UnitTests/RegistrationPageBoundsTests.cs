using NuGet.TestServer.Kernel.Owners.Registration;

namespace NuGet.TestServer.UnitTests;

public sealed class RegistrationPageBoundsTests
{
    private static readonly IReadOnlyList<string> Versions =
    [
        "1.0.0",
        "2.0.0-beta.1",
        "2.0.0"
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
        Assert.Equal(expected, RegistrationPageBounds.Matches(Versions, lower, upper));
    }

    [Fact]
    public void Matches_rejects_an_empty_package_page()
    {
        Assert.False(RegistrationPageBounds.Matches([], "1.0.0", "2.0.0"));
    }
}
