using DotNetMcp.Core;
using DotNetMcp.FSharp;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

/// <summary>
/// Issue #526 — F# handles reuse the cross-project finder contract: the default
/// scope is the defining project plus its transitive F# dependents, entireSolution
/// is every F# project, and C#/VB consumers are named but never searched.
/// </summary>
public class FsharpCrossProjectSeamTests
{
    [Fact]
    public async Task fsharp_find_references_searches_transitive_fsharp_dependents()
    {
        var root = CreateTempDir("refs");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpCrossProject(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var ids = await ProjectIdsAsync(fx);
            var handle = await ResolveHandleAsync(fx, "FsLib.Core.doubleThenBump", ids["FsLib"]);

            var refs = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(refs.IsError is not true, InProcessMcpFixture.TextOf(refs));
            var body = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(refs);
            Assert.False(body.Truncated);

            Assert.Contains(body.Items, i =>
                i.Kind == ReferenceLocationKind.Definition &&
                i.ProjectId == ids["FsLib"] &&
                (i.FilePath ?? string.Empty).EndsWith("Library.fs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Items, i =>
                i.ProjectId == ids["FsApp"] &&
                (i.FilePath ?? string.Empty).EndsWith("Use.fs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(body.Items, i =>
                i.ProjectId == ids["FsTop"] &&
                (i.FilePath ?? string.Empty).EndsWith("Top.fs", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(body.Items, i => i.ProjectId == ids["CsConsumer"]);
            Assert.DoesNotContain("C#/VB dependent projects were not searched", body.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_find_references_entire_solution_scans_all_fsharp_projects()
    {
        var root = CreateTempDir("entire");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpCrossProject(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var ids = await ProjectIdsAsync(fx);
            var handle = await ResolveHandleAsync(fx, "FsLib.Core.doubleThenBump", ids["FsLib"]);

            var scoped = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(scoped.IsError is not true, InProcessMcpFixture.TextOf(scoped));
            var scopedBody = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(scoped);

            var entire = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = handle, ["entireSolution"] = true });
            Assert.True(entire.IsError is not true, InProcessMcpFixture.TextOf(entire));
            var entireBody = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(entire);

            Assert.False(entireBody.Truncated);
            // Both scopes find every real use; the wider scope only re-orders them,
            // because entireSolution sorts the project list alphabetically
            // (FsApp before FsLib) while the default scope walks the defining
            // project first. FsOnlyCs has no use of the symbol.
            Assert.Equal(
                scopedBody.Items.Select(SignatureOf).OrderBy(s => s, StringComparer.Ordinal),
                entireBody.Items.Select(SignatureOf).OrderBy(s => s, StringComparer.Ordinal));
            Assert.Equal(ids["FsApp"], entireBody.Items[0].ProjectId);
            Assert.Equal(ids["FsLib"], scopedBody.Items[0].ProjectId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string SignatureOf(ReferenceLocationItemDto item) =>
        $"{item.ProjectId}|{item.FilePath}|{item.Line}|{item.Column}|{item.Kind}";

    [Fact]
    public async Task fsharp_find_callers_searches_dependents_and_survives_parameter_definitions()
    {
        var root = CreateTempDir("callers");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpCrossProject(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var ids = await ProjectIdsAsync(fx);
            var handle = await ResolveHandleAsync(fx, "FsLib.Core.doubleThenBump", ids["FsLib"]);

            var callers = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(callers.IsError is not true, InProcessMcpFixture.TextOf(callers));
            var body = InProcessMcpFixture.Deserialize<SymbolFindCallersResultDto>(callers);

            // FsApp.Use.doubled and FsTop.Top.run call it directly; FsApp.Use.viaParam
            // is the S10 regression case — a parameter def-use must not intercept the
            // enclosing member and drop the caller.
            var names = body.Items.Select(i => i.CallerSummary.DisplayName).ToList();
            Assert.Contains("doubled", names);
            Assert.Contains("run", names);
            Assert.Contains("viaParam", names);
            Assert.Contains(body.Items, i => i.ProjectId == ids["FsApp"]);
            Assert.Contains(body.Items, i => i.ProjectId == ids["FsTop"]);
            Assert.DoesNotContain(body.Items, i => i.ProjectId == ids["CsConsumer"]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_find_implementations_finds_implementations_in_dependent_projects()
    {
        var root = CreateTempDir("impls");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpCrossProject(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var ids = await ProjectIdsAsync(fx);
            var handle = await ResolveHandleAsync(fx, "FsLib.Core.IShape", ids["FsLib"]);

            var impls = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(impls.IsError is not true, InProcessMcpFixture.TextOf(impls));
            var body = InProcessMcpFixture.Deserialize<SymbolFindImplementationsResultDto>(impls);

            var coin = Assert.Single(body.Items, i => i.Summary.DisplayName == "Coin");
            Assert.Equal(ids["FsApp"], coin.Summary.ProjectId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_type_hierarchy_resolves_interfaces_from_referenced_projects()
    {
        var root = CreateTempDir("hier");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpCrossProject(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var ids = await ProjectIdsAsync(fx);
            var handle = await ResolveHandleAsync(fx, "FsApp.Use.Coin", ids["FsApp"]);

            var hierarchy = await fx.Client.CallToolAsync(
                "symbol_type_hierarchy",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(hierarchy.IsError is not true, InProcessMcpFixture.TextOf(hierarchy));
            var body = InProcessMcpFixture.Deserialize<SymbolTypeHierarchyResultDto>(hierarchy);

            var shape = Assert.Single(body.Items, i => i.Summary.DisplayName == "IShape");
            Assert.Equal(HierarchyRelationKind.Interface, shape.Kind);
            Assert.Equal(ids["FsLib"], shape.Summary.ProjectId);
            Assert.DoesNotContain("outside the searched F# projects", body.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_results_name_unsearched_csharp_consumers()
    {
        var root = CreateTempDir("gap");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpCrossProject(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var ids = await ProjectIdsAsync(fx);
            var handle = await ResolveHandleAsync(fx, "FsOnlyCs.Scaling.scale", ids["FsOnlyCs"]);

            var refs = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(refs.IsError is not true, InProcessMcpFixture.TextOf(refs));
            var refsBody = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(refs);
            Assert.Contains(refsBody.Items, i => i.Kind == ReferenceLocationKind.Definition);
            Assert.Contains("CsConsumer", refsBody.Message, StringComparison.Ordinal);
            Assert.Contains("csharp", refsBody.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(refsBody.Items, i => i.ProjectId == ids["CsConsumer"]);

            var callers = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(callers.IsError is not true, InProcessMcpFixture.TextOf(callers));
            var callersBody = InProcessMcpFixture.Deserialize<SymbolFindCallersResultDto>(callers);
            Assert.Contains("CsConsumer", callersBody.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_rename_preview_collects_edits_in_all_dependent_fsharp_projects()
    {
        var root = CreateTempDir("rename");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpCrossProject(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var ids = await ProjectIdsAsync(fx);
            var handle = await ResolveHandleAsync(fx, "FsLib.Core.doubleThenBump", ids["FsLib"]);

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = handle, ["newName"] = "bumpedTwice" });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var body = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);

            Assert.Contains(body.Documents, d =>
                d.Path.EndsWith("Library.fs", StringComparison.OrdinalIgnoreCase) &&
                d.NewText.Contains("bumpedTwice", StringComparison.Ordinal));
            var appDoc = Assert.Single(body.Documents, d =>
                d.Path.EndsWith("Use.fs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("FsLib.Core.bumpedTwice", appDoc.NewText, StringComparison.Ordinal);
            Assert.DoesNotContain("doubleThenBump", appDoc.NewText, StringComparison.Ordinal);
            var topDoc = Assert.Single(body.Documents, d =>
                d.Path.EndsWith("Top.fs", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("bumpedTwice", topDoc.NewText, StringComparison.Ordinal);
            Assert.DoesNotContain(body.Documents, d =>
                d.Path.EndsWith("Use.cs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_rename_still_refuses_visible_symbol_with_csharp_dependents()
    {
        var root = CreateTempDir("refuse");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpCrossProject(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var ids = await ProjectIdsAsync(fx);
            var handle = await ResolveHandleAsync(fx, "FsOnlyCs.Scaling.scale", ids["FsOnlyCs"]);

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = handle, ["newName"] = "rescale" });
            Assert.True(preview.IsError is true, InProcessMcpFixture.TextOf(preview));
            var error = InProcessMcpFixture.Deserialize<PolicyErrorDto>(preview);
            Assert.Equal(PolicyErrorCodes.FSharpCrossProjectRenameUnsupported, error.Error);
            Assert.Contains("CsConsumer", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_zero_budget_truncates_at_project_stage_and_cursor_resumes()
    {
        var root = CreateTempDir("budget");
        try
        {
            var loaded = FakeSolutionLoader.CreateFsharpCrossProjectLoaded(root);
            var fsharp = new FSharpSymbolQueryService(new SoftBudgetOptions
            {
                FindRefsScoped = TimeSpan.Zero,
                FindRefsEntireSolution = TimeSpan.Zero
            });
            var service = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService()), fsharp]);
            var snapshot = WorkspaceSession.CaptureFSharpSnapshot(loaded.Solution, epoch: 1, TrustedRoots.Create([root]));
            using var session = new WorkspaceSession(loaded, epoch: 1, fsharpSnapshot: snapshot);

            var lib = loaded.Solution.Projects.Single(p => p.Name == "FsLib");
            var (resolved, resolveError) = await service.ResolveByNameAsync(
                session,
                "FsLib.Core.doubleThenBump",
                lib.Id.Id.ToString("D"));
            Assert.Null(resolveError);
            Assert.NotNull(resolved);

            var all = new List<ReferenceLocationItem>();
            string? cursor = null;
            var pages = 0;
            do
            {
                var (page, error) = await service.FindReferencesAsync(
                    session,
                    resolved!.Handle,
                    cursor: cursor,
                    softBudget: TimeSpan.Zero);
                Assert.Null(error);
                Assert.NotNull(page);
                all.AddRange(page!.Items);
                cursor = page.NextCursor;
                pages++;
                Assert.True(pages < 10, "Paging did not converge.");
            }
            while (cursor is not null);

            // Zero budget scans exactly one project per page, so the project-stage
            // cursor emits at least one page per scope project.
            Assert.True(pages >= 3, $"Expected one page per scope project, got {pages}.");
            var libId = lib.Id.Id.ToString("D");
            var appId = loaded.Solution.Projects.Single(p => p.Name == "FsApp").Id.Id.ToString("D");
            var topId = loaded.Solution.Projects.Single(p => p.Name == "FsTop").Id.Id.ToString("D");
            Assert.Contains(all, i => i.ProjectId == libId && i.Kind == ReferenceLocationKind.Definition);
            Assert.Contains(all, i => i.ProjectId == appId);
            Assert.Contains(all, i => i.ProjectId == topId);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_find_callers_shares_the_project_stage_cursor_path()
    {
        var root = CreateTempDir("callbudget");
        try
        {
            var loaded = FakeSolutionLoader.CreateFsharpCrossProjectLoaded(root);
            var fsharp = new FSharpSymbolQueryService(new SoftBudgetOptions
            {
                FindRefsScoped = TimeSpan.Zero,
                FindRefsEntireSolution = TimeSpan.Zero
            });
            var service = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService()), fsharp]);
            var snapshot = WorkspaceSession.CaptureFSharpSnapshot(loaded.Solution, epoch: 1, TrustedRoots.Create([root]));
            using var session = new WorkspaceSession(loaded, epoch: 1, fsharpSnapshot: snapshot);

            var lib = loaded.Solution.Projects.Single(p => p.Name == "FsLib");
            var (resolved, resolveError) = await service.ResolveByNameAsync(
                session,
                "FsLib.Core.doubleThenBump",
                lib.Id.Id.ToString("D"));
            Assert.Null(resolveError);

            var (first, firstError) = await service.FindCallersAsync(
                session,
                resolved!.Handle,
                softBudget: TimeSpan.Zero);
            Assert.Null(firstError);
            Assert.NotNull(first);
            Assert.True(first!.Truncated);
            Assert.False(string.IsNullOrWhiteSpace(first.NextCursor));

            var all = new List<CallerLocationItem>(first.Items);
            string? cursor = first.NextCursor;
            var guard = 0;
            while (cursor is not null && guard++ < 10)
            {
                var (page, error) = await service.FindCallersAsync(
                    session,
                    resolved.Handle,
                    cursor: cursor,
                    softBudget: TimeSpan.Zero);
                Assert.Null(error);
                Assert.NotNull(page);
                all.AddRange(page!.Items);
                cursor = page.NextCursor;
            }

            Assert.Null(cursor);
            var names = all.Select(i => i.CallerSummary.DisplayName).ToList();
            Assert.Contains("doubled", names);
            Assert.Contains("run", names);
            Assert.Contains("viaParam", names);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<Dictionary<string, string>> ProjectIdsAsync(InProcessMcpFixture fx)
    {
        var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
        Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
        return InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list)
            .Projects
            .ToDictionary(p => p.Name, p => p.ProjectId, StringComparer.Ordinal);
    }

    private static async Task<string> ResolveHandleAsync(InProcessMcpFixture fx, string name, string projectId)
    {
        var result = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name, ["projectId"] = projectId });
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result).Handle;
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fsx-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
