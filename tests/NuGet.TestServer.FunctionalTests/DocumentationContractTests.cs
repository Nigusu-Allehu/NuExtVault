using System.Text.RegularExpressions;

namespace NuGet.TestServer.FunctionalTests;

public sealed partial class DocumentationContractTests
{
    private static readonly string[] Chapters =
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

    [Fact]
    public void User_manual_contains_every_chapter()
    {
        var missing = Chapters
            .Where(chapter => !File.Exists(Path.Combine(UserManualRoot, chapter)))
            .ToArray();

        Assert.True(missing.Length == 0, $"Missing user-manual chapters: {string.Join(", ", missing)}");
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
        foreach (var chapter in Chapters)
        {
            var path = Path.Combine(UserManualRoot, chapter);
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
                Assert.Contains("[User manual](README.md)", markdown, StringComparison.Ordinal);
                Assert.Contains("**Previous:**", markdown, StringComparison.Ordinal);
                Assert.Contains("**Next:**", markdown, StringComparison.Ordinal);
            }
        }
    }

    internal static IReadOnlyList<DocumentationExample> ReadExamples()
    {
        var examples = new List<DocumentationExample>();
        foreach (var chapter in Chapters)
        {
            var path = Path.Combine(UserManualRoot, chapter);
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

        return examples;
    }

    internal static string UserManualRoot { get; } =
        Path.Combine(FindRepositoryRoot(), "docs", "user");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "NuGet.TestServer.slnx")))
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
