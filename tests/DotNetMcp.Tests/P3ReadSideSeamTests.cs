using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class P3ReadSideSeamTests
{
    [Fact]
    public async Task binding_uses_static_datacontext_type_without_xdatatype()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="SampleApp.MainWindow">
                <TextBlock Text="{Binding Name}" />
            </Window>
            """);
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithDataContext());
            await OpenUntilReadyAsync(fx, solution);
            var binding = await fx.Client.CallToolAsync(
                "xaml_resolve_binding",
                new Dictionary<string, object?>
                {
                    ["path"] = axaml,
                    ["bindingPath"] = "Name"
                });
            Assert.True(binding.IsError is not true, InProcessMcpFixture.TextOf(binding));
            var body = InProcessMcpFixture.Deserialize<XamlResolveBindingResultDto>(binding);
            Assert.Contains(body.Items, i => i.Name == "Name");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xaml_resolve_class_returns_vb_handle()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="MainWindow" />
            """);
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbXaml());
            await OpenUntilReadyAsync(fx, solution);
            var result = await fx.Client.CallToolAsync(
                "xaml_resolve_class",
                new Dictionary<string, object?> { ["path"] = axaml });
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result).Handle;
            Assert.StartsWith("vb:", handle, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xaml_diagnostics_resolves_vb_element_types()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:SampleControls"
                    x:Class="MainWindow">
                <local:FancyPanel />
                <local:MissingPanel />
            </Window>
            """);
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbXaml());
            await OpenUntilReadyAsync(fx, solution);

            var cls = await fx.Client.CallToolAsync(
                "xaml_resolve_class",
                new Dictionary<string, object?> { ["path"] = axaml });
            Assert.True(cls.IsError is not true, InProcessMcpFixture.TextOf(cls));
            Assert.StartsWith("vb:", InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(cls).Handle, StringComparison.Ordinal);

            var diagnostics = await fx.Client.CallToolAsync(
                "xaml_diagnostics",
                new Dictionary<string, object?> { ["path"] = axaml });
            Assert.True(diagnostics.IsError is not true, InProcessMcpFixture.TextOf(diagnostics));
            var body = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(diagnostics);
            Assert.Contains(body.Items, i => i.Id == "XAML0001" && i.Message.Contains("MissingPanel", StringComparison.Ordinal));
            Assert.DoesNotContain(body.Items, i => i.Message.Contains("FancyPanel", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task project_diagnostics_without_project_id_pages_across_projects()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithVbAndCSharp(
                    Path.Combine(root, "cs", "CsLib.csproj"),
                    Path.Combine(root, "vb", "VbLib.vbproj")));
            await OpenUntilReadyAsync(fx, solution);
            var result = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?>());
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(result);
            Assert.NotNull(body.Items);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task OpenUntilReadyAsync
(InProcessMcpFixture fx, string path)
    {
        Assert.True((await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = path })).IsError is not true);
        for (var i = 0; i < 80; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            if (InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll).Phase == "ready")
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("not ready");
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-p3-" + prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
