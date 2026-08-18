using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// P1 exit gate (#52): one MCP-boundary walk of the demoable Avalonia loop. No new tools.
/// </summary>
public class P1ExitGateSeamTests
{
    [Fact]
    public async Task p1_avalonia_loop_open_xclass_xmlns_xname_binding_diagnostics()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:SampleApp"
                    x:Class="SampleApp.MainWindow"
                    x:DataType="local:Customer">
                <TextBlock x:Name="TitleText" Text="{Binding Name}" />
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            await OpenUntilReadyAsync(fx);

            var xclass = await fx.Client.CallToolAsync(
                "xaml_resolve_class",
                new Dictionary<string, object?> { ["path"] = axaml });
            Assert.True(xclass.IsError is not true, InProcessMcpFixture.TextOf(xclass));
            var classBody = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(xclass);
            Assert.Equal("MainWindow", classBody.Summary.DisplayName);
            Assert.True(SymbolHandle.TryParse(classBody.Handle, out _, out _));

            var gotoDef = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = classBody.Handle });
            Assert.True(gotoDef.IsError is not true);

            var xmlns = await fx.Client.CallToolAsync(
                "xaml_list_xmlns",
                new Dictionary<string, object?> { ["path"] = axaml });
            Assert.True(xmlns.IsError is not true, InProcessMcpFixture.TextOf(xmlns));
            var xmlnsBody = InProcessMcpFixture.Deserialize<XamlListXmlnsResultDto>(xmlns);
            Assert.Contains(xmlnsBody.Items, i => i.Prefix == "local" && i.ClrNamespace == "SampleApp");

            var xname = await fx.Client.CallToolAsync(
                "xaml_resolve_name",
                new Dictionary<string, object?> { ["path"] = axaml, ["name"] = "TitleText" });
            Assert.True(xname.IsError is not true, InProcessMcpFixture.TextOf(xname));
            var nameBody = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(xname);
            var attr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?> { ["handle"] = nameBody.Handle });
            Assert.True(attr.IsError is not true);
            Assert.Equal("SourceGenerator",
                InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attr).OriginKind);

            var binding = await fx.Client.CallToolAsync(
                "xaml_resolve_binding",
                new Dictionary<string, object?> { ["path"] = axaml, ["bindingPath"] = "Name" });
            Assert.True(binding.IsError is not true, InProcessMcpFixture.TextOf(binding));
            Assert.Equal("Name",
                Assert.Single(InProcessMcpFixture.Deserialize<XamlResolveBindingResultDto>(binding).Items).Name);

            var diags = await fx.Client.CallToolAsync(
                "xaml_diagnostics",
                new Dictionary<string, object?> { ["path"] = axaml });
            Assert.True(diags.IsError is not true, InProcessMcpFixture.TextOf(diags));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx)
    {
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
        }
    }
}
