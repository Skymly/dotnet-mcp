using DotNetMcp.Server;
using System.Text.RegularExpressions;

namespace DotNetMcp.Tests;

public class SdkPolicySeamTests
{
    [Fact]
    public void global_json_pins_net10_feature_band_with_roll_forward()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "global.json");
        Assert.True(File.Exists(path), "global.json is missing; CI and local SDK policy have no shared pin.");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var sdk = doc.RootElement.GetProperty("sdk");
        var version = sdk.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Matches(@"^10\.0\.\d+$", version);
        Assert.Equal("latestFeature", sdk.GetProperty("rollForward").GetString());
    }

    [Fact]
    public void ci_installs_floating_8_and_9_for_fixtures_and_does_not_commit_a_restore_lock()
    {
        var root = FindRepoRoot();
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var versions = ReadDotnetVersionList(ci);
        Assert.Equal(new[] { "8.0.x", "9.0.x", "10.0.x" }, versions);
        Assert.False(File.Exists(Path.Combine(root, "packages.lock.json")));
        Assert.False(File.Exists(Path.Combine(root, "src", "DotNetMcp.Server", "packages.lock.json")));
    }

    [Fact]
    public void sdk_selection_picks_newest_directory_with_msbuild_and_ignores_global_json()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet-mcp-sdk-" + Guid.NewGuid().ToString("N"));
        var older = Path.Combine(root, "sdk", "8.0.100");
        var newest = Path.Combine(root, "sdk", "10.0.201");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newest);
        File.WriteAllText(Path.Combine(older, "MSBuild.dll"), "older");
        File.WriteAllText(Path.Combine(newest, "MSBuild.dll"), "newest");
        File.WriteAllText(Path.Combine(root, "global.json"), """
            { "sdk": { "version": "8.0.100", "rollForward": "latestFeature" } }
            """);
        try
        {
            var chosen = MsBuildBootstrap.TryFindNewestSdkDirectory([root]);
            Assert.Equal(Path.GetFullPath(newest), Path.GetFullPath(chosen!));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static string[] ReadDotnetVersionList(string ci)
    {
        var match = Regex.Match(
            ci,
            @"^[ \t]*dotnet-version:[ \t]*\|[ \t]*\r?\n((?:[ \t]+\S+[ \t]*\r?\n)+)",
            RegexOptions.Multiline);
        Assert.True(match.Success, "ci.yml dotnet-version list is missing.");
        return match.Groups[1].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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