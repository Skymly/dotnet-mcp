using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class ComInteropSeamTests
{
    [Fact]
    public async Task symbol_summary_flags_comimport_and_leaves_ordinary_types_alone()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithComInterop());
            await OpenReady(fx, solution);

            var com = await Resolve(fx, "ComLib.IComThing");
            Assert.Equal("ComImport", com.Summary.InteropKind);
            Assert.StartsWith("csharp:", com.Handle, StringComparison.Ordinal);

            var ordinary = await Resolve(fx, "ComLib.Ordinary");
            Assert.Equal("None", ordinary.Summary.InteropKind);

            var meta = await Resolve(fx, "InteropLib.IMetaCom");
            Assert.Equal("ComImport", meta.Summary.InteropKind);
            var gotoDef = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = meta.Handle });
            Assert.True(gotoDef.IsError is not true, InProcessMcpFixture.TextOf(gotoDef));
            var loc = Assert.Single(InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(gotoDef).Locations);
            Assert.Equal("InMetadata", loc.DeclarationAvailability);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<SymbolResolveResultDto> Resolve(InProcessMcpFixture fx, string name)
    {
        var result = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name });
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result);
    }

    private static async Task OpenReady(InProcessMcpFixture fx, string path)
    {
        var open = await fx.Client.CallToolAsync("workspace_open", new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true);
        for (var i = 0; i < 80; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            var status = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (status.Phase == "ready") return;
            await Task.Delay(25);
        }
        Assert.Fail("not ready");
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
