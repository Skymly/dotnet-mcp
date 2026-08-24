using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class SymbolGotoMembersSeamTests
{
    [Fact]
    public async Task symbol_goto_definition_returns_file_span_and_handwritten_origin()
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
                new Dictionary<string, object?> { ["name"] = "SampleLib.Calculator.Subtract" });
            var ok = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);

            var gotoDef = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = ok.Handle });

            Assert.True(gotoDef.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(gotoDef);
            Assert.NotEmpty(body.Locations);
            var loc = Assert.Single(body.Locations);
            Assert.Equal("InSource", loc.DeclarationAvailability);
            Assert.Equal("Handwritten", loc.Origin);
            Assert.False(string.IsNullOrWhiteSpace(loc.FilePath));
            Assert.Contains("Calculator.cs", loc.FilePath!, StringComparison.OrdinalIgnoreCase);
            Assert.True(loc.Start >= 0);
            Assert.True(loc.Length > 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_goto_definition_does_not_trust_gcs_path_alone_as_generator_origin()
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
                new Dictionary<string, object?> { ["name"] = "GeneratedAnswer" });
            var ok = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);

            var gotoDef = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = ok.Handle });

            Assert.True(gotoDef.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(gotoDef);
            var loc = Assert.Single(body.Locations);
            Assert.Equal("InSource", loc.DeclarationAvailability);
            // Fake .g.cs documents are not SourceGeneratedDocuments and are not produced by a
            // GeneratorDriver — FilePath heuristics alone must not label them SourceGenerator.
            Assert.Equal("Handwritten", loc.Origin);
            Assert.Contains(".g.cs", loc.FilePath!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_goto_definition_rejects_bad_checksum_as_invalid_handle()
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
            var bad = ok.Handle[..^1] + (ok.Handle[^1] == '0' ? '1' : '0');

            var gotoDef = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = bad });

            Assert.True(gotoDef.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(gotoDef);
            Assert.Equal(PolicyErrorCodes.InvalidSymbolHandle, body.Error);
            Assert.Contains("symbol_resolve", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_members_pages_with_epoch_cursor_and_continues()
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

            var page1 = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?>
                {
                    ["handle"] = ok.Handle,
                    ["limit"] = 2
                });

            Assert.True(page1.IsError is not true);
            var first = InProcessMcpFixture.Deserialize<SymbolMembersResultDto>(page1);
            Assert.Equal(2, first.Items.Count);
            Assert.True(first.Truncated);
            Assert.False(string.IsNullOrWhiteSpace(first.NextCursor));
            Assert.Contains("nextCursor", first.Message, StringComparison.OrdinalIgnoreCase);

            var page2 = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?>
                {
                    ["handle"] = ok.Handle,
                    ["limit"] = 2,
                    ["cursor"] = first.NextCursor
                });

            Assert.True(page2.IsError is not true);
            var second = InProcessMcpFixture.Deserialize<SymbolMembersResultDto>(page2);
            Assert.NotEmpty(second.Items);
            Assert.DoesNotContain(second.Items, m => first.Items.Any(f => f.Handle == m.Handle));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_members_rejects_stale_cursor()
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

            // Epoch 1 is used after first successful load; forge cursor with epoch 999.
            var stale = MemberPageCursor.Encode(epoch: 999, offset: 0);

            var members = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?>
                {
                    ["handle"] = ok.Handle,
                    ["cursor"] = stale
                });

            Assert.True(members.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(members);
            Assert.Equal(PolicyErrorCodes.StaleCursor, body.Error);
            Assert.Contains("symbol_members", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("without", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_members_rejects_non_type_handle()
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
                new Dictionary<string, object?> { ["name"] = "SampleLib.Calculator.Clear" });
            var ok = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);

            var members = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?> { ["handle"] = ok.Handle });

            Assert.True(members.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(members);
            Assert.Equal(PolicyErrorCodes.SymbolNotFound, body.Error);
            Assert.Contains("type", body.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_goto_definition_errors_with_workspace_not_ready_while_loading()
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

            var ghost = SymbolHandle.Create(
                LanguageAdapters.CSharpLanguage,
                Guid.NewGuid().ToString("D"),
                "SampleLib.Calculator");

            var gotoDef = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = ghost.Format() });

            Assert.True(gotoDef.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(gotoDef);
            Assert.Equal(PolicyErrorCodes.WorkspaceNotReady, body.Error);
            Assert.Contains("workspace_status", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
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
