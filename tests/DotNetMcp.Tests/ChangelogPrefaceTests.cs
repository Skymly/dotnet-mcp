namespace DotNetMcp.Tests;

public class ChangelogPrefaceTests
{
    [Fact]
    public void preface_does_not_claim_every_heading_has_a_git_tag()
    {
        var changelog = File.ReadAllText(Path.Combine(FindRepoRoot(), "CHANGELOG.md"));
        var preface = changelog.Split('\n').First(line => line.StartsWith("All notable product changes", StringComparison.Ordinal));
        Assert.DoesNotContain("git tags", preface, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not guaranteed", preface, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DotNetMcp.Server.csproj", preface, StringComparison.Ordinal);
    }

    [Fact]
    public void unreleased_records_csharp_vb_illegal_rename_rejection()
    {
        var changelog = File.ReadAllText(Path.Combine(FindRepoRoot(), "CHANGELOG.md"));
        var unreleased = System.Text.RegularExpressions.Regex.Match(
            changelog,
            "(?ms)^## Unreleased\\b(.*?)(?=^## |\\z)").Groups[1].Value;
        Assert.Contains("(`#292`)", unreleased, StringComparison.Ordinal);
        Assert.Contains("InvalidRenameName", unreleased, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CHANGELOG.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate CHANGELOG.md.");
    }
}