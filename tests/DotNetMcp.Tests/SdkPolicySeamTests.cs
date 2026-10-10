using System.Text.RegularExpressions;
using DotNetMcp.Server;

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
        var rollForward = sdk.GetProperty("rollForward").GetString();
        var allowPrerelease = sdk.GetProperty("allowPrerelease").GetBoolean();
        Assert.True(IsProductFeatureBand(version, rollForward, allowPrerelease));
    }

    [Theory]
    [InlineData("8.0.100", "latestFeature", false)]
    [InlineData("9.0.100", "latestFeature", false)]
    [InlineData("11.0.100", "latestFeature", false)]
    [InlineData("10.0.100", "latestPatch", false)]
    [InlineData("10.0.100", "disable", false)]
    [InlineData("10.0.100-rc.1", "latestFeature", false)]
    [InlineData("10.0.302", "latestFeature", true)]
    public void product_feature_band_rejects_other_bands_and_patch_pins(
        string version,
        string rollForward,
        bool allowPrerelease)
    {
        Assert.False(IsProductFeatureBand(version, rollForward, allowPrerelease));
    }

    [Fact]
    public void product_feature_band_accepts_10_0_3xx_with_latest_feature()
    {
        Assert.True(IsProductFeatureBand("10.0.302", "latestFeature", false));
    }

    [Fact]
    public void ci_installs_floating_8_and_9_for_fixtures_and_does_not_commit_a_restore_lock()
    {
        var root = FindRepoRoot();
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.True(TryReadSetupDotNetWith(ci, out var fields));
        Assert.Equal("global.json", fields["global-json-file"]);
        Assert.Equal(new[] { "8.0.x", "9.0.x", "10.0.x" }, SplitBlock(fields["dotnet-version"]));
        Assert.False(File.Exists(Path.Combine(root, "packages.lock.json")));
        Assert.False(File.Exists(Path.Combine(root, "src", "DotNetMcp.Server", "packages.lock.json")));
    }

    [Fact]
    public void setup_dotnet_with_ignores_commented_version_lists()
    {
        const string yaml = """
            # dotnet-version: |
            #   1.0.x
            #   8.0.x
              - uses: actions/setup-dotnet@v4
                with:
                  # 8.0.x in a comment is not a version
                  global-json-file: global.json
                  dotnet-version: |
                    8.0.x
                    9.0.x
                    10.0.x
            """;

        Assert.True(TryReadSetupDotNetWith(yaml, out var fields));
        Assert.Equal(new[] { "8.0.x", "9.0.x", "10.0.x" }, SplitBlock(fields["dotnet-version"]));
    }

    [Fact]
    public void comment_only_dotnet_version_is_not_a_setup_list()
    {
        const string yaml = """
            # dotnet-version: |
            #   8.0.x
            #   9.0.x
            #   10.0.x
            """;

        Assert.False(TryReadSetupDotNetWith(yaml, out _));
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

    internal static bool IsProductFeatureBand(string? version, string? rollForward, bool allowPrerelease) =>
        version is not null
        && Regex.IsMatch(version, @"^10\.0\.\d+$")
        && rollForward == "latestFeature"
        && !allowPrerelease;

    internal static bool TryReadSetupDotNetWith(string yaml, out Dictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var uses = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsCommentOrBlank(lines[i]))
            {
                continue;
            }

            if (lines[i].Contains("uses: actions/setup-dotnet@", StringComparison.Ordinal))
            {
                uses = i;
                break;
            }
        }

        if (uses < 0)
        {
            return false;
        }

        var usesIndent = Indent(lines[uses]);
        var with = -1;
        for (var i = uses + 1; i < lines.Length; i++)
        {
            if (IsCommentOrBlank(lines[i]))
            {
                continue;
            }

            if (Indent(lines[i]) < usesIndent)
            {
                break;
            }

            if (lines[i].Trim() == "with:")
            {
                with = i;
                break;
            }
        }

        if (with < 0)
        {
            return false;
        }

        var withIndent = Indent(lines[with]);
        string? blockKey = null;
        var block = new List<string>();
        var parsed = fields;
        void Flush()
        {
            if (blockKey is null)
            {
                return;
            }

            parsed[blockKey] = string.Join('\n', block);
            blockKey = null;
            block.Clear();
        }

        for (var i = with + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (IsCommentOrBlank(line))
            {
                continue;
            }

            if (Indent(line) <= withIndent)
            {
                Flush();
                break;
            }

            var content = line.Trim();
            if (blockKey is not null && !content.Contains(':', StringComparison.Ordinal))
            {
                block.Add(content);
                continue;
            }

            Flush();
            var colon = content.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = content[..colon].Trim();
            var value = content[(colon + 1)..].Trim();
            if (value is "|" or "|-" or "|+" or ">")
            {
                blockKey = key;
            }
            else
            {
                fields[key] = value;
            }
        }

        Flush();
        return fields.ContainsKey("dotnet-version") && fields.ContainsKey("global-json-file");
    }

    internal static string[] SplitBlock(string block) =>
        block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsCommentOrBlank(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length == 0 || trimmed.StartsWith('#');
    }

    private static int Indent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
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