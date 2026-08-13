using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class SymbolFindCallersSeamTests
{
    [Fact]
    public async Task symbol_find_callers_returns_direct_call_sites_for_method_handle()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithCallers());

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "SampleLib.MathOps.Add");

            var result = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?> { ["handle"] = handle });

            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<SymbolFindCallersResultDto>(result);
            Assert.True(body.Items.Count >= 4);
            Assert.Contains(body.Items, i => (i.FilePath ?? string.Empty).Contains("Uses.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Items, i => (i.FilePath ?? string.Empty).Contains("MoreUses.cs", StringComparison.OrdinalIgnoreCase));
            Assert.All(body.Items, i =>
            {
                Assert.Equal("InSource", i.DeclarationAvailability);
                Assert.Equal("Handwritten", i.Origin);
                Assert.False(string.IsNullOrWhiteSpace(i.CallerHandle));
                Assert.True(i.Start >= 0);
                Assert.True(i.Length > 0);
            });
            var callerNames = body.Items.Select(i => i.CallerSummary.DisplayName).Distinct().ToArray();
            Assert.Contains("Twice", callerNames);
            Assert.Contains("Triple", callerNames);
            Assert.Contains("One", callerNames);
            Assert.Contains("Two", callerNames);
            Assert.False(body.Truncated);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_callers_pages_with_limit_and_continues_without_duplicates()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithCallers());

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "SampleLib.MathOps.Add");

            var page1 = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["limit"] = 1
                });

            Assert.True(page1.IsError is not true);
            var first = InProcessMcpFixture.Deserialize<SymbolFindCallersResultDto>(page1);
            Assert.Single(first.Items);
            Assert.True(first.Truncated);
            Assert.False(string.IsNullOrWhiteSpace(first.NextCursor));
            Assert.Contains("nextCursor", first.Message, StringComparison.OrdinalIgnoreCase);

            var page2 = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["limit"] = 1,
                    ["cursor"] = first.NextCursor
                });

            Assert.True(page2.IsError is not true);
            var second = InProcessMcpFixture.Deserialize<SymbolFindCallersResultDto>(page2);
            Assert.Single(second.Items);
            Assert.NotEqual(KeyOf(first.Items[0]), KeyOf(second.Items[0]));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_callers_rejects_stale_cursor_and_non_method_handle()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithCallers());

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "SampleLib.MathOps.Add");
            var stale = FindRefsPageCursor.Encode(
                epoch: 999,
                entireSolution: false,
                docIndex: 0,
                locOffset: 0);

            var staleResult = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["cursor"] = stale
                });

            Assert.True(staleResult.IsError is true);
            var staleBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(staleResult);
            Assert.Equal(PolicyErrorCodes.StaleCursor, staleBody.Error);
            Assert.Contains("symbol_find_callers", staleBody.SuggestedAction, StringComparison.OrdinalIgnoreCase);

            var typeHandle = await ResolveHandleAsync(fx, "SampleLib.MathOps");
            var nonMethod = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?> { ["handle"] = typeHandle });

            Assert.True(nonMethod.IsError is true);
            var nonMethodBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(nonMethod);
            Assert.Equal(PolicyErrorCodes.SymbolNotFound, nonMethodBody.Error);
            Assert.Contains("method", nonMethodBody.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_callers_errors_with_workspace_not_ready_while_loading()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.DelayedWithCallers(TimeSpan.FromMilliseconds(1000)));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            var ghost = SymbolHandle.Create(
                SymbolQueryService.CSharpLanguage,
                Guid.NewGuid().ToString("D"),
                "SampleLib.MathOps.Add(int, int)");

            var result = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?> { ["handle"] = ghost.Format() });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, body.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task FindCallersAsync_soft_budget_zero_truncates_with_continuation_message()
    {
        var loaded = FakeSolutionLoader.CreateCallersLoaded(@"C:\fake\CallerLib.csproj");
        var service = new SymbolQueryService(new GeneratorQueryService());
        using var session = new WorkspaceSession(loaded, epoch: 1);

        var (resolved, resolveError) = await service.ResolveByNameAsync(session, "SampleLib.MathOps.Add");
        Assert.Null(resolveError);
        Assert.NotNull(resolved);

        var (page, error) = await service.FindCallersAsync(
            session,
            resolved!.Handle,
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

    private static string KeyOf(CallerLocationItemDto item) =>
        $"{item.FilePath}|{item.Start}|{item.Length}|{item.CallerHandle}";

    private static async Task<string> ResolveHandleAsync(InProcessMcpFixture fx, string name)
    {
        var resolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name });
        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
        return InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
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
