using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class SymbolApplyRenameSeamTests
{
    [Fact]
    public async Task apply_rename_writes_preview_files_and_old_handle_is_gone()
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

            await OpenUntilReadyAsync(fx, solution);
            var widgetPath = Path.Combine(projectDir, "Widget.cs");
            var callerPath = Path.Combine(projectDir, "Caller.cs");
            var extraPath = Path.Combine(projectDir, "RenameApp.csproj");
            var extraBefore = await File.ReadAllTextAsync(extraPath);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "RenameApp.Widget.Ping" });
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["newName"] = "Pong"
                });
            var previewBody = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);
            var epochBefore = fx.WorkspaceHost.CurrentEpoch;

            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewBody.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));
            var applyBody = InProcessMcpFixture.Deserialize<SymbolApplyRenameResultDto>(apply);
            Assert.Equal(epochBefore + 1, applyBody.Epoch);
            Assert.Equal(2, applyBody.WrittenPaths.Count);

            var widget = await File.ReadAllTextAsync(widgetPath);
            var caller = await File.ReadAllTextAsync(callerPath);
            Assert.Contains("Pong", widget, StringComparison.Ordinal);
            Assert.DoesNotContain("Ping", widget, StringComparison.Ordinal);
            Assert.Contains("widget.Pong(2)", caller, StringComparison.Ordinal);
            Assert.Equal(extraBefore, await File.ReadAllTextAsync(extraPath));

            var old = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(old.IsError is true);
            var oldErr = InProcessMcpFixture.Deserialize<PolicyErrorDto>(old);
            Assert.Equal(PolicyErrorCodes.SymbolNotFound, oldErr.Error);

            var fresh = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "RenameApp.Widget.Pong" });
            Assert.True(fresh.IsError is not true, InProcessMcpFixture.TextOf(fresh));
            var newHandle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(fresh).Handle;
            Assert.NotEqual(handle, newHandle);

            var twice = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewBody.PreviewId });
            Assert.True(twice.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.PreviewNotFound,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(twice).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task apply_rejects_missing_expired_and_stale_epoch_previews()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var watcher = new ManualWorkspaceFileWatcher();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-19T00:00:00Z"));

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithRenameOnDisk(projectDir),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.Zero,
                    FileWatcher = watcher,
                    TimeProvider = clock,
                    RenamePreviewTtl = TimeSpan.FromMinutes(5)
                });

            await OpenUntilReadyAsync(fx, solution);
            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "RenameApp.Widget.Ping" });
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["newName"] = "Pong"
                });
            var previewId = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview).PreviewId;

            var missing = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = "" });
            Assert.Equal(
                PolicyErrorCodes.PreviewNotFound,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(missing).Error);

            var unknown = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = "deadbeefdeadbeef" });
            Assert.Equal(
                PolicyErrorCodes.PreviewNotFound,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(unknown).Error);

            clock.Advance(TimeSpan.FromMinutes(6));
            var expired = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewId });
            Assert.Equal(
                PolicyErrorCodes.PreviewExpired,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(expired).Error);

            clock.Advance(TimeSpan.FromMinutes(-6));
            var widget = Path.Combine(projectDir, "Widget.cs");
            await File.WriteAllTextAsync(widget, (await File.ReadAllTextAsync(widget)) + "\n");
            watcher.Raise(widget);
            var stale = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewId });
            Assert.Equal(
                PolicyErrorCodes.PreviewEpochMismatch,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(stale).Error);

            Assert.Contains("Ping", await File.ReadAllTextAsync(widget), StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task apply_refuses_preview_path_outside_trusted_roots_without_echoing_content()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var outside = Path.Combine(Path.GetTempPath(), "dotnet-mcp-outside-" + Guid.NewGuid().ToString("N") + ".cs");
        await File.WriteAllTextAsync(outside, "secret-source");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithRenameOnDisk(projectDir));

            await OpenUntilReadyAsync(fx, solution);
            var planted = fx.WorkspaceHost.StoreRenamePreview(
                "csharp:planted",
                "Nope",
                [new RenameDocumentSliceDto { Path = outside, OldText = "secret-source", NewText = "leaked" }],
                []);

            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = planted.PreviewId });
            Assert.True(apply.IsError is true);
            var err = InProcessMcpFixture.Deserialize<PolicyErrorDto>(apply);
            Assert.Equal(PolicyErrorCodes.PathOutsideTrustedRoots, err.Error);
            Assert.DoesNotContain("secret-source", InProcessMcpFixture.TextOf(apply), StringComparison.Ordinal);
            Assert.DoesNotContain("leaked", InProcessMcpFixture.TextOf(apply), StringComparison.Ordinal);
            Assert.Equal("secret-source", await File.ReadAllTextAsync(outside));
        }
        finally
        {
            TryDelete(root);
            try { File.Delete(outside); } catch { }
        }
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string path)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
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
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-apply-" + prefix + "-" + Guid.NewGuid().ToString("N"));
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
