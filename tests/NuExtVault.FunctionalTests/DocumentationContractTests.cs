using System.Text.RegularExpressions;

namespace NuExtVault.FunctionalTests;

public sealed partial class DocumentationContractTests
{
    private static readonly string[] UserChapters =
    [
        "README.md",
        "01-installation-and-quick-start.md",
        "02-package-workflows.md",
        "03-programmatic-testing.md",
        "04-authentication-and-production.md",
        "05-control-api-and-faults.md",
        "06-operations-and-recovery.md",
        "07-trusted-extensions-and-package-staging.md",
        "08-troubleshooting-limits-and-compatibility.md"
    ];

    private static readonly string[] ContributorChapters =
    [
        "README.md",
        "01-architecture-and-assemblies.md",
        "02-request-lifecycle.md",
        "03-extension-composition.md",
        "04-capabilities-and-security.md",
        "05-state-backup-and-recovery.md",
        "06-public-sdk-and-trusted-loading.md",
        "07-development-workflow.md",
        "08-build-test-and-release.md"
    ];

    [Fact]
    public void User_manual_contains_every_chapter()
    {
        var missing = UserChapters
            .Where(chapter => !File.Exists(Path.Combine(UserManualRoot, chapter)))
            .ToArray();

        Assert.True(missing.Length == 0, $"Missing user-manual chapters: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Contributor_manual_contains_every_chapter()
    {
        var missing = ContributorChapters
            .Where(chapter => !File.Exists(Path.Combine(ContributorManualRoot, chapter)))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"Missing contributor-manual chapters: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_fence_has_a_unique_stable_example_id_and_evidence_kind()
    {
        var examples = ReadExamples();
        var duplicateIds = examples
            .GroupBy(example => example.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.NotEmpty(examples);
        Assert.Empty(duplicateIds);
        Assert.All(examples, example =>
            Assert.Contains(example.Evidence, new[] { "executable", "reference" }));
    }

    [Fact]
    public void Relative_links_and_progressive_navigation_are_valid()
    {
        foreach (var (manualRoot, chapters, manualLink) in Manuals())
        {
            foreach (var chapter in chapters)
            {
                var path = Path.Combine(manualRoot, chapter);
                var markdown = File.ReadAllText(path);
                foreach (Match match in MarkdownLink().Matches(markdown))
                {
                    var target = match.Groups["target"].Value;
                    if (target.StartsWith('#') ||
                        Uri.TryCreate(target, UriKind.Absolute, out _) ||
                        target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var pathWithoutFragment = target.Split('#', 2)[0];
                    var resolved = Path.GetFullPath(
                        Path.Combine(Path.GetDirectoryName(path)!, pathWithoutFragment));
                    Assert.True(
                        File.Exists(resolved) || Directory.Exists(resolved),
                        $"{chapter} contains a broken link to '{target}'.");
                }

                if (chapter != "README.md")
                {
                    Assert.Contains(manualLink, markdown, StringComparison.Ordinal);
                    Assert.Contains("**Previous:**", markdown, StringComparison.Ordinal);
                    Assert.Contains("**Next:**", markdown, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void Root_readme_is_a_minimal_verified_landing_page()
    {
        var path = Path.Combine(RepositoryRoot, "README.md");
        var markdown = File.ReadAllText(path);
        Assert.True(File.ReadLines(path).Count() <= 40, "The root README must remain a minimal landing page.");
        Assert.Contains("actions/workflows/ci.yml/badge.svg", markdown, StringComparison.Ordinal);
        Assert.Contains(".NET SDK 10.0", markdown, StringComparison.Ordinal);
        Assert.Equal(
            "dotnet run --project .\\src\\NuExtVault.Cli -- start",
            RootQuickStartCommand());
        Assert.Contains("docs/user/README.md", markdown, StringComparison.Ordinal);
        Assert.Contains("docs/contributing/README.md", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Supported NuGet operations", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("## Contributing workflow", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_tests_have_one_dedicated_ci_owner()
    {
        var documentationWorkflowPath =
            Path.Combine(RepositoryRoot, ".github", "workflows", "documentation.yml");
        Assert.True(
            File.Exists(documentationWorkflowPath),
            "Missing dedicated documentation workflow at .github/workflows/documentation.yml.");

        var documentationWorkflow = File.ReadAllText(documentationWorkflowPath);
        Assert.Contains("name: Documentation examples", documentationWorkflow, StringComparison.Ordinal);
        Assert.Contains("contents: read", documentationWorkflow, StringComparison.Ordinal);
        Assert.Contains("windows-latest", documentationWorkflow, StringComparison.Ordinal);
        Assert.Contains("ubuntu-latest", documentationWorkflow, StringComparison.Ordinal);
        Assert.Contains("macos-latest", documentationWorkflow, StringComparison.Ordinal);
        Assert.Contains("--warnaserror", documentationWorkflow, StringComparison.Ordinal);
        Assert.Contains(
            "FullyQualifiedName~NuExtVault.FunctionalTests.DocumentationContractTests|FullyQualifiedName~NuExtVault.FunctionalTests.DocumentationExampleTests",
            documentationWorkflow,
            StringComparison.Ordinal);

        var generalWorkflow = File.ReadAllText(
            Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));
        Assert.Contains(
            "FullyQualifiedName!~NuExtVault.FunctionalTests.DocumentationContractTests&FullyQualifiedName!~NuExtVault.FunctionalTests.DocumentationExampleTests",
            generalWorkflow,
            StringComparison.Ordinal);
    }

    internal static string RootQuickStartCommand()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot, "README.md"));
        var opening = Array.FindIndex(lines, line => line.StartsWith("```", StringComparison.Ordinal));
        Assert.True(opening >= 0, "The root README must contain one quick-start fence.");
        Assert.Equal("```powershell", lines[opening]);
        var closing = Array.FindIndex(lines, opening + 1, line => line == "```");
        Assert.True(closing > opening, "The root README quick-start fence is unterminated.");
        Assert.DoesNotContain(lines[(closing + 1)..], line => line.StartsWith("```", StringComparison.Ordinal));
        return string.Join(Environment.NewLine, lines[(opening + 1)..closing]).Trim();
    }

    internal static IReadOnlyList<DocumentationExample> ReadExamples()
    {
        var examples = new List<DocumentationExample>();
        foreach (var (manualRoot, chapters, _) in Manuals())
        {
            foreach (var chapter in chapters)
            {
                var path = Path.Combine(manualRoot, chapter);
                var lines = File.ReadAllLines(path);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!lines[index].StartsWith("```", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Assert.True(index > 0, $"{chapter}:{index + 1} has an unmarked fence.");
                    var marker = ExampleMarker().Match(lines[index - 1]);
                    Assert.True(
                        marker.Success,
                        $"{chapter}:{index + 1} must be preceded by an example-id/evidence marker.");

                    var closing = Array.FindIndex(
                        lines,
                        index + 1,
                        line => line == "```");
                    Assert.True(closing >= 0, $"{chapter}:{index + 1} has an unterminated fence.");

                    examples.Add(new DocumentationExample(
                        marker.Groups["id"].Value,
                        marker.Groups["evidence"].Value,
                        lines[index][3..],
                        string.Join(Environment.NewLine, lines[(index + 1)..closing]),
                        chapter,
                        index + 1));
                    index = closing;
                }
            }
        }

        return examples;
    }

    internal static string RepositoryRoot { get; } = FindRepositoryRoot();

    internal static string UserManualRoot { get; } =
        Path.Combine(RepositoryRoot, "docs", "user");

    internal static string ContributorManualRoot { get; } =
        Path.Combine(RepositoryRoot, "docs", "contributing");

    private static IEnumerable<(string Root, string[] Chapters, string ManualLink)> Manuals()
    {
        yield return (UserManualRoot, UserChapters, "[User manual](README.md)");
        yield return (ContributorManualRoot, ContributorChapters, "[Contributor manual](README.md)");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "NuExtVault.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new InvalidOperationException("The repository root was not found.");
    }

    [GeneratedRegex(
        @"^<!-- example-id: (?<id>[a-z0-9]+(?:-[a-z0-9]+)*); evidence: (?<evidence>executable|reference) -->$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExampleMarker();

    [GeneratedRegex(@"\[[^\]]+\]\((?<target>[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();
}

internal sealed record DocumentationExample(
    string Id,
    string Evidence,
    string Language,
    string Content,
    string Chapter,
    int Line);
