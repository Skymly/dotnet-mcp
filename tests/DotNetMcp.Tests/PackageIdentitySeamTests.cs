namespace DotNetMcp.Tests;

public class PackageIdentitySeamTests
{
    [Fact]
    public void version_gate_rejects_unreleased_product_entries_at_previous_release()
    {
        var error = PackageIdentityGate.Evaluate(
            Csproj("4.0.1"),
            ServerJson("4.0.1"),
            UnreleasedChangelog("4.0.1", "- a product change (#295)"));

        Assert.NotNull(error);
        Assert.Contains("Unreleased", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4.0.1", error, StringComparison.Ordinal);
    }

    [Fact]
    public void version_gate_accepts_unreleased_when_version_moved_past_previous_release()
    {
        var error = PackageIdentityGate.Evaluate(
            Csproj("4.0.2"),
            ServerJson("4.0.2"),
            UnreleasedChangelog("4.0.1", "- a product change (#295)"));

        Assert.Null(error);
    }

    [Fact]
    public void version_gate_accepts_empty_unreleased_matching_release_heading()
    {
        var error = PackageIdentityGate.Evaluate(
            Csproj("4.0.1"),
            ServerJson("4.0.1"),
            UnreleasedChangelog("4.0.1", productEntry: null));

        Assert.Null(error);
    }

    [Fact]
    public void version_gate_rejects_csproj_server_json_mismatch()
    {
        var error = PackageIdentityGate.Evaluate(
            Csproj("4.0.1"),
            ServerJson("4.0.2"),
            "## 4.0.1 - 2026-09-12\n");

        Assert.NotNull(error);
        Assert.Contains("server.json", error, StringComparison.Ordinal);
    }

    private static string Csproj(string version) =>
        $"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>";

    private static string ServerJson(string version) =>
        "{\"name\":\"io.github.skymly/dotnet-mcp\",\"description\":\"test\",\"version\":\"" + version + "\"}";

    private static string UnreleasedChangelog(string released, string? productEntry)
    {
        var body = productEntry is null ? string.Empty : productEntry + "\n";
        return $"## Unreleased\n\n{body}## {released} - 2026-09-12\n";
    }
}
