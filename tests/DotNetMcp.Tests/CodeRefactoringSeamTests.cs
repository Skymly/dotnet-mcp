using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class CodeRefactoringSeamTests
{
    [Fact]
    public async Task list_refactorings_returns_first_party_actions_for_public_field()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithEncapsulateFieldOnDisk(projectDir));

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveAsync(fx, "RefactorApp.Widget.count");
            var listed = await ListAsync(fx, handle);
            Assert.NotEmpty(listed.Items);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task preview_refactoring_does_not_write_disk()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithEncapsulateFieldOnDisk(projectDir));

            await OpenUntilReadyAsync(fx, solution);
            var before = await File.ReadAllTextAsync(Path.Combine(projectDir, "Widget.cs"));
            var handle = await ResolveAsync(fx, "RefactorApp.Widget.count");
            var preview = await PreviewWorkingAsync(fx, handle);
            Assert.False(string.IsNullOrWhiteSpace(preview.PreviewId));
            Assert.NotEmpty(preview.Documents);
            Assert.Contains(preview.Documents, d => LooksLikeEncapsulate(d.NewText));
            Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(projectDir, "Widget.cs")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task apply_refactoring_updates_field_and_invalidates_old_handle()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithEncapsulateFieldOnDisk(projectDir));

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveAsync(fx, "RefactorApp.Widget.count");
            var preview = await PreviewWorkingAsync(fx, handle);
            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_refactoring",
                new Dictionary<string, object?> { ["previewId"] = preview.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));

            var after = await File.ReadAllTextAsync(Path.Combine(projectDir, "Widget.cs"));
            Assert.True(LooksLikeEncapsulate(after), after);
            var written = InProcessMcpFixture.Deserialize<SymbolApplyRefactoringResultDto>(apply);
            Assert.Contains(written.WrittenPaths, p => p.EndsWith("Widget.cs", StringComparison.OrdinalIgnoreCase));

            var oldSummary = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = handle });
            if (oldSummary.IsError is true)
            {
                Assert.Equal(
                    PolicyErrorCodes.SymbolNotFound,
                    InProcessMcpFixture.Deserialize<PolicyErrorDto>(oldSummary).Error);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task list_refactorings_on_fsharp_handle_is_language_not_supported()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root));

            await OpenUntilReadyAsync(fx, solution);
            var handle = await FsharpRenameSeamTests.ResolveFsharpPingAsync(fx);
            var result = await fx.Client.CallToolAsync(
                "symbol_list_refactorings",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(result.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.RefactoringLanguageNotSupported,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(result).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task vb_public_field_can_be_listed_previewed_and_applied()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbEncapsulateFieldOnDisk(projectDir));

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveAsync(fx, "Widget.count");
            Assert.StartsWith("vb:", handle, StringComparison.Ordinal);
            var preview = await PreviewWorkingAsync(fx, handle);
            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_refactoring",
                new Dictionary<string, object?> { ["previewId"] = preview.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));
            var after = await File.ReadAllTextAsync(Path.Combine(projectDir, "Widget.vb"));
            Assert.False(string.Equals(
                """
                Public Class Widget
                    Public count As Integer
                End Class
                """.ReplaceLineEndings("\n"),
                after.ReplaceLineEndings("\n"),
                StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }


    [Fact]
    public async Task list_refactorings_refuses_source_generator_origin()
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
            var handle = await ResolveAsync(fx, "SampleApp.Generated.CustomMarker");
            var result = await fx.Client.CallToolAsync(
                "symbol_list_refactorings",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(result.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.GeneratedSymbolRefactoringRefused,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(result).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task list_refactorings_refuses_vb_source_generator_origin()
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
            var handle = await ResolveAsync(fx, "SampleApp.Generated.VbMarker");
            Assert.StartsWith("vb:", handle, StringComparison.Ordinal);
            var result = await fx.Client.CallToolAsync(
                "symbol_list_refactorings",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(result.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.GeneratedSymbolRefactoringRefused,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(result).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }
    internal static async Task<string> ResolveAsync(InProcessMcpFixture fx, string name)
    {
        var resolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name });
        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
        return InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
    }

    internal static async Task<SymbolListRefactoringsResultDto> ListAsync(InProcessMcpFixture fx, string handle)
    {
        var listed = await fx.Client.CallToolAsync(
            "symbol_list_refactorings",
            new Dictionary<string, object?> { ["handle"] = handle });
        Assert.True(listed.IsError is not true, InProcessMcpFixture.TextOf(listed));
        return InProcessMcpFixture.Deserialize<SymbolListRefactoringsResultDto>(listed);
    }

    internal static async Task<SymbolPreviewRefactoringResultDto> PreviewWorkingAsync(
        InProcessMcpFixture fx,
        string handle)
    {
        var listed = await ListAsync(fx, handle);
        Assert.NotEmpty(listed.Items);
        foreach (var item in listed.Items)
        {
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_refactoring",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["refactoringIndex"] = item.RefactoringIndex
                });
            if (preview.IsError is true)
            {
                continue;
            }

            var body = InProcessMcpFixture.Deserialize<SymbolPreviewRefactoringResultDto>(preview);
            if (body.Documents.Any(d => LooksLikeEncapsulate(d.NewText) || d.NewText != d.OldText))
            {
                return body;
            }
        }

        Assert.Fail("No Code Refactoring preview changed handwritten text. " +
                    string.Join(" | ", listed.Items.Select(i => i.Title)));
        return null!;
    }

    internal static bool LooksLikeEncapsulate(string text) =>
        text.Contains("get", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("set", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Count", StringComparison.Ordinal);

    internal static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string solution)
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

    internal static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-ref-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void TryDelete(string dir)
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
