using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class VbSymbolSeamTests
{
    [Fact]
    public async Task symbol_resolve_returns_vb_handle_for_vb_type()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbSymbols(root));

            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "VbLib.Widget" });
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result);

            Assert.StartsWith("vb:", body.Handle, StringComparison.Ordinal);
            Assert.True(SymbolHandle.TryParse(body.Handle, out var parsed, out _), body.Handle);
            Assert.Equal(LanguageAdapters.VbLanguage, parsed!.Language);
            Assert.Equal("NamedType", body.Summary.Kind);
            Assert.Equal("Widget", body.Summary.DisplayName);
            Assert.Equal("vb", body.Summary.Language);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_summary_goto_and_members_work_for_vb_handles()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbSymbols(root));

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "VbLib.Widget");

            var summary = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(summary.IsError is not true, InProcessMcpFixture.TextOf(summary));
            var summaryBody = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(summary);
            Assert.Equal(handle, summaryBody.Handle);
            Assert.Equal("Widget", summaryBody.Summary.DisplayName);

            var gotoDef = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(gotoDef.IsError is not true, InProcessMcpFixture.TextOf(gotoDef));
            var def = InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(gotoDef);
            var loc = Assert.Single(def.Locations);
            Assert.Equal("InSource", loc.DeclarationAvailability);
            Assert.Contains("Widget.vb", loc.FilePath!, StringComparison.OrdinalIgnoreCase);
            Assert.True(loc.Start >= 0);
            Assert.True(loc.Length > 0);

            var members = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(members.IsError is not true, InProcessMcpFixture.TextOf(members));
            var memberBody = InProcessMcpFixture.Deserialize<SymbolMembersResultDto>(members);
            Assert.Contains(memberBody.Items, i => i.Summary.DisplayName == "Ping" && i.Handle.StartsWith("vb:", StringComparison.Ordinal));
            Assert.Contains(memberBody.Items, i => i.Summary.DisplayName == "Echo" && i.Handle.StartsWith("vb:", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_find_references_implementations_hierarchy_and_callers_work_for_vb()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbSymbols(root));

            await OpenUntilReadyAsync(fx, solution);

            var widget = await ResolveHandleAsync(fx, "VbLib.Widget");
            var pingable = await ResolveHandleAsync(fx, "VbLib.IPingable");
            var members = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?> { ["handle"] = widget });
            var memberBody = InProcessMcpFixture.Deserialize<SymbolMembersResultDto>(members);
            var ping = Assert.Single(memberBody.Items, i => i.Summary.DisplayName == "Ping").Handle;

            var refsDefault = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = widget });
            Assert.True(refsDefault.IsError is not true, InProcessMcpFixture.TextOf(refsDefault));
            var defaultBody = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(refsDefault);
            Assert.Contains(defaultBody.Items, i =>
                (i.FilePath ?? string.Empty).Contains("Uses.vb", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(defaultBody.Items, i =>
                (i.FilePath ?? string.Empty).Contains("Caller.cs", StringComparison.OrdinalIgnoreCase));

            var refsAll = await fx.Client.CallToolAsync(
                "symbol_find_references",
                new Dictionary<string, object?> { ["handle"] = widget, ["entireSolution"] = true });
            Assert.True(refsAll.IsError is not true, InProcessMcpFixture.TextOf(refsAll));
            var allBody = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(refsAll);
            Assert.Contains(allBody.Items, i =>
                (i.FilePath ?? string.Empty).Contains("Caller.cs", StringComparison.OrdinalIgnoreCase));

            var impl = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?> { ["handle"] = pingable });
            Assert.True(impl.IsError is not true, InProcessMcpFixture.TextOf(impl));
            var implBody = InProcessMcpFixture.Deserialize<SymbolFindImplementationsResultDto>(impl);
            Assert.Contains(implBody.Items, i => i.Summary.DisplayName == "Widget");
            Assert.All(implBody.Items, i => Assert.StartsWith("vb:", i.Handle, StringComparison.Ordinal));

            var derived = await fx.Client.CallToolAsync(
                "symbol_find_implementations",
                new Dictionary<string, object?> { ["handle"] = widget });
            Assert.True(derived.IsError is not true, InProcessMcpFixture.TextOf(derived));
            var derivedBody = InProcessMcpFixture.Deserialize<SymbolFindImplementationsResultDto>(derived);
            Assert.Contains(derivedBody.Items, i => i.Summary.DisplayName == "SpecialWidget");

            var hierarchy = await fx.Client.CallToolAsync(
                "symbol_type_hierarchy",
                new Dictionary<string, object?> { ["handle"] = widget });
            Assert.True(hierarchy.IsError is not true, InProcessMcpFixture.TextOf(hierarchy));
            var hierBody = InProcessMcpFixture.Deserialize<SymbolTypeHierarchyResultDto>(hierarchy);
            Assert.Contains(hierBody.Items, i => i.Summary.DisplayName == "IPingable");

            var callers = await fx.Client.CallToolAsync(
                "symbol_find_callers",
                new Dictionary<string, object?> { ["handle"] = ping });
            Assert.True(callers.IsError is not true, InProcessMcpFixture.TextOf(callers));
            var callerBody = InProcessMcpFixture.Deserialize<SymbolFindCallersResultDto>(callers);
            Assert.Contains(callerBody.Items, i =>
                (i.FilePath ?? string.Empty).Contains("Uses.vb", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(callerBody.Items, i =>
                (i.FilePath ?? string.Empty).Contains("Caller.cs", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task csharp_handles_still_resolve_in_mixed_workspace()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbSymbols(root));

            await OpenUntilReadyAsync(fx, solution);
            var result = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "CsLib.Caller" });
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result);
            Assert.StartsWith("csharp:", body.Handle, StringComparison.Ordinal);
            Assert.Equal("csharp", body.Summary.Language);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task invalid_and_stale_vb_handles_are_distinguishable()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbSymbols(root));

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "VbLib.Widget");

            var badChecksum = handle[..^1] + (handle[^1] == '0' ? '1' : '0');
            var invalid = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = badChecksum });
            Assert.True(invalid.IsError is true);
            var invalidBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(invalid);
            Assert.Equal(PolicyErrorCodes.InvalidSymbolHandle, invalidBody.Error);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
            var vbProject = Assert.Single(projects.Projects, p => p.Language == "vb");
            var stale = SymbolHandle.Create(
                LanguageAdapters.VbLanguage,
                vbProject.ProjectId,
                "VbLib.DoesNotExist");
            var missing = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = stale.Format() });
            Assert.True(missing.IsError is true);
            var missingBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(missing);
            Assert.Equal(PolicyErrorCodes.SymbolNotFound, missingBody.Error);

            var otherLang = SymbolHandle.Create("python", vbProject.ProjectId, "VbLib.Widget");
            var unsupported = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = otherLang.Format() });
            Assert.True(unsupported.IsError is true);
            var unsupportedBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(unsupported);
            Assert.Equal(PolicyErrorCodes.InvalidSymbolHandle, unsupportedBody.Error);
            Assert.Contains("Unsupported language", InProcessMcpFixture.TextOf(unsupported), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task msbuild_mixed_solution_resolves_vb_type_and_goto_definition()
    {
        var slnx = Path.Combine(
            AppContext.BaseDirectory, "fixtures", "MixedCsharpVb", "Mixed.slnx");
        Assert.True(File.Exists(slnx), $"Missing fixture: {slnx}");
        var root = Path.GetDirectoryName(slnx)!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            new MsBuildSolutionLoader());

        await OpenUntilReadyAsync(fx, slnx, TimeSpan.FromSeconds(90));
        var handle = await ResolveHandleAsync(fx, "VbLib.Widget");
        Assert.StartsWith("vb:", handle, StringComparison.Ordinal);

        var gotoDef = await fx.Client.CallToolAsync(
            "symbol_goto_definition",
            new Dictionary<string, object?> { ["handle"] = handle });
        Assert.True(gotoDef.IsError is not true, InProcessMcpFixture.TextOf(gotoDef));
        var def = InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(gotoDef);
        Assert.Contains(def.Locations, loc =>
            (loc.FilePath ?? string.Empty).Contains("Widget.vb", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> ResolveHandleAsync(InProcessMcpFixture fx, string name)
    {
        var result = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name });
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result).Handle;
    }

    private static async Task OpenUntilReadyAsync(
        InProcessMcpFixture fx,
        string path,
        TimeSpan? timeout = null)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        WorkspaceStatusDto? last = null;
        while (DateTime.UtcNow < deadline)
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
