using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class SymbolResolveSeamTests
{
    [Fact]
    public async Task symbol_resolve_returns_handle_and_lightweight_summary()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithSymbols());

            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleLib.Calculator" });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result);

            Assert.StartsWith("csharp:", body.Handle, StringComparison.Ordinal);
            Assert.Contains('#', body.Handle);
            Assert.True(SymbolHandle.TryParse(body.Handle, out var parsed, out _), body.Handle);
            Assert.Equal("csharp", parsed!.Language);
            Assert.Equal(body.Summary.ProjectId, parsed.ProjectId);
            Assert.Equal("NamedType", body.Summary.Kind);
            Assert.Equal("Calculator", body.Summary.DisplayName);
            Assert.Equal("csharp", body.Summary.Language);

            // Lightweight: no member tree payload on the DTO.
            var json = InProcessMcpFixture.TextOf(result);
            Assert.DoesNotContain("\"members\"", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"memberTree\"", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_summary_rejects_bad_checksum_as_invalid_handle()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithSymbols());

            await OpenUntilReadyAsync(fx, solution);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleLib.Calculator" });
            var ok = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);
            var bad = ok.Handle[..^1] + (ok.Handle[^1] == '0' ? '1' : '0');

            var summary = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = bad });

            Assert.True(summary.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(summary);
            Assert.Equal(PolicyErrorCodes.InvalidSymbolHandle, body.Error);
            Assert.Contains("symbol_resolve", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_summary_reports_symbol_not_found_for_valid_missing_handle()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithSymbols());

            await OpenUntilReadyAsync(fx, solution);

            var list = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
            var projectId = Assert.Single(projects.Projects).ProjectId;

            var ghost = SymbolHandle.Create(
                SymbolQueryService.CSharpLanguage,
                projectId,
                "SampleLib.DoesNotExist");

            var summary = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = ghost.Format() });

            Assert.True(summary.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(summary);
            Assert.Equal(PolicyErrorCodes.SymbolNotFound, body.Error);
            Assert.Contains("symbol_resolve", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("do not invent", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_resolve_unknown_name_is_symbol_not_found()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithSymbols());

            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "NoSuchType" });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.SymbolNotFound, body.Error);
            Assert.Contains("symbol_resolve", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_resolve_errors_with_workspace_not_ready_while_loading()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.DelayedWithSymbols(TimeSpan.FromMilliseconds(1000)));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true);

            var resolve = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleLib.Calculator" });

            Assert.True(resolve.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(resolve);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, body.Error);
            Assert.Contains("workspace_status", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_summary_round_trips_resolved_handle()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithSymbols());

            await OpenUntilReadyAsync(fx, solution);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "Calculator" });
            var ok = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);

            var summary = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = ok.Handle });

            Assert.True(summary.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(summary);
            Assert.Equal(ok.Handle, body.Handle);
            Assert.Equal(ok.Summary.DisplayName, body.Summary.DisplayName);
        }
        finally
        {
            TryDelete(root);
        }
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
