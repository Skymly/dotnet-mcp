using System.Collections.Concurrent;
using DotNetMcp.Server;
using ModelContextProtocol.Client;

namespace DotNetMcp.Tests;

public class StdioHostSmokeTests
{
    // Shorter than WorkspaceReady.DefaultTimeout so a missing line fails with its own message
    // instead of surfacing as a cancelled wait.
    private static readonly TimeSpan StderrLineTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task stdio_server_process_lists_tools()
    {
        using var timeout = new CancellationTokenSource(WorkspaceReady.DefaultTimeout);
        await using var client = await ConnectAsync(Path.GetTempPath(), timeout.Token);

        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Contains(tools, t => t.Name == "workspace_open");
    }

    [Fact]
    public async Task stdio_server_process_calls_status_and_returns_not_ready_error()
    {
        using var timeout = new CancellationTokenSource(WorkspaceReady.DefaultTimeout);
        await using var client = await ConnectAsync(Path.GetTempPath(), timeout.Token);

        var status = await client.CallToolAsync(
            "workspace_status",
            new Dictionary<string, object?>(),
            cancellationToken: timeout.Token);
        Assert.True(status.IsError is not true, InProcessMcpFixture.TextOf(status));
        Assert.Equal("idle", InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(status).Phase);

        var list = await client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>(),
            cancellationToken: timeout.Token);
        Assert.True(list.IsError is true, InProcessMcpFixture.TextOf(list));
        var error = InProcessMcpFixture.Deserialize<PolicyErrorDto>(list);
        Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, error.Error);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(string.IsNullOrWhiteSpace(error.SuggestedAction));
    }

    [Fact]
    public async Task stdio_server_ignores_appsettings_json_in_the_launch_directory()
    {
        // The MCP client picks the launch directory, not the user. A repository that ships an
        // appsettings.json must not be able to reformat or silence the server's audit log.
        var launchDirectory = Path.Combine(Path.GetTempPath(), "dotnet-mcp-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(launchDirectory);
        File.WriteAllText(
            Path.Combine(launchDirectory, "appsettings.json"),
            """
            {
              "Logging": {
                "LogLevel": { "Default": "None" },
                "Console": { "FormatterName": "json" }
              }
            }
            """);

        try
        {
            var stderr = new ConcurrentQueue<string>();
            using var timeout = new CancellationTokenSource(WorkspaceReady.DefaultTimeout);
            await using var client = await ConnectAsync(
                launchDirectory,
                timeout.Token,
                workingDirectory: launchDirectory,
                standardErrorLines: stderr.Enqueue);

            var contentRootLine = await WaitForStderrLineAsync(
                stderr,
                static line => line.Contains("Content root path:", StringComparison.Ordinal),
                "the host 'Content root path' line");
            var contentRoot = contentRootLine[(contentRootLine.IndexOf("Content root path:", StringComparison.Ordinal)
                + "Content root path:".Length)..].Trim();
            var expectedContentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            Assert.True(
                string.Equals(
                    expectedContentRoot,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot)),
                    PathComparison),
                $"Host content root was '{contentRoot}', expected the server install directory '{expectedContentRoot}', "
                + $"not the launch directory '{launchDirectory}'.");

            var status = await client.CallToolAsync(
                "workspace_status",
                new Dictionary<string, object?>(),
                cancellationToken: timeout.Token);
            Assert.True(status.IsError is not true, InProcessMcpFixture.TextOf(status));

            await WaitForStderrLineAsync(
                stderr,
                static line => line.Trim() == "audit tool_invoked tool=workspace_status",
                "the plain-text audit line for workspace_status");

            var jsonLines = stderr.Where(static line => line.TrimStart().StartsWith('{')).ToArray();
            Assert.True(
                jsonLines.Length == 0,
                "stderr switched to the JSON console formatter configured by the launch directory: "
                + string.Join(" | ", jsonLines));
        }
        finally
        {
            try
            {
                Directory.Delete(launchDirectory, recursive: true);
            }
            catch (IOException)
            {
                // The server child process may still hold its working directory while it shuts down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static async Task<string> WaitForStderrLineAsync(
        ConcurrentQueue<string> stderr,
        Func<string, bool> predicate,
        string description)
    {
        var deadline = DateTime.UtcNow + StderrLineTimeout;
        while (true)
        {
            var match = stderr.FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail(
                    $"Timed out waiting for {description} on server stderr. Lines seen: "
                    + (stderr.IsEmpty ? "(none)" : string.Join(" | ", stderr)));
            }

            await Task.Delay(50);
        }
    }

    private static async Task<McpClient> ConnectAsync(
        string roots,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        Action<string>? standardErrorLines = null)
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "DotNetMcp.Server.dll");
        Assert.True(File.Exists(serverDll), "Server assembly was not copied next to the tests.");

        return await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "dotnet-mcp",
                Command = "dotnet",
                Arguments = [serverDll, "--roots", roots],
                WorkingDirectory = workingDirectory,
                StandardErrorLines = standardErrorLines,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["DOTNET_MCP_TRUSTED_ROOTS"] = null,
                },
            }),
            cancellationToken: cancellationToken);
    }
}