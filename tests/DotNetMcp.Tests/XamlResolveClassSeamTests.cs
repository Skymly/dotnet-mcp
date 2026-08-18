using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class XamlResolveClassSeamTests
{
    [Fact]
    public async Task xaml_resolve_class_maps_axaml_xclass_to_codebehind_handle()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, AvaloniaMainWindowXaml("SampleApp.MainWindow"));

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());

            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_resolve_class",
                new Dictionary<string, object?> { ["path"] = axaml });

            Assert.True(result.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result);
            Assert.True(SymbolHandle.TryParse(body.Handle, out var parsed, out _), body.Handle);
            Assert.Equal("csharp", parsed!.Language);
            Assert.Equal("NamedType", body.Summary.Kind);
            Assert.Equal("MainWindow", body.Summary.DisplayName);
            Assert.Equal("SampleApp", body.Summary.ContainingSymbol);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xaml_resolve_class_rejects_path_outside_trusted_roots()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var solution = Path.Combine(root, "App.slnx");
        var secret = Path.Combine(outside, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(secret, "TOP_SECRET_CONTENT");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());

            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_resolve_class",
                new Dictionary<string, object?> { ["path"] = secret });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.PathOutsideTrustedRoots, body.Error);
            Assert.Contains("trusted root", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TOP_SECRET_CONTENT", InProcessMcpFixture.TextOf(result));
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task xaml_resolve_class_missing_xclass_is_not_path_policy()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "Bare.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui" Title="NoClass" />
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());

            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_resolve_class",
                new Dictionary<string, object?> { ["path"] = axaml });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.MissingXamlClass, body.Error);
            Assert.NotEqual(PolicyErrorCodes.PathOutsideTrustedRoots, body.Error);
            Assert.NotEqual(PolicyErrorCodes.SymbolNotFound, body.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xaml_resolve_class_unknown_type_is_symbol_not_found()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "Ghost.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, AvaloniaMainWindowXaml("SampleApp.DoesNotExist"));

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());

            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_resolve_class",
                new Dictionary<string, object?> { ["path"] = axaml });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.SymbolNotFound, body.Error);
            Assert.NotEqual(PolicyErrorCodes.PathOutsideTrustedRoots, body.Error);
            Assert.NotEqual(PolicyErrorCodes.MissingXamlClass, body.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xaml_resolve_class_rejects_non_avalonia_xaml_extension()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var xaml = Path.Combine(root, "MainWindow.xaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(xaml, AvaloniaMainWindowXaml("SampleApp.MainWindow"));

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());

            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_resolve_class",
                new Dictionary<string, object?> { ["path"] = xaml });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.UnsupportedXamlDocument, body.Error);
            Assert.Contains("not registered", body.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string AvaloniaMainWindowXaml(string className) =>
        $"""
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="{className}"
                Title="Sample" />
        """;

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
