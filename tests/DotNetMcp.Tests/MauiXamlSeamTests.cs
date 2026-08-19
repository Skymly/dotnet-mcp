using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class MauiXamlSeamTests
{
    [Fact]
    public async Task maui_xaml_resolve_class_name_and_binding()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var xaml = Path.Combine(root, "MainPage.xaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(xaml, """
            <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="clr-namespace:MauiPage"
                         x:Class="MauiPage.MainPage"
                         x:DataType="local:MainViewModel">
                <Label x:Name="TitleLabel" Text="{Binding Title}" />
            </ContentPage>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithMaui());

            await OpenUntilReadyAsync(fx, solution);

            var cls = await fx.Client.CallToolAsync(
                "xaml_resolve_class",
                new Dictionary<string, object?> { ["path"] = xaml });
            Assert.True(cls.IsError is not true, InProcessMcpFixture.TextOf(cls));
            var classBody = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(cls);
            Assert.Equal("MainPage", classBody.Summary.DisplayName);

            var name = await fx.Client.CallToolAsync(
                "xaml_resolve_name",
                new Dictionary<string, object?>
                {
                    ["path"] = xaml,
                    ["name"] = "TitleLabel"
                });
            Assert.True(name.IsError is not true, InProcessMcpFixture.TextOf(name));
            Assert.Equal("TitleLabel", InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(name).Summary.DisplayName);

            var binding = await fx.Client.CallToolAsync(
                "xaml_resolve_binding",
                new Dictionary<string, object?>
                {
                    ["path"] = xaml,
                    ["bindingPath"] = "Title"
                });
            Assert.True(binding.IsError is not true, InProcessMcpFixture.TextOf(binding));
            var bindBody = InProcessMcpFixture.Deserialize<XamlResolveBindingResultDto>(binding);
            Assert.Contains(bindBody.Items, i => i.Name == "Title");

            var xmlns = await fx.Client.CallToolAsync(
                "xaml_list_xmlns",
                new Dictionary<string, object?> { ["path"] = xaml });
            Assert.True(xmlns.IsError is not true, InProcessMcpFixture.TextOf(xmlns));

            var diagnostics = await fx.Client.CallToolAsync(
                "xaml_diagnostics",
                new Dictionary<string, object?> { ["path"] = xaml });
            Assert.True(diagnostics.IsError is not true, InProcessMcpFixture.TextOf(diagnostics));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task wpf_xaml_is_still_unsupported()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var xaml = Path.Combine(root, "MainWindow.xaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(xaml, """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="WpfApp.MainWindow">
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithMaui());

            await OpenUntilReadyAsync(fx, solution);
            var result = await fx.Client.CallToolAsync(
                "xaml_resolve_class",
                new Dictionary<string, object?> { ["path"] = xaml });
            Assert.True(result.IsError is true);
            Assert.Equal(
                PolicyErrorCodes.UnsupportedXamlDocument,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(result).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string path)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
        for (var i = 0; i < 80; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            if (InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll).Phase == "ready")
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("workspace did not become ready");
    }

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-maui-" + prefix + "-" + Guid.NewGuid().ToString("N"));
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
