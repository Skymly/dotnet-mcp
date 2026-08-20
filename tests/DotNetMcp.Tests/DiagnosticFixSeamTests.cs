using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class DiagnosticFixSeamTests
{
    [Fact]
    public async Task list_fixes_returns_first_party_actions_for_missing_using()
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
            var occurrence = await FirstCs0246Async(fx);
            var listed = await ListFixesAsync(fx, occurrence);
            Assert.NotEmpty(listed.Items);
            Assert.Contains(listed.Items, i =>
                i.Title.Contains("System.Collections.Generic", StringComparison.Ordinal) ||
                i.Title.Contains("using", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task list_fixes_unknown_locator_is_diagnostic_not_found()
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
            var projectId = await FirstProjectIdAsync(fx);
            var result = await fx.Client.CallToolAsync(
                "diagnostics_list_fixes",
                new Dictionary<string, object?>
                {
                    ["projectId"] = projectId,
                    ["diagnosticId"] = "CS0000"
                });
            Assert.True(result.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.DiagnosticNotFound,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(result).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task preview_fix_does_not_write_disk()
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
            var before = await File.ReadAllTextAsync(Path.Combine(projectDir, "Broken.cs"));
            var occurrence = await FirstCs0246Async(fx);
            var preview = await PreviewWorkingFixAsync(fx, occurrence);
            Assert.False(string.IsNullOrWhiteSpace(preview.PreviewId));
            Assert.NotEmpty(preview.Documents);
            Assert.Contains(preview.Documents, d =>
                d.NewText.Contains("System.Collections.Generic", StringComparison.Ordinal));
            Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(projectDir, "Broken.cs")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task apply_fix_clears_csharp_missing_using()
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
            var occurrence = await FirstCs0246Async(fx);
            var preview = await PreviewWorkingFixAsync(fx, occurrence);
            var apply = await fx.Client.CallToolAsync(
                "diagnostics_apply_fix",
                new Dictionary<string, object?> { ["previewId"] = preview.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));

            var after = await File.ReadAllTextAsync(Path.Combine(projectDir, "Broken.cs"));
            Assert.Contains("System.Collections.Generic", after, StringComparison.Ordinal);

            var remaining = await ProjectDiagnosticsAsync(fx, occurrence.ProjectId);
            Assert.DoesNotContain(remaining.Items, d =>
                d.Id == "CS0246" &&
                d.FilePath is not null &&
                d.FilePath.EndsWith("Broken.cs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task apply_fix_expired_preview_is_distinguishable()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-19T00:00:00Z"));

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithMissingUsingOnDisk(projectDir),
                new WorkspaceHostOptions { TimeProvider = clock, WorkspaceEditPreviewTtl = TimeSpan.FromMinutes(5) });

            await OpenUntilReadyAsync(fx, solution);
            var occurrence = await FirstCs0246Async(fx);
            var preview = await PreviewWorkingFixAsync(fx, occurrence);
            clock.Advance(TimeSpan.FromMinutes(6));

            var apply = await fx.Client.CallToolAsync(
                "diagnostics_apply_fix",
                new Dictionary<string, object?> { ["previewId"] = preview.PreviewId });
            Assert.True(apply.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.PreviewExpired,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(apply).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task list_fixes_on_fsharp_project_is_language_not_supported()
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
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
                await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>()));
            var fs = Assert.Single(projects.Projects, p => p.Language == "fsharp");
            var result = await fx.Client.CallToolAsync(
                "diagnostics_list_fixes",
                new Dictionary<string, object?>
                {
                    ["projectId"] = fs.ProjectId,
                    ["diagnosticId"] = "FS0039"
                });
            Assert.True(result.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.FixLanguageNotSupported,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(result).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    internal static async Task<DiagnosticItemDto> FirstCs0246Async(InProcessMcpFixture fx)
    {
        var projectId = await FirstProjectIdAsync(fx);
        var page = await ProjectDiagnosticsAsync(fx, projectId);
        var hit = page.Items.FirstOrDefault(d => d.Id is "CS0246" or "CS0103" or "BC30002" or "BC30451");
        Assert.NotNull(hit);
        return hit!;
    }

    internal static async Task<string> FirstProjectIdAsync(InProcessMcpFixture fx)
    {
        var listed = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
        Assert.True(listed.IsError is not true, InProcessMcpFixture.TextOf(listed));
        var body = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(listed);
        return Assert.Single(body.Projects).ProjectId;
    }

    internal static async Task<ProjectDiagnosticsResultDto> ProjectDiagnosticsAsync(
        InProcessMcpFixture fx,
        string projectId)
    {
        var result = await fx.Client.CallToolAsync(
            "project_diagnostics",
            new Dictionary<string, object?> { ["projectId"] = projectId, ["limit"] = 100 });
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(result);
    }

    internal static async Task<DiagnosticsListFixesResultDto> ListFixesAsync(
        InProcessMcpFixture fx,
        DiagnosticItemDto occurrence)
    {
        var result = await fx.Client.CallToolAsync(
            "diagnostics_list_fixes",
            Locator(occurrence));
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<DiagnosticsListFixesResultDto>(result);
    }

    internal static async Task<DiagnosticsPreviewFixResultDto> PreviewWorkingFixAsync(
        InProcessMcpFixture fx,
        DiagnosticItemDto occurrence,
        string scope = "occurrence")
    {
        var listed = await ListFixesAsync(fx, occurrence);
        Assert.NotEmpty(listed.Items);
        foreach (var item in listed.Items)
        {
            var args = Locator(occurrence);
            args["fixIndex"] = item.FixIndex;
            args["scope"] = scope;
            var preview = await fx.Client.CallToolAsync("diagnostics_preview_fix", args);
            if (preview.IsError is true)
            {
                continue;
            }

            var body = InProcessMcpFixture.Deserialize<DiagnosticsPreviewFixResultDto>(preview);
            if (body.Documents.Any(d => d.NewText.Contains("System.Collections.Generic", StringComparison.Ordinal)))
            {
                return body;
            }
        }

        Assert.Fail("No Diagnostic fix preview mentioned System.Collections.Generic. " +
                    string.Join(" | ", listed.Items.Select(i => i.Title)));
        return null!;
    }

    internal static Dictionary<string, object?> Locator(DiagnosticItemDto occurrence) => new()
    {
        ["projectId"] = occurrence.ProjectId,
        ["diagnosticId"] = occurrence.Id,
        ["filePath"] = occurrence.FilePath,
        ["startLine"] = occurrence.StartLine,
        ["startCharacter"] = occurrence.StartCharacter,
        ["endLine"] = occurrence.EndLine,
        ["endCharacter"] = occurrence.EndCharacter
    };

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string solution)
    {
        Assert.True((await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = solution })).IsError is not true);
        for (var i = 0; i < 400; i++)
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
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fix-" + prefix + "-" + Guid.NewGuid().ToString("N"));
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
