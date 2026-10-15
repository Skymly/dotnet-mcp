using DotNetMcp.Server;
using ModelContextProtocol.Client;

namespace DotNetMcp.Tests;

/// <summary>
/// Runs the packed <c>Skymly.DotNetMcp</c> tool the way the README tells users to, then talks MCP to it.
/// The other stdio tests start the server from the build output, so a problem that only exists in the
/// package (a missing BuildHost folder, a dll left out of <c>tools/</c>) would reach users first.
/// </summary>
public class PackedToolSmokeTests
{
    public const string SourceEnvironmentVariable = "DOTNET_MCP_PACKED_TOOL_SOURCE";

    private const string PackageId = "Skymly.DotNetMcp";

    [PackedToolFact]
    public async Task packed_tool_opens_a_workspace_and_resolves_a_symbol()
    {
        var source = Environment.GetEnvironmentVariable(SourceEnvironmentVariable)!;
        var version = NewestPackedVersion(source);
        var freshPackageCache = Path.Combine(Path.GetTempPath(), "dotnet-mcp-packed-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await using var client = await McpClient.CreateAsync(
                new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = "dotnet-mcp-packed",
                    Command = "dotnet",
                    // The README form: dotnet tool exec --source <folder> --yes Skymly.DotNetMcp -- --roots <path>.
                    // --version pins the package that was just packed when the folder holds older ones too.
                    Arguments =
                    [
                        "tool", "exec", "--source", source, "--yes", "--version", version, PackageId,
                        "--", "--roots", MsBuildWorkspaceIntegrationTests.FixturesRoot,
                    ],
                    EnvironmentVariables = new Dictionary<string, string?>
                    {
                        // A cache that cannot already hold this version, so the package really is unpacked from the source.
                        ["NUGET_PACKAGES"] = freshPackageCache,
                        ["DOTNET_MCP_TRUSTED_ROOTS"] = null,
                    },
                }),
                cancellationToken: timeout.Token);

            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
            Assert.Contains(tools, static tool => tool.Name == "workspace_open");

            var open = await client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = MsBuildWorkspaceIntegrationTests.SampleSlnf },
                cancellationToken: timeout.Token);
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

            await WaitUntilReadyAsync(client, timeout.Token);

            // App references LibA, so the bare name matches in both projects. Name the declaring one.
            var list = await client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>(),
                cancellationToken: timeout.Token);
            Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
            var libA = Assert.Single(
                InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list).Projects,
                static project => project.Name.StartsWith("LibA", StringComparison.OrdinalIgnoreCase));

            var resolved = await client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "LibA.Marker", ["projectId"] = libA.ProjectId },
                cancellationToken: timeout.Token);
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);
            Assert.Equal("Marker", body.Summary.DisplayName);
            Assert.Equal("csharp", body.Summary.Language);
        }
        finally
        {
            try
            {
                Directory.Delete(freshPackageCache, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task WaitUntilReadyAsync(McpClient client, CancellationToken cancellationToken)
    {
        WorkspaceStatusDto? last = null;
        while (true)
        {
            var poll = await client.CallToolAsync(
                "workspace_status",
                new Dictionary<string, object?>(),
                cancellationToken: cancellationToken);
            Assert.True(poll.IsError is not true, InProcessMcpFixture.TextOf(poll));
            last = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (last.Phase == "ready")
            {
                return;
            }

            Assert.False(
                last.Phase is "failed" or "cancelled",
                $"The packed tool's workspace {last.Phase}: error={last.Error} message={last.Message}");
            await Task.Delay(250, cancellationToken);
        }
    }

    /// <summary>The highest version among the Skymly.DotNetMcp packages in <paramref name="source"/>.</summary>
    private static string NewestPackedVersion(string source)
    {
        Assert.True(Directory.Exists(source), $"{SourceEnvironmentVariable} points at a folder that does not exist: {source}");

        var versions = Directory.GetFiles(source, PackageId + ".*.nupkg")
            .Select(static path => Path.GetFileNameWithoutExtension(path)[(PackageId.Length + 1)..])
            .Select(static text => (Text: text, Number: Version.TryParse(text.Split('-')[0], out var number) ? number : null))
            .Where(static v => v.Number is not null)
            .OrderByDescending(static v => v.Number)
            .ThenBy(static v => v.Text.Contains('-', StringComparison.Ordinal))
            .ToArray();

        Assert.True(versions.Length > 0, $"No {PackageId}.*.nupkg was found in {source}. Pack the server into it first.");
        return versions[0].Text;
    }
}

/// <summary>
/// Skipped, with the reason shown, unless <c>DOTNET_MCP_PACKED_TOOL_SOURCE</c> names the folder that holds the
/// packed tool. CI sets it after its Pack step and then checks that the test really ran.
/// </summary>
public sealed class PackedToolFactAttribute : FactAttribute
{
    public PackedToolFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PackedToolSmokeTests.SourceEnvironmentVariable)))
        {
            Skip = $"Set {PackedToolSmokeTests.SourceEnvironmentVariable} to the folder holding the packed "
                + "Skymly.DotNetMcp nupkg. CI sets it after the Pack step.";
        }
    }
}
