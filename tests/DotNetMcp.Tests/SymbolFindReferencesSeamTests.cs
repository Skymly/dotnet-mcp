using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class SymbolFindReferencesSeamTests
{
    [Fact]
    public async Task symbol_find_references_default_scope_is_dependency_closure_excluding_consumers()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFindRefsGraph());

            await OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveMarkerHandleAsync(fx);

            var result = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = handle });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(result);
            Assert.NotEmpty(body.Items);
            Assert.All(body.Items, i =>
                Assert.Contains("LibA", i.FilePath!, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(body.Items, i =>
                (i.FilePath ?? string.Empty).Contains("LibB", StringComparison.OrdinalIgnoreCase) ||
                (i.FilePath ?? string.Empty).Contains("Outsider", StringComparison.OrdinalIgnoreCase));
            Assert.False(body.Truncated);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_references_entire_solution_includes_outsider_and_libb()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFindRefsGraph());

            await OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveMarkerHandleAsync(fx);

            var result = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["entireSolution"] = true
                });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(result);
            Assert.Contains(body.Items, i =>
                (i.FilePath ?? string.Empty).Contains("LibB", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Items, i =>
                (i.FilePath ?? string.Empty).Contains("Outsider", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Items, i =>
                (i.FilePath ?? string.Empty).Contains("LibA", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_references_pages_with_limit_and_continues_without_duplicates()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFindRefsGraph());

            await OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveMarkerHandleAsync(fx);

            var page1 = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["entireSolution"] = true,
                    ["limit"] = 1
                });

            Assert.True(page1.IsError is not true);
            var first = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(page1);
            Assert.Single(first.Items);
            Assert.True(first.Truncated);
            Assert.False(string.IsNullOrWhiteSpace(first.NextCursor));
            Assert.Contains("nextCursor", first.Message, StringComparison.OrdinalIgnoreCase);

            var page2 = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["entireSolution"] = true,
                    ["limit"] = 1,
                    ["cursor"] = first.NextCursor
                });

            Assert.True(page2.IsError is not true);
            var second = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(page2);
            Assert.Single(second.Items);
            Assert.NotEqual(
                KeyOf(first.Items[0]),
                KeyOf(second.Items[0]));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_references_rejects_stale_cursor()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFindRefsGraph());

            await OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveMarkerHandleAsync(fx);
            var stale = FindRefsPageCursor.Encode(
                epoch: 999,
                entireSolution: false,
                docIndex: 0,
                locOffset: 0);

            var result = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["cursor"] = stale
                });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.StaleCursor, body.Error);
            Assert.Contains("symbol_find_references", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("without", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_references_errors_with_workspace_not_ready_while_loading()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                new FakeSolutionLoader(
                    TimeSpan.FromMilliseconds(1000),
                    () => FakeSolutionLoader.CreateFindRefsGraphLoaded()));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            var ghost = SymbolHandle.Create(
                SymbolQueryService.CSharpLanguage,
                Guid.NewGuid().ToString("D"),
                "LibA.Marker");

            var result = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = ghost.Format() });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, body.Error);
            Assert.Contains("workspace_status", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task FindReferencesAsync_soft_budget_zero_truncates_with_continuation_message()
    {
        var loaded = FakeSolutionLoader.CreateFindRefsGraphLoaded();
        var service = new SymbolQueryService(new GeneratorQueryService());
        var libA = loaded.Solution.Projects.Single(p => p.Name == "LibA");
        using var session = new WorkspaceSession(loaded, epoch: 1);

        var (resolved, resolveError) = await service.ResolveByNameAsync(
            session,
            "LibA.Marker",
            libA.Id.Id.ToString("D"));
        Assert.Null(resolveError);
        Assert.NotNull(resolved);

        var (page, error) = await service.FindReferencesAsync(
            session,
            resolved!.Handle,
            entireSolution: true,
            limit: 50,
            cursor: null,
            softBudget: TimeSpan.Zero);

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.True(page!.Truncated);
        Assert.False(string.IsNullOrWhiteSpace(page.NextCursor));
        Assert.Contains("Soft budget", page.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not retry from scratch", page.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(page.Items);
    }

    private static async Task<string> ResolveMarkerHandleAsync(InProcessMcpFixture fx)
    {
        var projects = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        var list = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(projects);
        var libA = Assert.Single(list.Projects, p => p.Name == "LibA");

        var resolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?>
            {
                ["name"] = "LibA.Marker",
                ["projectId"] = libA.ProjectId
            });
        var ok = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);
        return ok.Handle;
    }

    private static string KeyOf(ReferenceLocationItemDto item) =>
        $"{item.FilePath}|{item.Start}|{item.Length}|{item.Kind}";

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
