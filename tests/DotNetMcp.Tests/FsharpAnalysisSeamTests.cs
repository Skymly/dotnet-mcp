using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class FsharpAnalysisSeamTests
{
    [Fact]
    public async Task fsharp_find_references_implementations_hierarchy_and_callers_work()
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

            var widget = await ResolveHandleAsync(fx, "FsLib.Widget");
            var pingable = await ResolveHandleAsync(fx, "FsLib.Widget.IPingable");
            var pingWidget = await ResolveHandleAsync(fx, "FsLib.Widget.PingWidget");
            var members = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?> { ["handle"] = widget });
            var memberBody = InProcessMcpFixture.Deserialize<SymbolMembersResultDto>(members);
            var ping = await ResolveHandleAsync(fx, "FsLib.Widget.ping");

            var refsDefault = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = widget });
            Assert.True(refsDefault.IsError is not true, InProcessMcpFixture.TextOf(refsDefault));
            var defaultBody = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(refsDefault);
            Assert.Contains(defaultBody.Items, i =>
                (i.FilePath ?? string.Empty).Contains("Uses.fs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(defaultBody.Items, i =>
                (i.FilePath ?? string.Empty).Contains("Caller.cs", StringComparison.OrdinalIgnoreCase));

            var impl = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?> { ["handle"] = pingable });
            Assert.True(impl.IsError is not true, InProcessMcpFixture.TextOf(impl));
            var implBody = InProcessMcpFixture.Deserialize<SymbolFindImplementationsResultDto>(impl);
            Assert.Contains(implBody.Items, i => i.Summary.DisplayName == "PingWidget");

            var derived = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?> { ["handle"] = pingWidget });
            Assert.True(derived.IsError is not true, InProcessMcpFixture.TextOf(derived));
            var derivedBody = InProcessMcpFixture.Deserialize<SymbolFindImplementationsResultDto>(derived);
            Assert.Contains(derivedBody.Items, i => i.Summary.DisplayName == "SpecialPingWidget");

            var hierarchy = await fx.Client.CallToolAsync(
                "symbol_type_hierarchy",
                new Dictionary<string, object?> { ["handle"] = await ResolveHandleAsync(fx, "FsLib.Widget.SpecialPingWidget") });
            Assert.True(hierarchy.IsError is not true, InProcessMcpFixture.TextOf(hierarchy));
            var hierBody = InProcessMcpFixture.Deserialize<SymbolTypeHierarchyResultDto>(hierarchy);
            Assert.Contains(hierBody.Items, i => i.Summary.DisplayName == "PingWidget");

            var callers = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?> { ["handle"] = ping });
            Assert.True(callers.IsError is not true, InProcessMcpFixture.TextOf(callers));
            var callerBody = InProcessMcpFixture.Deserialize<SymbolFindCallersResultDto>(callers);
            Assert.Contains(callerBody.Items, i =>
                (i.FilePath ?? string.Empty).Contains("Uses.fs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> ResolveHandleAsync(InProcessMcpFixture fx, string name)
    {
        var result = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name });
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result).Handle;
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string path)
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

        Assert.True(last?.Phase == "ready", $"phase={last?.Phase} error={last?.Error}");
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
        }
    }
}


