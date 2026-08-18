using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// P2 exit gate (#64): one MCP-boundary walk of the demoable VB loop. No new tools.
/// </summary>
public class P2ExitGateSeamTests
{
    [Fact]
    public async Task p2_mixed_loop_open_list_resolve_navigate_refs_diagnostics()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbSymbols(root));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            await OpenUntilReadyAsync(fx);

            var projects = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            Assert.True(projects.IsError is not true);
            var list = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(projects);
            Assert.Contains(list.Projects, p => p.Language == "vb");
            Assert.Contains(list.Projects, p => p.Language == "csharp");

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "VbLib.Widget" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var widget = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);
            Assert.StartsWith("vb:", widget.Handle, StringComparison.Ordinal);

            var summary = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = widget.Handle });
            Assert.True(summary.IsError is not true);

            var gotoDef = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = widget.Handle });
            Assert.True(gotoDef.IsError is not true);
            Assert.NotEmpty(InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(gotoDef).Locations);

            var members = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?> { ["handle"] = widget.Handle });
            Assert.True(members.IsError is not true);

            var refs = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = widget.Handle });
            Assert.True(refs.IsError is not true);

            var pingable = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "VbLib.IPingable" });
            var pingableHandle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(pingable).Handle;
            var impls = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?> { ["handle"] = pingableHandle });
            Assert.True(impls.IsError is not true);

            var hierarchy = await fx.Client.CallToolAsync(
                "symbol_type_hierarchy",
                new Dictionary<string, object?> { ["handle"] = widget.Handle });
            Assert.True(hierarchy.IsError is not true);

            var vbProject = Assert.Single(list.Projects, p => p.Language == "vb");
            var diags = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?> { ["projectId"] = vbProject.ProjectId });
            Assert.True(diags.IsError is not true, InProcessMcpFixture.TextOf(diags));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task p2_generator_diagnostics_attribution_loop()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbGenerators());

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            await OpenUntilReadyAsync(fx);

            var list = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
                await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>()));
            var projectId = Assert.Single(list.Projects).ProjectId;

            var gens = await fx.Client.CallToolAsync(
                "project_list_generators",
                new Dictionary<string, object?> { ["projectId"] = projectId });
            Assert.True(gens.IsError is not true, InProcessMcpFixture.TextOf(gens));
            var genBody = InProcessMcpFixture.Deserialize<ProjectListGeneratorsResultDto>(gens);
            Assert.Contains(genBody.Generators, g => g.TypeFullName == "CustomGenerator.VbMarkerGenerator");

            var sources = await fx.Client.CallToolAsync(
                "project_list_generated_sources",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "CustomGenerator",
                    ["typeFullName"] = "CustomGenerator.VbMarkerGenerator"
                });
            Assert.True(sources.IsError is not true, InProcessMcpFixture.TextOf(sources));

            var genDiags = await fx.Client.CallToolAsync(
                "project_list_generator_diagnostics",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "CustomGenerator",
                    ["typeFullName"] = "CustomGenerator.VbDiagnosticEmittingGenerator"
                });
            Assert.True(genDiags.IsError is not true, InProcessMcpFixture.TextOf(genDiags));

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleApp.Generated.VbMarker" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var attr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?>
                {
                    ["handle"] = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle
                });
            Assert.True(attr.IsError is not true, InProcessMcpFixture.TextOf(attr));
            Assert.Equal("SourceGenerator",
                InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attr).OriginKind);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx)
    {
        for (var i = 0; i < 40; i++)
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
