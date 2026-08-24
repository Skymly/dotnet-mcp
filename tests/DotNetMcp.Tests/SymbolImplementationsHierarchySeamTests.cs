using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class SymbolImplementationsHierarchySeamTests
{
    [Fact]
    public async Task symbol_find_implementations_returns_interface_implementers_with_locations()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithHierarchy());

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "SampleLib.IDrawable");

            var result = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?> { ["handle"] = handle });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolFindImplementationsResultDto>(result);
            var names = body.Items.Select(i => i.Summary.DisplayName).ToArray();
            Assert.Contains("Shape", names);
            Assert.Contains("Circle", names);
            Assert.Contains("Square", names);
            Assert.Contains("SpecialCircle", names);
            Assert.All(body.Items, i =>
            {
                Assert.False(string.IsNullOrWhiteSpace(i.Handle));
                Assert.NotEmpty(i.Locations);
                Assert.All(i.Locations, loc =>
                {
                    Assert.Equal("InSource", loc.DeclarationAvailability);
                    Assert.Equal("Handwritten", loc.Origin);
                    Assert.Contains("Shapes.cs", loc.FilePath!, StringComparison.OrdinalIgnoreCase);
                });
            });
            Assert.False(body.Truncated);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_implementations_returns_derived_classes_for_class_handle()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithHierarchy());

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "SampleLib.Shape");

            var result = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?> { ["handle"] = handle });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolFindImplementationsResultDto>(result);
            var names = body.Items.Select(i => i.Summary.DisplayName).ToArray();
            Assert.Contains("Circle", names);
            Assert.Contains("Square", names);
            Assert.Contains("SpecialCircle", names);
            Assert.DoesNotContain("Shape", names);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_implementations_pages_with_limit_and_continues_without_duplicates()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithHierarchy());

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "SampleLib.IDrawable");

            var page1 = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["limit"] = 2
                });

            Assert.True(page1.IsError is not true);
            var first = InProcessMcpFixture.Deserialize<SymbolFindImplementationsResultDto>(page1);
            Assert.Equal(2, first.Items.Count);
            Assert.True(first.Truncated);
            Assert.False(string.IsNullOrWhiteSpace(first.NextCursor));
            Assert.Contains("nextCursor", first.Message, StringComparison.OrdinalIgnoreCase);

            var page2 = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["limit"] = 2,
                    ["cursor"] = first.NextCursor
                });

            Assert.True(page2.IsError is not true);
            var second = InProcessMcpFixture.Deserialize<SymbolFindImplementationsResultDto>(page2);
            Assert.NotEmpty(second.Items);
            Assert.DoesNotContain(second.Items, m => first.Items.Any(f => f.Handle == m.Handle));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_implementations_rejects_stale_cursor()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithHierarchy());

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "SampleLib.IDrawable");
            var stale = MemberPageCursor.Encode(epoch: 999, offset: 0);

            var result = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["cursor"] = stale
                });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.StaleCursor, body.Error);
            Assert.Contains("symbol_find_implementations", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("without", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_implementations_errors_with_workspace_not_ready_while_loading()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.DelayedWithHierarchy(TimeSpan.FromMilliseconds(1000)));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            var ghost = SymbolHandle.Create(
                LanguageAdapters.CSharpLanguage,
                Guid.NewGuid().ToString("D"),
                "SampleLib.IDrawable");

            var result = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
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
    public async Task symbol_type_hierarchy_returns_base_types_then_interfaces()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithHierarchy());

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "SampleLib.SpecialCircle");

            var result = await fx.Client.CallToolAsync(
                "symbol_type_hierarchy",
                new Dictionary<string, object?> { ["handle"] = handle });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolTypeHierarchyResultDto>(result);
            Assert.False(body.Truncated);

            var bases = body.Items.Where(i => i.Kind == "BaseType").Select(i => i.Summary.DisplayName).ToArray();
            Assert.Equal(["Circle", "Shape", "object"], bases);

            var ifaces = body.Items.Where(i => i.Kind == "Interface").Select(i => i.Summary.DisplayName).ToArray();
            Assert.Contains("IDrawable", ifaces);
            Assert.Contains("IWidget", ifaces);
            Assert.Equal(
                body.Items.TakeWhile(i => i.Kind == "BaseType").Count(),
                bases.Length);
            Assert.All(body.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Handle)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_type_hierarchy_pages_with_epoch_cursor()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithHierarchy());

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "SampleLib.SpecialCircle");

            var page1 = await fx.Client.CallToolAsync(
                "symbol_type_hierarchy",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["limit"] = 2
                });

            Assert.True(page1.IsError is not true);
            var first = InProcessMcpFixture.Deserialize<SymbolTypeHierarchyResultDto>(page1);
            Assert.Equal(2, first.Items.Count);
            Assert.True(first.Truncated);
            Assert.False(string.IsNullOrWhiteSpace(first.NextCursor));

            var page2 = await fx.Client.CallToolAsync(
                "symbol_type_hierarchy",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["limit"] = 2,
                    ["cursor"] = first.NextCursor
                });

            Assert.True(page2.IsError is not true);
            var second = InProcessMcpFixture.Deserialize<SymbolTypeHierarchyResultDto>(page2);
            Assert.NotEmpty(second.Items);
            Assert.DoesNotContain(second.Items, m => first.Items.Any(f => f.Handle == m.Handle && f.Kind == m.Kind));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_type_hierarchy_rejects_stale_cursor_and_non_type_handle()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithHierarchy());

            await OpenUntilReadyAsync(fx, solution);
            var typeHandle = await ResolveHandleAsync(fx, "SampleLib.Circle");
            var stale = MemberPageCursor.Encode(epoch: 999, offset: 0);

            var staleResult = await fx.Client.CallToolAsync(
                "symbol_type_hierarchy",
                new Dictionary<string, object?>
                {
                    ["handle"] = typeHandle,
                    ["cursor"] = stale
                });

            Assert.True(staleResult.IsError is true);
            var staleBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(staleResult);
            Assert.Equal(PolicyErrorCodes.StaleCursor, staleBody.Error);
            Assert.Contains("symbol_type_hierarchy", staleBody.SuggestedAction, StringComparison.OrdinalIgnoreCase);

            var memberHandle = await ResolveHandleAsync(fx, "SampleLib.IDrawable.Draw");
            var nonType = await fx.Client.CallToolAsync(
                "symbol_type_hierarchy",
                new Dictionary<string, object?> { ["handle"] = memberHandle });

            Assert.True(nonType.IsError is true);
            var nonTypeBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(nonType);
            Assert.Equal(PolicyErrorCodes.SymbolNotFound, nonTypeBody.Error);
            Assert.Contains("type", nonTypeBody.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_type_hierarchy_errors_with_workspace_not_ready_while_loading()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.DelayedWithHierarchy(TimeSpan.FromMilliseconds(1000)));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            var ghost = SymbolHandle.Create(
                LanguageAdapters.CSharpLanguage,
                Guid.NewGuid().ToString("D"),
                "SampleLib.Circle");

            var result = await fx.Client.CallToolAsync(
                "symbol_type_hierarchy",
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
