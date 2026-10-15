using System.Text.RegularExpressions;

namespace DotNetMcp.Tests;

/// <summary>
/// Conventions of the GitHub workflows and the Dependabot configuration that nothing else enforces: every action
/// is pinned to a commit SHA, every workflow starts from a read-only token, CodeQL keeps the job name that branch
/// protection will refer to, and Dependabot leaves test material alone and keeps package families together.
/// </summary>
public partial class WorkflowSeamTests
{
    [Fact]
    public void every_action_is_pinned_to_a_commit_sha_with_a_version_comment()
    {
        var uses = WorkflowFiles()
            .SelectMany(file => File.ReadAllLines(file)
                .Select((text, index) => (File: Path.GetFileName(file), Line: index + 1, Text: text))
                .Where(static l => UsesLine().IsMatch(l.Text)))
            .ToArray();
        Assert.NotEmpty(uses);

        foreach (var (file, line, text) in uses)
        {
            var match = UsesLine().Match(text);
            var action = match.Groups["action"].Value;
            if (action.StartsWith("./", StringComparison.Ordinal) || action.StartsWith("docker://", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(
                Regex.IsMatch(match.Groups["ref"].Value, "^[0-9a-f]{40}$"),
                $"{file}:{line} pins {action} to '{match.Groups["ref"].Value}', not a 40-character commit SHA.");
            Assert.True(
                Regex.IsMatch(match.Groups["comment"].Value, @"^v\d+\.\d+(\.\d+)?\b"),
                $"{file}:{line} pins {action} to a SHA but has no '# vX.Y.Z' comment saying which release it is.");
        }
    }

    [Fact]
    public void every_workflow_declares_top_level_permissions()
    {
        foreach (var file in WorkflowFiles())
        {
            Assert.True(
                File.ReadAllLines(file).Any(static line => line.StartsWith("permissions:", StringComparison.Ordinal)),
                $"{Path.GetFileName(file)} has no top-level permissions block, so jobs get the repository default token.");
        }
    }

    [Fact]
    public void codeql_workflow_keeps_the_job_name_and_skips_test_material_without_filtering_pull_requests()
    {
        var lines = File.ReadAllLines(Path.Combine(WorkflowsDirectory(), "codeql.yml"));

        // Branch protection refers to the job name. Renaming it silently removes the required check.
        Assert.Contains("    name: CodeQL", lines);
        Assert.Contains(lines, static line => line.Trim() == "languages: csharp");
        Assert.Contains(lines, static line => line.Trim() == "build-mode: none");

        var ignored = lines
            .SkipWhile(static line => line.Trim() != "paths-ignore:")
            .Skip(1)
            .TakeWhile(static line => line.Trim().StartsWith("- ", StringComparison.Ordinal))
            .Select(static line => line.Trim()[2..])
            .ToArray();
        Assert.Contains("tests/fixtures", ignored);
        Assert.Contains("spikes", ignored);

        // A required check that is skipped for some pull requests blocks them forever.
        var triggers = TopLevelSection(lines, "on:");
        Assert.DoesNotContain(triggers, static line => line.Trim().StartsWith("paths", StringComparison.Ordinal));
        Assert.Contains(triggers, static line => line.Trim() == "pull_request:");
    }

    [Fact]
    public void dependabot_updates_nuget_and_actions_without_touching_test_material()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "dependabot.yml"));
        var lines = text.Split('\n').Select(static line => line.Trim()).ToArray();

        Assert.Contains("- package-ecosystem: nuget", lines);
        Assert.Contains("- package-ecosystem: github-actions", lines);

        // Fixtures and spikes pin their own versions on purpose.
        Assert.Contains("- \"tests/fixtures/**\"", lines);
        Assert.Contains("- \"spikes/**\"", lines);

        // Packages of one family must move together (see CentralPackageSeamTests).
        Assert.Contains("- \"Microsoft.CodeAnalysis*\"", lines);
        Assert.Contains("- \"ModelContextProtocol*\"", lines);
        Assert.Contains("- \"Microsoft.Extensions.*\"", lines);

        // Until #498 is resolved, 1.10 and later fail the build with MSBL001.
        Assert.Contains("- dependency-name: \"Microsoft.Build.Locator\"", lines);
    }

    private static string WorkflowsDirectory() => Path.Combine(FindRepoRoot(), ".github", "workflows");

    private static string[] WorkflowFiles()
    {
        var files = Directory.GetFiles(WorkflowsDirectory(), "*.yml");
        Assert.NotEmpty(files);
        return files;
    }

    /// <summary>The indented lines under a top-level key such as <c>on:</c>.</summary>
    private static string[] TopLevelSection(string[] lines, string key)
    {
        var start = Array.FindIndex(lines, line => line == key);
        Assert.True(start >= 0, $"No top-level '{key}' in the workflow.");
        return lines.Skip(start + 1).TakeWhile(static line => line.Length == 0 || char.IsWhiteSpace(line[0])).ToArray();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotNetMcp.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test host.");
    }

    [GeneratedRegex(@"^\s*(-\s+)?uses:\s*(?<action>[^@\s]+)@(?<ref>[^\s#]+)\s*(#\s*(?<comment>.*))?$")]
    private static partial Regex UsesLine();
}
