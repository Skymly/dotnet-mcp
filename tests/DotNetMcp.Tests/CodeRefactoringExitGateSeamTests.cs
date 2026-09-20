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

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
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

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
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
    public async Task package_version_matches_csproj_and_server_json()
    {
        var root = FindRepoRoot();
        var csproj = await File.ReadAllTextAsync(Path.Combine(root, "src", "DotNetMcp.Server", "DotNetMcp.Server.csproj"));
        var serverJson = await File.ReadAllTextAsync(Path.Combine(root, "src", "DotNetMcp.Server", ".mcp", "server.json"));

        var csprojMatch = System.Text.RegularExpressions.Regex.Match(csproj, @"<Version>([^<]+)</Version>");
        Assert.True(csprojMatch.Success, "DotNetMcp.Server.csproj is missing <Version>.");
        var version = csprojMatch.Groups[1].Value;

        using var doc = System.Text.Json.JsonDocument.Parse(serverJson);
        Assert.Equal(version, doc.RootElement.GetProperty("version").GetString());
    }

    private static string FindRepoRoot()
    {
        var candidates = new[]
        {
            Environment.CurrentDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "src", "DotNetMcp.Server", "DotNetMcp.Server.csproj")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root from the test host.");
    }
}
