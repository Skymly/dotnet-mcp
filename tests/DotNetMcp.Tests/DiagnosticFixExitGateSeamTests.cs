using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class P0DiagnosticFixExitGateSeamTests
{
    [Fact]
    public async Task p0_diagnostic_fix_loop_list_preview_apply_clears_cs0246()
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
            var preview = await DiagnosticFixSeamTests.PreviewWorkingFixAsync(fx, occurrence);
            var apply = await fx.Client.CallToolAsync(
                "diagnostics_apply_fix",
                new Dictionary<string, object?> { ["previewId"] = preview.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));

            var remaining = await DiagnosticFixSeamTests.ProjectDiagnosticsAsync(fx, occurrence.ProjectId);
            Assert.DoesNotContain(remaining.Items, d => d.Id == occurrence.Id);
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
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-p0fix-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}

public class P1DiagnosticFixExitGateSeamTests
{
    [Fact]
    public async Task p1_vb_diagnostic_fix_loop_clears_missing_import()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbMissingImportOnDisk(projectDir));

            await OpenUntilReadyAsync(fx, solution);
            var occurrence = await DiagnosticFixSeamTests.FirstCs0246Async(fx);
            var preview = await DiagnosticFixSeamTests.PreviewWorkingFixAsync(fx, occurrence);
            var apply = await fx.Client.CallToolAsync(
                "diagnostics_apply_fix",
                new Dictionary<string, object?> { ["previewId"] = preview.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));

            var remaining = await DiagnosticFixSeamTests.ProjectDiagnosticsAsync(fx, occurrence.ProjectId);
            Assert.DoesNotContain(remaining.Items, d => d.Id == occurrence.Id);
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
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-p1fix-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}

public class P3FixAllExitGateSeamTests
{
    [Fact]
    public async Task p3_document_scope_fixes_one_file_and_leaves_the_other()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFixAllOnDisk(projectDir));

            await OpenUntilReadyAsync(fx, solution);
            var projectId = await DiagnosticFixSeamTests.FirstProjectIdAsync(fx);
            var page = await DiagnosticFixSeamTests.ProjectDiagnosticsAsync(fx, projectId);
            var one = page.Items.First(d =>
                d.Id is "CS0246" or "CS0103" &&
                d.FilePath is not null &&
                d.FilePath.EndsWith("One.cs", StringComparison.OrdinalIgnoreCase));

            var preview = await DiagnosticFixSeamTests.PreviewWorkingFixAsync(fx, one, scope: "document");
            Assert.Contains(preview.Documents, d => d.Path.EndsWith("One.cs", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(preview.Documents, d => d.Path.EndsWith("Two.cs", StringComparison.OrdinalIgnoreCase));

            var apply = await fx.Client.CallToolAsync(
                "diagnostics_apply_fix",
                new Dictionary<string, object?> { ["previewId"] = preview.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));

            var remaining = await DiagnosticFixSeamTests.ProjectDiagnosticsAsync(fx, projectId);
            Assert.DoesNotContain(remaining.Items, d =>
                d.FilePath is not null &&
                d.FilePath.EndsWith("One.cs", StringComparison.OrdinalIgnoreCase) &&
                d.Id is "CS0246" or "CS0103");
            Assert.Contains(remaining.Items, d =>
                d.FilePath is not null &&
                d.FilePath.EndsWith("Two.cs", StringComparison.OrdinalIgnoreCase) &&
                d.Id is "CS0246" or "CS0103");
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
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-p3fix-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
