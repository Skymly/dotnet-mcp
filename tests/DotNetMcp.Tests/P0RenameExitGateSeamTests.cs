using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// P0 2.0 exit gate (#88): demoable C# rename MCP loop. No new product tools.
/// </summary>
public class P0RenameExitGateSeamTests
{
    [Fact]
    public async Task p0_rename_loop_resolve_preview_apply_new_handle()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithRenameOnDisk(projectDir));

            Assert.True((await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution })).IsError is not true);
            await OpenUntilReadyAsync(fx);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "RenameApp.Widget.Ping" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var oldHandle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = oldHandle,
                    ["newName"] = "Pong"
                });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var previewBody = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);
            Assert.NotEmpty(previewBody.Documents);
            Assert.Contains(previewBody.Documents, d => d.NewText.Contains("Pong", StringComparison.Ordinal));

            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewBody.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));

            var gone = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = oldHandle });
            Assert.True(gone.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.SymbolNotFound,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(gone).Error);

            var fresh = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "RenameApp.Widget.Pong" });
            Assert.True(fresh.IsError is not true, InProcessMcpFixture.TextOf(fresh));
            var newHandle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(fresh).Handle;
            Assert.NotEqual(oldHandle, newHandle);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx)
    {
        for (var i = 0; i < 80; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            var status = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (status.Phase == "ready")
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("workspace did not become ready");
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-p0r-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
