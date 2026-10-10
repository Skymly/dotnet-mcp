using System.Diagnostics;
using System.Text.Json;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

[Collection(TrustedRootsEnvCollection.Name)]
public class TrustedRootsStartupSeamTests
{
    [Fact]
    public void equals_form_matches_separate_token_and_empty_value_fails_closed()
    {
        var root = CreateTempDir();
        var other = CreateTempDir();
        var previous = Environment.GetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS", null);
            var spaced = TrustedRoots.FromStartup(["--roots", root]);
            var equals = TrustedRoots.FromStartup(["--roots=" + root]);
            Assert.Equal(spaced.Roots, equals.Roots);
            Assert.True(equals.Contains(root));

            Environment.SetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS", other);
            var combined = TrustedRoots.FromStartup(["--roots=" + root]);
            Assert.True(combined.Contains(root));
            Assert.True(combined.Contains(other));

            var empty = Assert.Throws<ArgumentException>(() => TrustedRoots.FromStartup(["--roots="]));
            Assert.Contains("--roots", empty.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS", previous);
            TryDelete(root);
            TryDelete(other);
        }
    }

    [Fact]
    public void env_roots_alone_are_accepted()
    {
        var root = CreateTempDir();
        var outside = CreateTempDir();
        var previous = Environment.GetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS", root);
            var trusted = TrustedRoots.FromStartup([]);
            Assert.True(trusted.Contains(root));
            Assert.False(trusted.Contains(outside));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS", previous);
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public void cli_roots_and_env_roots_are_a_union()
    {
        var cli = CreateTempDir();
        var env = CreateTempDir();
        var outside = CreateTempDir();
        var previous = Environment.GetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS", env);
            var trusted = TrustedRoots.FromStartup(["--roots", cli]);
            Assert.True(trusted.Contains(cli));
            Assert.True(trusted.Contains(env));
            Assert.False(trusted.Contains(outside));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS", previous);
            TryDelete(cli);
            TryDelete(env);
            TryDelete(outside);
        }
    }
    [Fact]
    public async Task missing_roots_exits_with_one_structured_line_and_no_stack()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "DotNetMcp.Server.dll");
        Assert.True(File.Exists(serverDll), "Server assembly was not copied next to the tests.");

        using var process = new Process();
        process.StartInfo.FileName = "dotnet";
        process.StartInfo.ArgumentList.Add(serverDll);
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.Environment.Remove("DOTNET_MCP_TRUSTED_ROOTS");
        Assert.True(process.Start());

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("Server stayed up without trusted roots.");
        }

        var stderr = (await stderrTask).Trim();
        var stdout = (await stdoutTask).Trim();
        Assert.Equal(1, process.ExitCode);
        Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stdout, StringComparison.Ordinal);
        Assert.False(stderr.Contains('\n') || stderr.Contains('\r'), stderr);

        var error = JsonSerializer.Deserialize<PolicyErrorDto>(stderr, JsonOptions.Default);
        Assert.NotNull(error);
        Assert.Equal(PolicyErrorCodes.TrustedRootsConfigurationFailed, error.Error);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(string.IsNullOrWhiteSpace(error.SuggestedAction));
    }

    [Fact]
    public async Task equals_form_is_accepted_by_the_server_process()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "DotNetMcp.Server.dll");
        var root = CreateTempDir();
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "dotnet";
            process.StartInfo.ArgumentList.Add(serverDll);
            process.StartInfo.ArgumentList.Add("--roots=" + root);
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.Environment.Remove("DOTNET_MCP_TRUSTED_ROOTS");
            Assert.True(process.Start());

            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            var stderr = await stderrTask;
            await stdoutTask;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("Application started", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain(PolicyErrorCodes.TrustedRootsConfigurationFailed, stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir()    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-roots-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TrustedRootsEnvCollection
{
    public const string Name = "TrustedRootsEnv";
}