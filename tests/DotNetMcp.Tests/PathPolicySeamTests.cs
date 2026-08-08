using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class PathPolicySeamTests
{
    [Fact]
    public async Task workspace_open_rejects_path_outside_trusted_roots_with_suggested_action()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var secretPath = Path.Combine(outside, "secret.txt");
        await File.WriteAllTextAsync(secretPath, "TOP_SECRET_CONTENT");

        try
        {
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));
            var result = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = secretPath });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.PathOutsideTrustedRoots, body.Error);
            Assert.False(string.IsNullOrWhiteSpace(body.SuggestedAction));
            Assert.Contains("trusted root", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);

            var text = InProcessMcpFixture.TextOf(result);
            Assert.DoesNotContain("TOP_SECRET_CONTENT", text);
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task workspace_open_rejects_traversal_outside_trusted_roots()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var secretPath = Path.Combine(outside, "leak.txt");
        await File.WriteAllTextAsync(secretPath, "TOP_SECRET_CONTENT");

        var traversal = Path.Combine(root, "..", Path.GetFileName(outside), "leak.txt");

        try
        {
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));
            var result = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = traversal });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.PathOutsideTrustedRoots, body.Error);
            Assert.DoesNotContain("TOP_SECRET_CONTENT", InProcessMcpFixture.TextOf(result));
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task workspace_open_accepts_path_inside_trusted_roots()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateMultiTfm());
            var result = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<WorkspaceOpenResultDto>(result);
            Assert.Equal("loading", body.Phase);
            Assert.Contains("workspace_status", body.SuggestedAction!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
