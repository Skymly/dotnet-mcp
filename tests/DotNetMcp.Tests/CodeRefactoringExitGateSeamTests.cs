using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class P0CodeRefactoringExitGateSeamTests
{
    [Fact]
    public async Task p0_csharp_refactoring_loop_resolve_list_preview_apply()
    {
        var root = CodeRefactoringSeamTests.CreateTempDir("p0");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithEncapsulateFieldOnDisk(projectDir));

            await CodeRefactoringSeamTests.OpenUntilReadyAsync(fx, solution);
            var handle = await CodeRefactoringSeamTests.ResolveAsync(fx, "RefactorApp.Widget.count");
            var preview = await CodeRefactoringSeamTests.PreviewWorkingAsync(fx, handle);
            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_refactoring",
                new Dictionary<string, object?> { ["previewId"] = preview.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));
            Assert.True(
                CodeRefactoringSeamTests.LooksLikeEncapsulate(
                    await File.ReadAllTextAsync(Path.Combine(projectDir, "Widget.cs"))));
        }
        finally
        {
            CodeRefactoringSeamTests.TryDelete(root);
        }
    }
}

public class P1CodeRefactoringExitGateSeamTests
{
    [Fact]
    public async Task p1_vb_refactoring_loop_resolve_list_preview_apply()
    {
        var root = CodeRefactoringSeamTests.CreateTempDir("p1");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbEncapsulateFieldOnDisk(projectDir));

            await CodeRefactoringSeamTests.OpenUntilReadyAsync(fx, solution);
            var handle = await CodeRefactoringSeamTests.ResolveAsync(fx, "Widget.count");
            Assert.StartsWith("vb:", handle, StringComparison.Ordinal);
            var preview = await CodeRefactoringSeamTests.PreviewWorkingAsync(fx, handle);
            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_refactoring",
                new Dictionary<string, object?> { ["previewId"] = preview.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));
        }
        finally
        {
            CodeRefactoringSeamTests.TryDelete(root);
        }
    }
}

public class P3FourOhExitGateSeamTests
{
    [Fact]
    public async Task package_version_is_four_oh()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "src", "DotNetMcp.Server", "DotNetMcp.Server.csproj")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DotNetMcp.Server", "DotNetMcp.Server.csproj"))
        };
        var path = candidates.First(File.Exists);
        var csproj = await File.ReadAllTextAsync(path);
        Assert.Contains("<Version>4.0.0</Version>", csproj, StringComparison.Ordinal);
    }
}
