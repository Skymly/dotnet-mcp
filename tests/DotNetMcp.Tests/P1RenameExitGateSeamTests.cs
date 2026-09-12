using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// P1 exit gate: demoable VB rename MCP loop. No new product tools.
/// </summary>
public class P1RenameExitGateSeamTests
{
    [Fact]
    public async Task p1_vb_rename_loop_resolve_preview_apply()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet-mcp-p1r-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbRenameOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "Widget.Ping" });
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
            Assert.StartsWith("vb:", handle, StringComparison.Ordinal);
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = handle, ["newName"] = "Pong" });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var previewId = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview).PreviewId;
            Assert.True((await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewId })).IsError is not true);
            var gone = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.Equal(PolicyErrorCodes.SymbolNotFound, InProcessMcpFixture.Deserialize<PolicyErrorDto>(gone).Error);
            var fresh = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "Widget.Pong" });
            Assert.True(fresh.IsError is not true, InProcessMcpFixture.TextOf(fresh));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
