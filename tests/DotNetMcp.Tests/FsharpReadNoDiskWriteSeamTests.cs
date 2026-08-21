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

            await OpenUntilReadyAsync(fx, solution);

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

            await OpenUntilReadyAsync(fx, solution);

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
    public async Task fsharp_injected_soft_budget_truncates_find_refs_and_callers_as_continuable()
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
            var service = new SymbolQueryService(new GeneratorQueryService(), fsharp: fsharp);
            using var session = new WorkspaceSession(loaded, epoch: 1);

            var fs = loaded.Solution.Projects.Single(p => p.Language == LanguageNames.FSharp);
            var (resolved, resolveError) = await service.ResolveByNameAsync(
                session,
                "FsLib.Widget",
                fs.Id.Id.ToString("D"));
            Assert.Null(resolveError);
            Assert.NotNull(resolved);

            var (refs, refsError) = await service.FindReferencesAsync(session, resolved!.Handle);
            Assert.Null(refsError);
            Assert.NotNull(refs);
            Assert.True(refs!.Truncated);
            Assert.False(string.IsNullOrWhiteSpace(refs.NextCursor));
            Assert.Contains("Soft budget", refs.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("do not retry from scratch", refs.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Page complete.", refs.Message, StringComparison.Ordinal);

            var (ping, pingError) = await service.ResolveByNameAsync(
                session,
                "FsLib.Widget.ping",
                fs.Id.Id.ToString("D"));
            Assert.Null(pingError);
            Assert.NotNull(ping);

            var (callers, callersError) = await service.FindCallersAsync(session, ping!.Handle);
            Assert.Null(callersError);
            Assert.NotNull(callers);
            Assert.True(callers!.Truncated);
            Assert.False(string.IsNullOrWhiteSpace(callers.NextCursor));
            Assert.Contains("Soft budget", callers.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Page complete.", callers.Message, StringComparison.Ordinal);
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

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string path)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        WorkspaceStatusDto? last = null;
        for (var i = 0; i < 400; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            last = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (last.Phase is "ready" or "failed")
            {
                break;
            }

            await Task.Delay(25);
        }

        Assert.True(last?.Phase == "ready", $"phase={last?.Phase} error={last?.Error} message={last?.Message}");
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