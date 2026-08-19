using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class VbRenameSeamTests
{
    [Fact]
    public async Task vb_preview_and_apply_rename_cross_file()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbRenameOnDisk(projectDir));

            await OpenUntilReadyAsync(fx, solution);
            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "Widget.Ping" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
            Assert.StartsWith("vb:", handle, StringComparison.Ordinal);

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["newName"] = "Pong"
                });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var previewBody = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);
            Assert.Equal(2, previewBody.Documents.Count);

            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewBody.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));

            var gone = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.Equal(
                PolicyErrorCodes.SymbolNotFound,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(gone).Error);

            var fresh = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "Widget.Pong" });
            Assert.True(fresh.IsError is not true, InProcessMcpFixture.TextOf(fresh));
            Assert.StartsWith("vb:", InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(fresh).Handle);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_handle_rename_is_still_refused()
    {
        var handle = DotNetMcp.Core.SymbolHandle.Create("fsharp", Guid.NewGuid().ToString("D"), "FsLib.Widget").Format();
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
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["newName"] = "Renamed"
                });
            Assert.True(preview.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.RenameLanguageNotSupported,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(preview).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task vb_generated_member_rename_is_refused()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbGenerators());

            await OpenUntilReadyAsync(fx, solution);
            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleApp.Generated.VbMarker" });
            if (resolved.IsError is true)
            {
                resolved = await fx.Client.CallToolAsync(
                    "symbol_resolve",
                    new Dictionary<string, object?> { ["name"] = "VbMarker" });
            }

            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["newName"] = "Renamed"
                });
            Assert.True(preview.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.GeneratedSymbolRenameRefused,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(preview).Error);
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
            if (InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll).Phase == "ready")
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("workspace did not become ready");
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-vb-ren-" + prefix + "-" + Guid.NewGuid().ToString("N"));
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
