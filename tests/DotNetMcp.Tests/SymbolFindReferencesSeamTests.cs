using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class SymbolFindReferencesSeamTests
{
    [Fact]
    public async Task symbol_find_references_default_scope_includes_dependents()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithFindRefsGraph());

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveMarkerHandleAsync(fx);

            var result = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = handle });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(result);
            Assert.NotEmpty(body.Items);
            Assert.Contains(body.Items, i =>
                (i.FilePath ?? string.Empty).Contains("LibA", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Items, i =>
                (i.FilePath ?? string.Empty).Contains("LibB", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Items, i =>
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
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithFindRefsGraph());

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

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
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithFindRefsGraph());

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

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
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithFindRefsGraph());

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveMarkerHandleAsync(fx);
            var stale = FindRefsPageCursor.Encode(
                epoch: 999,
                entireSolution: false,
                docIndex: 0,
                locOffset: 0,
                tool: "symbol_find_references",
                queryId: handle);

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
                TestTrustedRoots.Create(root),
                new FakeSolutionLoader(
                    TimeSpan.FromMilliseconds(1000),
                    () => FakeSolutionLoader.CreateFindRefsGraphLoaded()));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            var ghost = SymbolHandle.Create(
                LanguageAdapters.CSharpLanguage,
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
    public async Task symbol_find_references_includes_hits_in_source_generated_documents()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithGenerators());

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleApp.Generated.CustomMarker" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var result = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(result);
            Assert.NotEmpty(body.Items);
            Assert.Contains(
                body.Items,
                i => (i.FilePath ?? string.Empty).Contains(".g.cs", StringComparison.OrdinalIgnoreCase)
                     || (i.FilePath ?? string.Empty).Contains("CustomMarker", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task FindReferencesAsync_soft_budget_zero_is_spent_and_truncates()
    {
        var loaded = FakeSolutionLoader.CreateFindRefsGraphLoaded();
        var service = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
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
    }

    /// <summary>
    /// Issue #559: the references lane shares the same engine gate — scope
    /// projects it cannot search are named instead of reported behind
    /// "Reference page complete".
    /// </summary>
    [Fact]
    public async Task FindReferencesAsync_entire_solution_names_unsearched_scope_projects()
    {
        var loaded = FakeSolutionLoader.CreateCallersCoverageGapLoaded();
        var service = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        var libA = loaded.Solution.Projects.Single(p => p.Name == "LibA");
        using var session = new WorkspaceSession(loaded, epoch: 1);

        var (resolved, resolveError) = await service.ResolveByNameAsync(
            session,
            "LibA.MathOps.Add",
            libA.Id.Id.ToString("D"));
        Assert.Null(resolveError);
        Assert.NotNull(resolved);

        var (page, error) = await service.FindReferencesAsync(
            session,
            resolved!.Handle,
            entireSolution: true,
            limit: 50,
            cursor: null);

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.False(page!.Truncated);
        var paths = page.Items.Select(i => i.FilePath ?? string.Empty).ToArray();
        Assert.Contains(paths, p => p.Contains("HealthyUses.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("BrokenUses.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("MetaUses.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Reference page complete", page.Message, StringComparison.Ordinal);
        Assert.Contains("coverage is incomplete", page.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("CallerBroken", page.Message, StringComparison.Ordinal);
        Assert.Contains("CallerMeta", page.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #565: the references lane applies the same materialization gate —
    /// CallerDecl's declared edge to LibMod never produced a usable reference,
    /// so it is named with the failure reason while the unrelated projects
    /// collapse to a count.
    /// </summary>
    [Fact]
    public async Task FindReferencesAsync_entire_solution_names_unmaterialized_edge_and_collapses_unrelated()
    {
        var loaded = FakeSolutionLoader.CreateMaterializationGapLoaded();
        var service = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        var libMod = loaded.Solution.Projects.Single(p => p.Name == "LibMod");
        using var session = new WorkspaceSession(loaded, epoch: 1);

        var (resolved, resolveError) = await service.ResolveByNameAsync(
            session,
            "LibMod.ModOps.Sub",
            libMod.Id.Id.ToString("D"));
        Assert.Null(resolveError);
        Assert.NotNull(resolved);

        var (page, error) = await service.FindReferencesAsync(
            session,
            resolved!.Handle,
            entireSolution: true,
            limit: 50,
            cursor: null);

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.False(page!.Truncated);
        var paths = page.Items.Select(i => i.FilePath ?? string.Empty).ToArray();
        Assert.Contains(paths, p => p.Contains("LocalSub.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths, p => p.Contains("DeclUses.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("CallerDecl", page.Message, StringComparison.Ordinal);
        Assert.Contains("did not materialize", page.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ZeroRefA", page.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ZeroRefB", page.Message, StringComparison.Ordinal);
        Assert.Contains("2 other scoped projects", page.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issues #572/#577: the references lane applies the same
    /// searched-degraded fidelity check — read from the compilation
    /// snapshot, so a stale snapshot (assets file written, package
    /// references not yet materialized under a cache root) is named with
    /// the same force as a missing one — while whole dependents keep
    /// producing hits unmentioned.
    /// </summary>
    [Fact]
    public async Task FindReferencesAsync_entire_solution_names_searched_degraded_projects()
    {
        var root = CreateTempDir("degraded-refs");
        var loaded = FakeSolutionLoader.CreateSearchedDegradedGapLoaded(root);
        var service = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        var lib = loaded.Solution.Projects.Single(p => p.Name == "Lib");
        using var session = new WorkspaceSession(loaded, epoch: 1);

        var (resolved, resolveError) = await service.ResolveByNameAsync(
            session,
            "Lib.LibOps.Jump",
            lib.Id.Id.ToString("D"));
        Assert.Null(resolveError);
        Assert.NotNull(resolved);

        var (page, error) = await service.FindReferencesAsync(
            session,
            resolved!.Handle,
            entireSolution: true,
            limit: 50,
            cursor: null);

        Assert.Null(error);
        Assert.NotNull(page);
        Assert.False(page!.Truncated);
        var paths = page.Items.Select(i => i.FilePath ?? string.Empty).ToArray();
        Assert.Contains(paths, p => p.Contains("DepRestoredUses.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.Contains("DepPlainUses.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("coverage is incomplete", page.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DepUnrestored", page.Message, StringComparison.Ordinal);
        Assert.Contains("project.assets.json missing", page.Message, StringComparison.Ordinal);
        Assert.Contains("DepStale", page.Message, StringComparison.Ordinal);
        Assert.Contains("stale snapshot", page.Message, StringComparison.Ordinal);
        Assert.Contains("DepNoFramework", page.Message, StringComparison.Ordinal);
        Assert.Contains("no assembly references resolved", page.Message, StringComparison.Ordinal);
        Assert.Contains("DepVendorRef", page.Message, StringComparison.Ordinal);
        Assert.Contains("did not materialize", page.Message, StringComparison.Ordinal);
        Assert.Contains("may be incomplete", page.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DepRestored", page.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DepBareRef", page.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DepPlain", page.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("other scoped projects", page.Message, StringComparison.Ordinal);
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
