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
    public async Task workspace_open_empty_path_is_structured_policy_error()
    {
        var root = CreateTempDir("root");
        try
        {
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));
            var result = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = "" });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.InvalidWorkspacePath, body.Error);
            Assert.Contains("empty", body.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--roots", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Add the directory", body.SuggestedAction, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task xaml_tools_reject_blank_path_without_suggesting_a_new_root(string path)
    {
        var root = CreateTempDir("root");
        try
        {
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));
            foreach (var tool in new[]
            {
                "xaml_resolve_class",
                "xaml_list_xmlns",
                "xaml_resolve_name",
                "xaml_resolve_binding",
                "xaml_diagnostics",
            })
            {
                var args = new Dictionary<string, object?> { ["path"] = path, ["name"] = "Title", ["bindingPath"] = "Name" };
                var result = await fx.Client.CallToolAsync(tool, args);
                Assert.True(result.IsError is true, tool);
                var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
                Assert.Equal(PolicyErrorCodes.XamlDocumentNotFound, body.Error);
                Assert.Contains("empty", body.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("--roots", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(PolicyErrorCodes.PathOutsideTrustedRoots, body.Error);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_open_whitespace_path_is_empty_path_error()
    {
        var root = CreateTempDir("root");
        try
        {
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));
            var result = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = "   " });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.InvalidWorkspacePath, body.Error);
            Assert.Contains("empty", body.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--roots", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
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
