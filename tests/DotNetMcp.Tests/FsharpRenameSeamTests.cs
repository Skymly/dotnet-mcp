using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class FsharpRenameSeamTests
{
    [Fact]
    public async Task preview_and_apply_rename_fsharp_function_across_files()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root));

            await OpenUntilReadyAsync(fx, solution);
            var oldHandle = await ResolveFsharpPingAsync(fx);
            Assert.StartsWith("fsharp:", oldHandle, StringComparison.Ordinal);

            var widgetBefore = await File.ReadAllTextAsync(Path.Combine(root, "FsLib", "Widget.fs"));
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = oldHandle,
                    ["newName"] = "pong"
                });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var previewBody = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);
            Assert.Contains(previewBody.Documents, d =>
                d.Path.EndsWith("Widget.fs", StringComparison.OrdinalIgnoreCase) &&
                d.NewText.Contains("pong", StringComparison.Ordinal));
            Assert.Contains(previewBody.Documents, d =>
                d.Path.EndsWith("Uses.fs", StringComparison.OrdinalIgnoreCase) &&
                d.NewText.Contains("pong", StringComparison.Ordinal));
            Assert.Equal(widgetBefore, await File.ReadAllTextAsync(Path.Combine(root, "FsLib", "Widget.fs")));

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

            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
                await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>()));
            var fs = Assert.Single(projects.Projects, p => p.Language == "fsharp");
            var fresh = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "pong", ["projectId"] = fs.ProjectId });
            Assert.True(fresh.IsError is not true, InProcessMcpFixture.TextOf(fresh));
        }
        finally
        {
            TryDelete(root);
        }
    }

    internal static async Task<string> ResolveFsharpPingAsync(InProcessMcpFixture fx)
    {
        var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
            await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>()));
        var fs = Assert.Single(projects.Projects, p => p.Language == "fsharp");
        var widget = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = "FsLib.Widget", ["projectId"] = fs.ProjectId });
        Assert.True(widget.IsError is not true, InProcessMcpFixture.TextOf(widget));
        var members = await fx.Client.CallToolAsync(
            "symbol_members",
            new Dictionary<string, object?>
            {
                ["handle"] = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(widget).Handle,
                ["limit"] = 50
            });
        Assert.True(members.IsError is not true, InProcessMcpFixture.TextOf(members));
        var page = InProcessMcpFixture.Deserialize<SymbolMembersResultDto>(members);
        var ping = Assert.Single(page.Items, m => m.Summary.DisplayName == "ping");
        return ping.Handle;
    }

    internal static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string path)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        WorkspaceStatusDto? last = null;
        for (var i = 0; i < 400; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            last = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (last.Phase is "ready" or "failed")
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.True(last?.Phase == "ready", $"phase={last?.Phase} error={last?.Error} message={last?.Message}");
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fsr-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}

public class P2FsharpRenameExitGateSeamTests
{
    [Fact]
    public async Task p2_fsharp_rename_loop_resolve_preview_apply_new_handle()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root));

            await FsharpRenameSeamTests.OpenUntilReadyAsync(fx, solution);
            var oldHandle = await FsharpRenameSeamTests.ResolveFsharpPingAsync(fx);

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = oldHandle, ["newName"] = "pong" });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var previewId = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview).PreviewId;

            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));

            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
                await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>()));
            var fs = Assert.Single(projects.Projects, p => p.Language == "fsharp");
            var fresh = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "pong", ["projectId"] = fs.ProjectId });
            Assert.True(fresh.IsError is not true, InProcessMcpFixture.TextOf(fresh));
            Assert.NotEqual(oldHandle, InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(fresh).Handle);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-p2fs-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
