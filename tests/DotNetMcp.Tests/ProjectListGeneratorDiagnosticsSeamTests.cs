using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class ProjectListGeneratorDiagnosticsSeamTests
{
    [Fact]
    public async Task project_list_generator_diagnostics_returns_fixture_diagnostic()
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
            var projectId = await GetSingleProjectIdAsync(fx);

            var result = await fx.Client.CallToolAsync(
                "project_list_generator_diagnostics",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "CustomGenerator",
                    ["typeFullName"] = "CustomGenerator.DiagnosticEmittingGenerator"
                });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<ProjectListGeneratorDiagnosticsResultDto>(result);
            Assert.True(body.Epoch > 0);
            Assert.Equal("CustomGenerator", body.Generator.AssemblyName);
            Assert.Equal("CustomGenerator.DiagnosticEmittingGenerator", body.Generator.TypeFullName);
            var item = Assert.Single(body.Items);
            Assert.Equal(CustomGenerator.DiagnosticEmittingGenerator.DiagnosticId, item.Id);
            Assert.Equal(nameof(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning), item.Severity);
            Assert.Equal(CustomGenerator.DiagnosticEmittingGenerator.DiagnosticMessage, item.Message);
            Assert.False(body.Truncated);

            var projectDiags = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?> { ["projectId"] = projectId });
            Assert.True(projectDiags.IsError is not true);
            var compiler = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(projectDiags);
            Assert.DoesNotContain(
                compiler.Items,
                d => d.Id == CustomGenerator.DiagnosticEmittingGenerator.DiagnosticId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_list_generator_diagnostics_rejects_unknown_generator()
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
            var projectId = await GetSingleProjectIdAsync(fx);

            var result = await fx.Client.CallToolAsync(
                "project_list_generator_diagnostics",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "Missing.Assembly",
                    ["typeFullName"] = "Missing.Type"
                });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.GeneratorNotFound, body.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_list_generator_diagnostics_stale_epoch_cursor_errors()
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
            var projectId = await GetSingleProjectIdAsync(fx);

            var stale = DotNetMcp.Core.GeneratedSourcesPageCursor.Encode(
                epoch: 999_999,
                assemblyName: "CustomGenerator",
                typeFullName: "CustomGenerator.DiagnosticEmittingGenerator",
                offset: 0);

            var result = await fx.Client.CallToolAsync(
                "project_list_generator_diagnostics",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["assemblyName"] = "CustomGenerator",
                    ["typeFullName"] = "CustomGenerator.DiagnosticEmittingGenerator",
                    ["cursor"] = stale
                });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.StaleCursor, body.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> GetSingleProjectIdAsync(InProcessMcpFixture fx)
    {
        var list = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(list.IsError is not true);
        var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
        return Assert.Single(projects.Projects).ProjectId;
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string solution)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = solution });
        Assert.True(open.IsError is not true);

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
