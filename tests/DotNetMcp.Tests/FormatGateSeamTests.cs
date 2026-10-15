using System.Diagnostics;

namespace DotNetMcp.Tests;

/// <summary>
/// The Linux CI job runs <c>dotnet format whitespace --verify-no-changes</c> against the product, test and
/// bench sources. Fixtures and spikes are test material, so each has an empty .editorconfig that stops the
/// root one. A gate that silently checks nothing is worse than none, so one test runs the exact command from
/// ci.yml against a badly formatted file.
/// </summary>
public class FormatGateSeamTests
{
    private const string StepName = "Check C# whitespace formatting";

    [Fact]
    public void root_editorconfig_sets_whitespace_rules_and_no_diagnostic_severities()
    {
        var lines = SettingLines(Path.Combine(FindRepoRoot(), ".editorconfig"));

        Assert.Contains("root = true", lines);
        Assert.Contains("end_of_line = lf", Section(lines, "[*]"));
        var csharp = Section(lines, "[*.cs]");
        Assert.Contains("indent_style = space", csharp);
        Assert.Contains("indent_size = 4", csharp);
        Assert.DoesNotContain(
            lines,
            static line => line.Contains("severity", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("dotnet_diagnostic", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("tests/fixtures")]
    [InlineData("spikes")]
    public void fixtures_and_spikes_have_an_editorconfig_that_only_stops_the_root(string relativeDirectory)
    {
        var path = Path.Combine(
            FindRepoRoot(),
            relativeDirectory.Replace('/', Path.DirectorySeparatorChar),
            ".editorconfig");
        Assert.True(
            File.Exists(path),
            $"{relativeDirectory}/.editorconfig is missing, so the root rules would apply to it.");

        Assert.Equal(["root = true"], SettingLines(path));
    }

    [Fact]
    public void test_output_carries_the_editorconfig_beside_the_copied_fixtures()
    {
        // MSBuildWorkspace loads the fixture copies under the test output directory, which is still
        // inside the repository tree. Without this copy the root .editorconfig would reach them.
        var copied = Path.Combine(MsBuildWorkspaceIntegrationTests.FixturesRoot, ".editorconfig");
        Assert.True(
            File.Exists(copied),
            "tests/fixtures/.editorconfig was not copied to the test output directory.");
    }

    [Fact]
    public void ci_format_step_includes_folders_with_a_trailing_slash_and_skips_build_output()
    {
        var arguments = CiFormatArguments();

        Assert.Equal("whitespace", arguments[2]);
        Assert.Contains("--folder", arguments);
        Assert.Contains("--verify-no-changes", arguments);

        // Without the trailing slash the value is read as a file name, nothing matches, and the check passes.
        var includes = OptionValues(arguments, "--include");
        Assert.NotEmpty(includes);
        Assert.All(includes, static include => Assert.EndsWith("/", include, StringComparison.Ordinal));

        var excludes = OptionValues(arguments, "--exclude");
        Assert.Contains("**/bin/**", excludes);
        Assert.Contains("**/obj/**", excludes);
    }

    [Fact]
    public async Task ci_format_command_rejects_a_badly_formatted_file_and_accepts_a_clean_one()
    {
        var arguments = CiFormatArguments();
        var folder = Path.Combine(Path.GetTempPath(), "dotnet-mcp-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        try
        {
            File.Copy(Path.Combine(FindRepoRoot(), ".editorconfig"), Path.Combine(folder, ".editorconfig"));
            var source = Path.Combine(folder, "src", "Sample.cs");

            // Brace on the same line as the declaration. Explicit \n so the verdict does not depend on how
            // this test file was checked out.
            File.WriteAllText(source, string.Join("\n", "namespace Sample;", "", "public class Widget    {", "}", ""));
            var rejected = await RunAsync(folder, arguments);
            Assert.True(
                rejected.ExitCode != 0,
                "The format gate accepted a badly formatted file; it is not checking anything. Output: " + rejected.Output);

            File.WriteAllText(source, string.Join("\n", "namespace Sample;", "", "public class Widget", "{", "}", ""));
            var accepted = await RunAsync(folder, arguments);
            Assert.True(accepted.ExitCode == 0, "The format gate rejected a clean file. Output: " + accepted.Output);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(arguments[0])
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>The <c>run:</c> command of the CI format step, split into arguments, quotes removed.</summary>
    private static IReadOnlyList<string> CiFormatArguments()
    {
        var lines = File.ReadAllLines(Path.Combine(FindRepoRoot(), ".github", "workflows", "ci.yml"));
        var step = Array.FindIndex(lines, static line => line.Contains($"name: {StepName}", StringComparison.Ordinal));
        Assert.True(step >= 0, $"ci.yml has no step named '{StepName}'.");

        for (var i = step + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("- name:", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith("run: ", StringComparison.Ordinal))
            {
                var tokens = Tokenize(line["run: ".Length..]);
                Assert.Equal("dotnet", tokens[0]);
                Assert.Equal("format", tokens[1]);
                return tokens;
            }
        }

        Assert.Fail($"The '{StepName}' step has no single-line run command.");
        return [];
    }

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in command)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            else if (ch == ' ' && !quoted)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>The values that follow <paramref name="option"/> up to the next option.</summary>
    private static List<string> OptionValues(IReadOnlyList<string> arguments, string option)
    {
        var values = new List<string>();
        var index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0, $"The format command has no {option}.");
        for (var i = index + 1; i < arguments.Count && !arguments[i].StartsWith("--", StringComparison.Ordinal); i++)
        {
            values.Add(arguments[i]);
        }

        return values;
    }

    /// <summary>Setting and section lines of an .editorconfig, without comments and blank lines.</summary>
    private static List<string> SettingLines(string path) =>
        File.ReadAllLines(path)
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

    private static List<string> Section(List<string> lines, string header)
    {
        var start = lines.IndexOf(header);
        Assert.True(start >= 0, $".editorconfig has no {header} section.");
        return lines.Skip(start + 1).TakeWhile(static line => !line.StartsWith('[')).ToList();
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
}
