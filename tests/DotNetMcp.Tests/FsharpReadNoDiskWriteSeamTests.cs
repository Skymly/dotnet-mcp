using DotNetMcp.Core;
using DotNetMcp.FSharp;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

public class FsharpReadNoDiskWriteSeamTests
{
    [Fact]
    public async Task fsharp_read_tools_do_not_write_workspace_source_when_snapshot_differs_from_disk()
    {
        var root = CreateTempDir("nowrite");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var watcher = new ManualWorkspaceFileWatcher();

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root),
                new WorkspaceHostOptions { FileWatcher = watcher });

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var widgetPath = Path.Combine(root, "FsLib", "Widget.fs");
            var usesPath = Path.Combine(root, "FsLib", "Uses.fs");
            const string sentinel = """
                module Tampered
                let nope () = 1
                """;
            await File.WriteAllTextAsync(widgetPath, sentinel);
            await File.WriteAllTextAsync(usesPath, sentinel);

            var resolve = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "FsLib.Widget" });
            Assert.True(resolve.IsError is not true, InProcessMcpFixture.TextOf(resolve));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolve).Handle;

            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
                await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>()));
            var fs = Assert.Single(projects.Projects, p => p.Language == "fsharp");

            var diagnostics = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?> { ["projectId"] = fs.ProjectId });
            Assert.True(diagnostics.IsError is not true, InProcessMcpFixture.TextOf(diagnostics));

            var refs = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(refs.IsError is not true, InProcessMcpFixture.TextOf(refs));

            var ping = await ResolvePingAsync(fx, handle);
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = ping, ["newName"] = "pong" });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));

            Assert.Equal(sentinel.Replace("\r\n", "\n"), (await File.ReadAllTextAsync(widgetPath)).Replace("\r\n", "\n"));
            Assert.Equal(sentinel.Replace("\r\n", "\n"), (await File.ReadAllTextAsync(usesPath)).Replace("\r\n", "\n"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_path_identity_does_not_confuse_same_filename_in_different_directories()
    {
        var root = CreateTempDir("collide");
        var solution = Path.Combine(root, "Collide.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpCollidingFileNames(root),
                new WorkspaceHostOptions { FileWatcher = new ManualWorkspaceFileWatcher() });

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var alpha = await ResolveHandleAsync(fx, "Collide.Alpha");
            var gotoDef = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = alpha });
            Assert.True(gotoDef.IsError is not true, InProcessMcpFixture.TextOf(gotoDef));
            var def = InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(gotoDef);
            var loc = Assert.Single(def.Locations);
            Assert.Contains($"{Path.DirectorySeparatorChar}A{Path.DirectorySeparatorChar}Widget.fs", loc.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"{Path.DirectorySeparatorChar}B{Path.DirectorySeparatorChar}Widget.fs", loc.FilePath, StringComparison.OrdinalIgnoreCase);

            var ping = await ResolveHandleAsync(fx, "Collide.Alpha.ping");
            var alphaBefore = await File.ReadAllTextAsync(Path.Combine(root, "A", "Widget.fs"));
            var betaBefore = await File.ReadAllTextAsync(Path.Combine(root, "B", "Widget.fs"));

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = ping, ["newName"] = "pong" });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var previewBody = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);
            Assert.Contains(previewBody.Documents, d =>
                d.Path.Contains($"{Path.DirectorySeparatorChar}A{Path.DirectorySeparatorChar}Widget.fs", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(previewBody.Documents, d =>
                d.Path.Contains($"{Path.DirectorySeparatorChar}B{Path.DirectorySeparatorChar}Widget.fs", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(alphaBefore, await File.ReadAllTextAsync(Path.Combine(root, "A", "Widget.fs")));
            Assert.Equal(betaBefore, await File.ReadAllTextAsync(Path.Combine(root, "B", "Widget.fs")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_zero_budget_completes_single_project_scope_and_truncates_wider_scopes()
    {
        var root = CreateTempDir("budget");
        try
        {
            var loaded = FakeSolutionLoader.CreateFsharpSymbolsLoaded(root);
            var fsharp = new FSharpSymbolQueryService(new SoftBudgetOptions
            {
                FindRefsScoped = TimeSpan.Zero,
                FindRefsEntireSolution = TimeSpan.Zero
            });
            var service = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService()), fsharp]);
            var snapshot = WorkspaceSession.CaptureFSharpSnapshot(loaded.Solution, epoch: 1, TrustedRoots.Create([root]));
            using var session = new WorkspaceSession(loaded, epoch: 1, fsharpSnapshot: snapshot);

            var fs = loaded.Solution.Projects.Single(p => p.Language == LanguageNames.FSharp);
            var (resolved, resolveError) = await service.ResolveByNameAsync(
                session,
                "FsLib.Widget",
                fs.Id.Id.ToString("D"));
            Assert.Null(resolveError);
            Assert.NotNull(resolved);

            // Project-stage paging (issue #526): the budget is checked before scanning
            // the NEXT project, so the single-project default scope completes even at
            // zero budget. Multi-project scopes still truncate with a resumable cursor.
            var (refs, refsError) = await service.FindReferencesAsync(session, resolved!.Handle);
            Assert.Null(refsError);
            Assert.NotNull(refs);
            Assert.False(refs!.Truncated);
            Assert.Null(refs.NextCursor);
            Assert.NotEmpty(refs.Items);

            var (entire, entireError) = await service.FindReferencesAsync(
                session,
                resolved.Handle,
                entireSolution: true);
            Assert.Null(entireError);
            Assert.NotNull(entire);
            Assert.False(entire!.Truncated);

            var (ping, pingError) = await service.ResolveByNameAsync(
                session,
                "FsLib.Widget.ping",
                fs.Id.Id.ToString("D"));
            Assert.Null(pingError);
            Assert.NotNull(ping);

            var (callers, callersError) = await service.FindCallersAsync(session, ping!.Handle);
            Assert.Null(callersError);
            Assert.NotNull(callers);
            Assert.False(callers!.Truncated);
            Assert.Null(callers.NextCursor);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> ResolveHandleAsync(InProcessMcpFixture fx, string name)
    {
        var result = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name });
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result).Handle;
    }

    private static async Task<string> ResolvePingAsync(InProcessMcpFixture fx, string widgetHandle)
    {
        var members = await fx.Client.CallToolAsync(
            "symbol_members",
            new Dictionary<string, object?> { ["handle"] = widgetHandle, ["limit"] = 50 });
        Assert.True(members.IsError is not true, InProcessMcpFixture.TextOf(members));
        var page = InProcessMcpFixture.Deserialize<SymbolMembersResultDto>(members);
        return Assert.Single(page.Items, m => m.Summary.DisplayName == "ping").Handle;
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fs-nowrite-" + prefix + "-" + Guid.NewGuid().ToString("N"));
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