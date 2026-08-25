using NuGet.Packaging;
using System.Xml.Linq;

namespace NuExtVault.Extensions.Sdk.Tests;

public sealed class PackagingContractTests
{
    [Fact]
    public async Task Sdk_and_testkit_projects_pack_only_net10_contract_assets()
    {
        var output = Path.Combine(
            TestPaths.RepositoryRoot,
            "artifacts",
            "step19-sdk-tests",
            "packages-under-test");
        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }
        Directory.CreateDirectory(output);

        var sdk = PackAsync("NuExtVault.Extensions.Sdk", output);
        var testKit = PackAsync("NuExtVault.Extensions.TestKit", output);
        var results = await Task.WhenAll(sdk, testKit);
        Assert.Equal(0, results[0].ExitCode);
        Assert.Equal(0, results[1].ExitCode);

        AssertPackage(
            Path.Combine(output, "NuExtVault.Extensions.Sdk.1.4.0.nupkg"),
            [
                "lib/net10.0/NuExtVault.Extensions.Sdk.dll",
                "lib/net10.0/NuExtVault.Extensions.Sdk.xml",
                "contentFiles/any/any/nuextvault/extension-manifest-v1.schema.json",
                "contentFiles/any/any/nuextvault/extension-manifest-v2.schema.json"
            ]);
        AssertPackage(
            Path.Combine(output, "NuExtVault.Extensions.TestKit.1.1.0.nupkg"),
            [
                "lib/net10.0/NuExtVault.Extensions.TestKit.dll",
                "lib/net10.0/NuExtVault.Extensions.TestKit.xml"
            ]);
    }

    [Fact]
    public async Task Separately_compiled_consumer_and_template_output_build_and_pack()
    {
        var fixture = Path.Combine(
            "tests",
            "NuExtVault.SdkFixture",
            "NuExtVault.SdkFixture.csproj");
        var result = await TestPaths.DotNetAsync(
            "pack",
            fixture,
            "--configuration",
            "Release",
            "--no-restore",
            "-p:TreatWarningsAsErrors=true",
            "--output",
            Path.Combine("artifacts", "step19-sdk-tests", "fixture"));

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.True(File.Exists(Path.Combine(
            TestPaths.RepositoryRoot,
            "artifacts",
            "step19-sdk-tests",
            "fixture",
            "Contoso.Flavors.1.2.3.nupkg")));
    }

    [Fact]
    public void Package_projects_are_net10_only_and_do_not_bundle_host_implementation()
    {
        foreach (var project in new[]
                 {
                     "NuExtVault.Extensions.Sdk",
                     "NuExtVault.Extensions.TestKit"
                 })
        {
            var path = Path.Combine(TestPaths.RepositoryRoot, "src", project, $"{project}.csproj");
            var xml = File.ReadAllText(path);
            var references = XDocument.Parse(xml)
                .Descendants()
                .Where(element =>
                    element.Name.LocalName is "ProjectReference" or "PackageReference")
                .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
                .ToArray();
            Assert.Contains("<TargetFramework>net10.0</TargetFramework>", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("<TargetFrameworks>", xml, StringComparison.Ordinal);
            Assert.DoesNotContain(
                references,
                reference => reference.Contains("NuExtVault.Kernel", StringComparison.Ordinal));
            Assert.DoesNotContain(
                references,
                reference => reference.Contains(
                    "NuExtVault.Extensions.Official",
                    StringComparison.Ordinal));
        }
    }

    private static Task<ProcessResult> PackAsync(string project, string output) =>
        TestPaths.DotNetAsync(
            "pack",
            Path.Combine("src", project, $"{project}.csproj"),
            "--configuration",
            "Release",
            "--no-restore",
            "-p:TreatWarningsAsErrors=true",
            "--output",
            Path.GetRelativePath(TestPaths.RepositoryRoot, output));

    private static void AssertPackage(string path, string[] required)
    {
        Assert.True(File.Exists(path), $"Package '{path}' was not produced.");
        using var reader = new PackageArchiveReader(path);
        var files = reader.GetFiles().Order(StringComparer.Ordinal).ToArray();
        Assert.All(required, entry => Assert.Contains(entry, files));
        Assert.DoesNotContain(
            files,
            entry => entry.EndsWith("NuExtVault.dll", StringComparison.Ordinal) ||
                     entry.EndsWith("NuExtVault.Kernel.dll", StringComparison.Ordinal) ||
                     entry.EndsWith("NuExtVault.Extensions.Official.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(files, entry => entry.StartsWith("lib/net8", StringComparison.Ordinal));
        Assert.DoesNotContain(files, entry => entry.StartsWith("lib/net9", StringComparison.Ordinal));
    }
}
