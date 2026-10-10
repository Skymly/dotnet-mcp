using System.Text.Json;
using System.Diagnostics;
using System.Text.RegularExpressions;
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

    [Fact]
    public void version_gate_evaluates_repo_changelog_on_disk()
    {
        var root = FindRepoRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "src", "DotNetMcp.Server", "DotNetMcp.Server.csproj"));
        var serverJson = File.ReadAllText(Path.Combine(root, "src", "DotNetMcp.Server", ".mcp", "server.json"));
        var changelog = File.ReadAllText(Path.Combine(root, "CHANGELOG.md"));

        Assert.Null(PackageIdentityGate.Evaluate(csproj, serverJson, changelog));

        var version = Regex.Match(csproj, @"<Version>([^<]+)</Version>").Groups[1].Value;
        var released = Regex.Match(changelog, @"^## (\d+\.\d+\.\d+)\b", RegexOptions.Multiline);
        Assert.True(released.Success);
        Assert.NotEqual(version, released.Groups[1].Value);
        var stuck = Regex.Replace(
            changelog,
            @"^## \d+\.\d+\.\d+\b",
            "## " + version,
            RegexOptions.Multiline);
        var error = PackageIdentityGate.Evaluate(csproj, serverJson, stuck);
        Assert.NotNull(error);
        Assert.Contains("Unreleased", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("4.0.2", "4.0.2", "4.0.1", "- a product change (#295)")]
    [InlineData("4.0.1", "4.0.1", "4.0.1", "- a product change (#295)")]
    [InlineData("4.0.1", "4.0.1", "4.0.1", null)]
    [InlineData("4.0.1", "4.0.2", "4.0.1", null)]
    public void ci_powershell_gate_matches_package_identity_gate(
        string csprojVersion,
        string jsonVersion,
        string released,
        string? productEntry)
    {
        var changelog = UnreleasedChangelog(released, productEntry);
        var csproj = Csproj(csprojVersion);
        var serverJson = ServerJson(jsonVersion);
        var csharp = PackageIdentityGate.Evaluate(csproj, serverJson, changelog);
        var (exit, output) = RunExtractedCiGate(csproj, serverJson, changelog);
        if (csharp is null)
        {
            Assert.True(exit == 0, output);
        }
        else
        {
            Assert.NotEqual(0, exit);
            Assert.Contains(csharp, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void readme_embed_and_mcp_name_match_package_identity()
    {
        var root = FindRepoRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var comment = Regex.Match(readme, @"<!--\s*mcp-name:\s*(\S+)\s*-->");
        Assert.True(comment.Success, "README is missing the mcp-name comment.");

        using var server = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "DotNetMcp.Server", ".mcp", "server.json")));
        Assert.Equal(server.RootElement.GetProperty("name").GetString(), comment.Groups[1].Value);

        var csproj = File.ReadAllText(Path.Combine(root, "src", "DotNetMcp.Server", "DotNetMcp.Server.csproj"));
        Assert.Contains("<PackageReadmeFile>README.md</PackageReadmeFile>", csproj, StringComparison.Ordinal);
        Assert.Contains("<PackageId>Skymly.DotNetMcp</PackageId>", csproj, StringComparison.Ordinal);
        Assert.Contains("<PackageType>McpServer</PackageType>", csproj, StringComparison.Ordinal);
        Assert.Matches(
            @"<None Include=""\.\.\\.\.\\README\.md"" Pack=""true"" PackagePath=""\\"" />",
            csproj);
        Assert.Matches(
            @"<None Include=""\.mcp\\server\.json"" Pack=""true"" PackagePath=""\.mcp\\"" />",
            csproj);
    }

    private static (int ExitCode, string Output) RunExtractedCiGate(string csproj, string serverJson, string changelog)
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet-mcp-gate-" + Guid.NewGuid().ToString("N"));
        var serverDir = Path.Combine(root, "src", "DotNetMcp.Server");
        Directory.CreateDirectory(Path.Combine(serverDir, ".mcp"));
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(Path.Combine(serverDir, "DotNetMcp.Server.csproj"), csproj, utf8);
        File.WriteAllText(Path.Combine(serverDir, ".mcp", "server.json"), serverJson, utf8);
        File.WriteAllText(Path.Combine(root, "CHANGELOG.md"), changelog, utf8);
        var script = ExtractVersionGateScript(File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "ci.yml")));
        var scriptPath = Path.Combine(root, "gate.ps1");
        File.WriteAllText(scriptPath, script, utf8);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = root,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(
            "& { $ErrorActionPreference = 'Stop'; try { . './gate.ps1' } catch { Write-Output $_.Exception.Message; exit 1 } }");
        Assert.True(process.Start());
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(WorkspaceReady.DefaultTimeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Extracted CI version gate did not exit.");
        }

        var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }

        return (process.ExitCode, output);
    }

    private static string ExtractVersionGateScript(string ci)
    {
        var lines = ci.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var name = Array.FindIndex(lines, line => line.Contains("name: Verify package versions match", StringComparison.Ordinal));
        Assert.True(name >= 0, "CI version gate step is missing.");
        var run = -1;
        for (var i = name + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("- name:", StringComparison.Ordinal))
            {
                break;
            }

            if (lines[i].Trim() == "run: |")
            {
                run = i;
                break;
            }
        }

        Assert.True(run >= 0 && run + 1 < lines.Length, "CI version gate script is missing.");
        var indent = lines[run + 1].TakeWhile(ch => ch == ' ').Count();
        var body = new List<string>();
        for (var i = run + 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                body.Add(string.Empty);
                continue;
            }

            var lineIndent = lines[i].TakeWhile(ch => ch == ' ').Count();
            if (lines[i].Trim().Length > 0 && lineIndent < indent)
            {
                break;
            }

            body.Add(lines[i].Length >= indent ? lines[i][indent..] : string.Empty);
        }

        return string.Join('\n', body) + "\n";
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
