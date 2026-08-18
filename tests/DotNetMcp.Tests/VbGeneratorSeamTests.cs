using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class VbGeneratorSeamTests
{
    [Fact]
    public async Task project_list_generators_lists_vb_language_generators_only()
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
            var projectId = await VbProjectIdAsync(fx);

            var result = await fx.Client.CallToolAsync(
                "project_list_generators",
                new Dictionary<string, object?> { ["projectId"] = projectId });
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<ProjectListGeneratorsResultDto>(result);

            var marker = Assert.Single(body.Generators, g => g.TypeFullName == "CustomGenerator.VbMarkerGenerator");
            Assert.Equal("CustomGenerator", marker.AssemblyName);
            Assert.Equal("1.2.3.0", marker.Version);
            Assert.DoesNotContain(body.Generators, g => g.TypeFullName == "CustomGenerator.MarkerGenerator");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task generated_sources_and_diagnostics_work_for_vb_generators()
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
            var projectId = await VbProjectIdAsync(fx);

            var sources = await fx.Client.CallToolAsync(
                "project_list_generated_sources",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "CustomGenerator",
                    ["typeFullName"] = "CustomGenerator.VbMarkerGenerator",
                    ["limit"] = 10
                });
            Assert.True(sources.IsError is not true, InProcessMcpFixture.TextOf(sources));
            var sourceBody = InProcessMcpFixture.Deserialize<ProjectListGeneratedSourcesResultDto>(sources);
            var item = Assert.Single(sourceBody.Items);
            Assert.Equal(CustomGenerator.VbMarkerGenerator.HintName, item.HintName);
            Assert.Contains("VbMarker", item.Content, StringComparison.Ordinal);

            var diags = await fx.Client.CallToolAsync(
                "project_list_generator_diagnostics",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "CustomGenerator",
                    ["typeFullName"] = "CustomGenerator.VbDiagnosticEmittingGenerator"
                });
            Assert.True(diags.IsError is not true, InProcessMcpFixture.TextOf(diags));
            var diagBody = InProcessMcpFixture.Deserialize<ProjectListGeneratorDiagnosticsResultDto>(diags);
            Assert.Contains(diagBody.Items, d => d.Id == CustomGenerator.VbDiagnosticEmittingGenerator.DiagnosticId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_attribution_distinguishes_vb_generated_and_handwritten_partials()
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

            var generated = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleApp.Generated.VbMarker" });
            Assert.True(generated.IsError is not true, InProcessMcpFixture.TextOf(generated));
            var generatedHandle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(generated).Handle;
            Assert.StartsWith("vb:", generatedHandle, StringComparison.Ordinal);

            var generatedAttr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?> { ["handle"] = generatedHandle });
            Assert.True(generatedAttr.IsError is not true, InProcessMcpFixture.TextOf(generatedAttr));
            var generatedBody = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(generatedAttr);
            Assert.Equal("SourceGenerator", generatedBody.OriginKind);
            Assert.Equal("CustomGenerator.VbMarkerGenerator", generatedBody.Generator!.TypeFullName);

            var host = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "GeneratorHost.Host" });
            Assert.True(host.IsError is not true, InProcessMcpFixture.TextOf(host));
            var hostAttr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?>
                {
                    ["handle"] = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(host).Handle
                });
            var hostBody = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(hostAttr);
            Assert.Equal("Handwritten", hostBody.OriginKind);

            var partial = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "GeneratorHost.VbPartialThing" });
            Assert.True(partial.IsError is not true, InProcessMcpFixture.TextOf(partial));
            var partialAttr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?>
                {
                    ["handle"] = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(partial).Handle
                });
            var partialBody = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(partialAttr);
            Assert.Contains(partialBody.Members, kv => kv.Key.Contains("Format", StringComparison.Ordinal) && kv.Value.OriginKind == "Handwritten");
            Assert.Contains(partialBody.Members, kv => kv.Key.Contains("GeneratedValue", StringComparison.Ordinal) && kv.Value.OriginKind == "SourceGenerator");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> VbProjectIdAsync(InProcessMcpFixture fx)
    {
        var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
        var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
        return Assert.Single(projects.Projects, p => p.Language == "vb").ProjectId;
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string solution)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = solution });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

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
