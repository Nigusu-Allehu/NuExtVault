using NuGet.Packaging;
using NuExtVault.Extensions.Official;

namespace NuExtVault.UnitTests;

/// <summary>
/// The official extension assembly may not depend on a packaging implementation library,
/// so it validates package identifiers itself. This test pins that local rule to the
/// authoritative NuGet rule.
/// </summary>
public sealed class PackageIdSyntaxTests
{
    public static TheoryData<string> Candidates =>
    [
        "Contoso.Utilities",
        "contoso.utilities",
        "Contoso-Utilities",
        "Contoso_Utilities",
        "a",
        "a.b-c_d",
        "1",
        "1.2.3",
        "_leading",
        "with space",
        "with/slash",
        "with\\backslash",
        "trailing.",
        ".leading",
        "double..dot",
        "double--dash",
        "double__underscore",
        "unicode\u00e9",
        "semi;colon",
        "at@sign",
        "plus+sign"
    ];

    [Theory]
    [MemberData(nameof(Candidates))]
    public void The_local_rule_matches_the_authoritative_nuget_rule(string candidate)
    {
        Assert.Equal(
            PackageIdValidator.IsValidPackageId(candidate),
            PackageIdSyntax.IsValid(candidate));
    }

    [Fact]
    public void The_local_rule_matches_the_authoritative_rule_for_long_identifiers()
    {
        var atLimit = new string('a', 100);
        var overLimit = new string('a', 101);

        Assert.Equal(PackageIdValidator.IsValidPackageId(atLimit), PackageIdSyntax.IsValid(atLimit));
        Assert.Equal(
            PackageIdValidator.IsValidPackageId(overLimit),
            PackageIdSyntax.IsValid(overLimit));
    }
}
