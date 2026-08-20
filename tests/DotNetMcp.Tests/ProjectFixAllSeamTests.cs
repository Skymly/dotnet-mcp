using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class ProjectFixAllSeamTests
{
    [Fact]
    public async Task project_scope_changes_both_files_and_leaves_other_project()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithProjectFixAllOnDisk(root));

            await OpenUntilReadyAsync(fx, solution);
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
                await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>()));
            var projectA = Assert.Single(projects.Projects, p => p.Name == "FixAllA");
            var page = await DiagnosticFixSeamTests.ProjectDiagnosticsAsync(fx, projectA.ProjectId);
            var one = page.Items.First(d =>
                d.Id is "CS0246" or "CS0103" &&
                d.FilePath is not null &&
                d.FilePath.EndsWith("One.cs", StringComparison.OrdinalIgnoreCase));

            var preview = await DiagnosticFixSeamTests.PreviewWorkingFixAsync(fx, one, scope: "project");
            Assert.Contains(preview.Documents, d => d.Path.EndsWith("One.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(preview.Documents, d => d.Path.EndsWith("Two.cs", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(preview.Documents, d => d.Path.EndsWith("Other.cs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task vb_project_scope_changes_both_files()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbProjectFixAllOnDisk(projectDir));

            await OpenUntilReadyAsync(fx, solution);
            var projectId = await DiagnosticFixSeamTests.FirstProjectIdAsync(fx);
            var page = await DiagnosticFixSeamTests.ProjectDiagnosticsAsync(fx, projectId);
            var one = page.Items.First(d =>
                d.FilePath is not null &&
                d.FilePath.EndsWith("One.vb", StringComparison.OrdinalIgnoreCase));

            var preview = await DiagnosticFixSeamTests.PreviewWorkingFixAsync(fx, one, scope: "project");
            Assert.Contains(preview.Documents, d => d.Path.EndsWith("One.vb", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(preview.Documents, d => d.Path.EndsWith("Two.vb", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task solution_scope_is_unavailable()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithMissingUsingOnDisk(projectDir));

            await OpenUntilReadyAsync(fx, solution);
            var occurrence = await DiagnosticFixSeamTests.FirstCs0246Async(fx);
            var listed = await DiagnosticFixSeamTests.ListFixesAsync(fx, occurrence);
            var args = DiagnosticFixSeamTests.Locator(occurrence);
            args["fixIndex"] = listed.Items[0].FixIndex;
            args["scope"] = "solution";
            var preview = await fx.Client.CallToolAsync("diagnostics_preview_fix", args);
            Assert.True(preview.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.FixAllUnavailable,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(preview).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_scope_zero_budget_fails_without_writing()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithProjectFixAllOnDisk(root),
                softBudgetOptions: new SoftBudgetOptions { FixAllProject = TimeSpan.Zero });

            await OpenUntilReadyAsync(fx, solution);
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
                await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>()));
            var projectA = Assert.Single(projects.Projects, p => p.Name == "FixAllA");
            var page = await DiagnosticFixSeamTests.ProjectDiagnosticsAsync(fx, projectA.ProjectId);
            var one = page.Items.First(d =>
                d.FilePath is not null &&
                d.FilePath.EndsWith("One.cs", StringComparison.OrdinalIgnoreCase));
            var listed = await DiagnosticFixSeamTests.ListFixesAsync(fx, one);
            var args = DiagnosticFixSeamTests.Locator(one);
            args["fixIndex"] = listed.Items[0].FixIndex;
            args["scope"] = "project";
            var preview = await fx.Client.CallToolAsync("diagnostics_preview_fix", args);
            Assert.True(preview.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.FixAllBudgetExceeded,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(preview).Error);
            Assert.DoesNotContain(
                "System.Collections.Generic",
                await File.ReadAllTextAsync(Path.Combine(root, "A", "One.cs")),
                StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string solution)
    {
        Assert.True((await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = solution })).IsError is not true);
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
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-pfix-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
