using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class FsharpSymbolSeamTests
{
    [Fact]
    public async Task symbol_resolve_returns_fsharp_handle_for_fsharp_module()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root));

            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "FsLib.Widget" });
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result);

            Assert.StartsWith("fsharp:", body.Handle, StringComparison.Ordinal);
            Assert.True(SymbolHandle.TryParse(body.Handle, out var parsed, out _), body.Handle);
            Assert.Equal(SymbolQueryService.FSharpLanguage, parsed!.Language);
            Assert.Equal("NamedType", body.Summary.Kind);
            Assert.Equal("Widget", body.Summary.DisplayName);
            Assert.Equal("fsharp", body.Summary.Language);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_summary_goto_and_members_work_for_fsharp_handles()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root));

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "FsLib.Widget");

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
            Assert.Contains("Widget.fs", loc.FilePath!, StringComparison.OrdinalIgnoreCase);
            Assert.True(loc.Start >= 0);
            Assert.True(loc.Length > 0);

            var members = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(members.IsError is not true, InProcessMcpFixture.TextOf(members));
            var memberBody = InProcessMcpFixture.Deserialize<SymbolMembersResultDto>(members);
            Assert.Contains(memberBody.Items, i =>
                i.Summary.DisplayName == "ping" && i.Handle.StartsWith("fsharp:", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task csharp_handles_still_resolve_in_mixed_fsharp_workspace()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root));

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
    public async Task invalid_and_stale_fsharp_handles_are_distinguishable()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root));

            await OpenUntilReadyAsync(fx, solution);
            var handle = await ResolveHandleAsync(fx, "FsLib.Widget");

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
            var fsProject = Assert.Single(projects.Projects, p => p.Language == "fsharp");
            var stale = SymbolHandle.Create(
                SymbolQueryService.FSharpLanguage,
                fsProject.ProjectId,
                "FsLib.DoesNotExist");
            var missing = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = stale.Format() });
            Assert.True(missing.IsError is true);
            var missingBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(missing);
            Assert.Equal(PolicyErrorCodes.SymbolNotFound, missingBody.Error);
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

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string path)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        WorkspaceStatusDto? last = null;
        for (var i = 0; i < 80; i++)
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
        }
    }
}
