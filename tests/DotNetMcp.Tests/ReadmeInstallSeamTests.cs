namespace DotNetMcp.Tests;

public class ReadmeInstallSeamTests
{
    [Fact]
    public void quick_start_leads_with_source_run_not_unpublished_dnx()
    {
        var readme = File.ReadAllText(Path.Combine(FindRepoRoot(), "README.md"));
        var start = readme.IndexOf("## Quick Start", StringComparison.Ordinal);
        Assert.True(start >= 0, "README.md is missing ## Quick Start.");
        var rest = readme[start..];

        var bash = IndexOfFence(rest, "```bash");
        var json = IndexOfFence(rest, "```json");
        Assert.True(bash >= 0, "Quick Start has no bash fence.");
        Assert.True(json >= 0, "Quick Start has no mcpServers json fence.");

        var firstBash = FenceBody(rest, bash);
        Assert.DoesNotContain("dnx Skymly.DotNetMcp", firstBash, StringComparison.Ordinal);

        var firstJson = FenceBody(rest, json);
        Assert.DoesNotContain("\"command\": \"dnx\"", firstJson, StringComparison.Ordinal);

        Assert.Contains("dnx Skymly.DotNetMcp", rest, StringComparison.Ordinal);
        Assert.Contains("after the package is published", rest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void server_json_does_not_advertise_unpublished_nuget_dnx_package()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "DotNetMcp.Server", ".mcp", "server.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var nugetOrDnx = false;
        if (root.TryGetProperty("packages", out var packages) && packages.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var package in packages.EnumerateArray())
            {
                var registry = package.TryGetProperty("registryType", out var registryType)
                    ? registryType.GetString()
                    : null;
                var hint = package.TryGetProperty("runtimeHint", out var runtimeHint)
                    ? runtimeHint.GetString()
                    : null;
                if (string.Equals(registry, "nuget", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(hint, "dnx", StringComparison.OrdinalIgnoreCase))
                {
                    nugetOrDnx = true;
                }
            }
        }

        Assert.False(
            nugetOrDnx,
            "server.json must not advertise a NuGet/dnx package while Skymly.DotNetMcp is unpublished.");
    }

    private static int IndexOfFence(string text, string fence) =>
        text.IndexOf(fence + Environment.NewLine, StringComparison.Ordinal) >= 0
            ? text.IndexOf(fence + Environment.NewLine, StringComparison.Ordinal)
            : text.IndexOf(fence + "\n", StringComparison.Ordinal);

    private static string FenceBody(string text, int fenceStart)
    {
        var nl = text.IndexOf('\n', fenceStart);
        var end = text.IndexOf("```", nl + 1, StringComparison.Ordinal);
        return text[(nl + 1)..end];
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
            if (File.Exists(Path.Combine(candidate, "README.md")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate README.md from the test host.");
    }
}
