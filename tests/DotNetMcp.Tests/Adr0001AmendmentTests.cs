using System.Text.RegularExpressions;

namespace DotNetMcp.Tests;

public class Adr0001AmendmentTests
{
    [Fact]
    public void amendment_6_names_only_the_product_projects_on_disk()
    {
        var root = FindRepoRoot();
        var adr = File.ReadAllText(Path.Combine(root, "docs", "adr", "0001-core-interface-llm-first-symbol-handles.md"));
        Assert.Contains("2026-10-11 Amendment 6", adr, StringComparison.Ordinal);

        var modules = adr.IndexOf("## 模块分解", StringComparison.Ordinal);
        var amendment = adr.IndexOf("## Amendment 6", StringComparison.Ordinal);
        Assert.True(modules >= 0 && amendment > modules);
        Assert.Contains("DotNetMcp.Workspace", adr[modules..amendment], StringComparison.Ordinal);

        var productProjects = Regex.Matches(
                File.ReadAllText(Path.Combine(root, "DotNetMcp.slnx")),
                "<Project Path=\"(src/[^\"]+\\.csproj)\"")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(4, productProjects.Length);

        var named = Regex.Matches(adr[amendment..], @"src/[\w.]+/[\w.]+\.csproj")
            .Select(m => m.Value.Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(productProjects, named);
        Assert.DoesNotContain(named, p => p.Contains("Workspace", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepoRoot()
    {
        var candidates = new[]
        {
            Environment.CurrentDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "DotNetMcp.slnx")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root from the test host.");
    }
}
