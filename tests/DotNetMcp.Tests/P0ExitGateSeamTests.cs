using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// P0 exit gate (#36): one MCP-boundary walk of the demoable read-side loop. No new tools.
/// </summary>
public class P0ExitGateSeamTests
{
    [Fact]
    public async Task p0_navigation_loop_open_status_resolve_goto_members_refs_impls_hierarchy_callers()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithHierarchy());

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
            Assert.NotEmpty(list.Projects);

            var drawable = await ResolveAsync(fx, "SampleLib.IDrawable");
            var summary = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = drawable });
            Assert.True(summary.IsError is not true);

            var gotoDef = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = drawable });
            Assert.True(gotoDef.IsError is not true);
            Assert.NotEmpty(InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(gotoDef).Locations);

            var members = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?> { ["handle"] = drawable });
            Assert.True(members.IsError is not true);

            var refs = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = drawable });
            Assert.True(refs.IsError is not true);

            var impls = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?> { ["handle"] = drawable });
            Assert.True(impls.IsError is not true);
            Assert.NotEmpty(InProcessMcpFixture.Deserialize<SymbolFindImplementationsResultDto>(impls).Items);

            var special = await ResolveAsync(fx, "SampleLib.SpecialCircle");
            var hierarchy = await fx.Client.CallToolAsync(
                "symbol_type_hierarchy",
                new Dictionary<string, object?> { ["handle"] = special });
            Assert.True(hierarchy.IsError is not true);
            Assert.NotEmpty(InProcessMcpFixture.Deserialize<SymbolTypeHierarchyResultDto>(hierarchy).Items);

            var draw = await ResolveAsync(fx, "SampleLib.Circle.Draw");
            var callers = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?> { ["handle"] = draw });
            Assert.True(callers.IsError is not true, InProcessMcpFixture.TextOf(callers));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task p0_generator_diagnostics_attribution_loop()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithGenerators());

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);
            await OpenUntilReadyAsync(fx);

            var projects = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            var projectId = Assert.Single(
                InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(projects).Projects).ProjectId;

            var gens = await fx.Client.CallToolAsync(
                "project_list_generators",
                new Dictionary<string, object?> { ["projectId"] = projectId });
            Assert.True(gens.IsError is not true);
            var genList = InProcessMcpFixture.Deserialize<ProjectListGeneratorsResultDto>(gens);
            var marker = Assert.Single(genList.Generators, g => g.TypeFullName == "CustomGenerator.MarkerGenerator");

            var sources = await fx.Client.CallToolAsync(
                "project_list_generated_sources",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = marker.AssemblyName,
                    ["typeFullName"] = marker.TypeFullName
                });
            Assert.True(sources.IsError is not true);
            Assert.NotEmpty(InProcessMcpFixture.Deserialize<ProjectListGeneratedSourcesResultDto>(sources).Items);

            var genDiags = await fx.Client.CallToolAsync(
                "project_list_generator_diagnostics",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "CustomGenerator",
                    ["typeFullName"] = "CustomGenerator.DiagnosticEmittingGenerator"
                });
            Assert.True(genDiags.IsError is not true);

            var diags = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?> { ["projectId"] = projectId });
            Assert.True(diags.IsError is not true);

            var resolved = await ResolveAsync(fx, "SampleApp.Generated.CustomMarker");
            var attr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?> { ["handle"] = resolved });
            Assert.True(attr.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attr);
            Assert.Equal("SourceGenerator", body.OriginKind);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task p0_drift_check_is_callable_on_ready_workspace()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithSymbolsOnDisk(projectDir));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);
            await OpenUntilReadyAsync(fx);

            var drift = await fx.Client.CallToolAsync(
                "workspace_check_drift",
                new Dictionary<string, object?>());
            Assert.True(drift.IsError is not true, InProcessMcpFixture.TextOf(drift));
            var body = InProcessMcpFixture.Deserialize<WorkspaceCheckDriftResultDto>(drift);
            Assert.True(body.Epoch > 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> ResolveAsync(InProcessMcpFixture fx, string name)
    {
        var resolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name });
        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
        return InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
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
            // best-effort cleanup
        }
    }
}
