using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class SymbolPreviewRenameSeamTests
{
    [Fact]
    public async Task preview_rename_returns_workspace_edit_without_writing_disk()
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
            var widgetBefore = await File.ReadAllTextAsync(Path.Combine(projectDir, "Widget.cs"));
            var callerBefore = await File.ReadAllTextAsync(Path.Combine(projectDir, "Caller.cs"));

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "RenameApp.Widget.Ping" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["newName"] = "Pong"
                });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var body = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);

            Assert.False(string.IsNullOrWhiteSpace(body.PreviewId));
            Assert.Equal(fx.WorkspaceHost.CurrentEpoch, body.Epoch);
            Assert.True(body.ExpiresAt > DateTimeOffset.UtcNow);
            Assert.Equal(handle, body.OldHandle);
            Assert.Equal("Pong", body.NewName);
            Assert.Equal(2, body.Documents.Count);
            Assert.Contains(body.Documents, d =>
                d.Path.EndsWith("Widget.cs", StringComparison.OrdinalIgnoreCase) &&
                d.OldText.Contains("Ping(") &&
                d.NewText.Contains("Pong("));
            Assert.Contains(body.Documents, d =>
                d.Path.EndsWith("Caller.cs", StringComparison.OrdinalIgnoreCase) &&
                d.OldText.Contains("widget.Ping(2)") &&
                d.NewText.Contains("widget.Pong(2)"));
            Assert.Contains(handle, body.InvalidatedHandles);
            Assert.All(body.Documents, d => Assert.True(
                PathPolicy.IsUnderRoot(PathPolicy.Normalize(d.Path), PathPolicy.Normalize(root))));

            Assert.Equal(widgetBefore, await File.ReadAllTextAsync(Path.Combine(projectDir, "Widget.cs")));
            Assert.Equal(callerBefore, await File.ReadAllTextAsync(Path.Combine(projectDir, "Caller.cs")));

        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task preview_rename_refuses_source_generator_origin_before_storing()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithGenerators());

            await OpenUntilReadyAsync(fx, solution);
            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleApp.Generated.CustomMarker" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["newName"] = "RenamedMarker"
                });
            Assert.True(preview.IsError is true);
            var err = InProcessMcpFixture.Deserialize<PolicyErrorDto>(preview);
            Assert.Equal(PolicyErrorCodes.GeneratedSymbolRenameRefused, err.Error);
            Assert.False(string.IsNullOrWhiteSpace(err.SuggestedAction));
        }
        finally
        {
            TryDelete(root);
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
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-preview-" + prefix + "-" + Guid.NewGuid().ToString("N"));
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

internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _utcNow = start;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}
