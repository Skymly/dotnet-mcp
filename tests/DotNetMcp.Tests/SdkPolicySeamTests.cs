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
        Assert.DoesNotMatch(@"^10\.0\.3\d{2}$", version);
    }

    [Fact]
    public void ci_installs_floating_8_and_9_for_fixtures_and_does_not_commit_a_restore_lock()
    {
        var root = FindRepoRoot();
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.Contains("8.0.x", ci, StringComparison.Ordinal);
        Assert.Contains("9.0.x", ci, StringComparison.Ordinal);
        Assert.Contains("10.0.x", ci, StringComparison.Ordinal);
        Assert.Contains("SampleFilter", ci, StringComparison.Ordinal);
        Assert.Contains("global.json", ci, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "packages.lock.json")));
        Assert.False(File.Exists(Path.Combine(root, "src", "DotNetMcp.Server", "packages.lock.json")));
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
