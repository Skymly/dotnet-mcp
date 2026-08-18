using DotNetMcp.Server;
using DotNetMcp.Xaml;

namespace DotNetMcp.Tests;

public class XamlListXmlnsSeamTests
{
    [Fact]
    public async Task xaml_list_xmlns_resolves_using_and_clr_namespace()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:SampleApp"
                    xmlns:c="clr-namespace:SampleControls;assembly=ControlsLib"
                    x:Class="SampleApp.MainWindow" />
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());
            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_list_xmlns",
                new Dictionary<string, object?> { ["path"] = axaml });

            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<XamlListXmlnsResultDto>(result);

            var local = Assert.Single(body.Items, i => i.Prefix == "local");
            Assert.Equal("SampleApp", local.ClrNamespace);
            Assert.Equal("AvaloniaApp", local.AssemblyName);
            Assert.Equal(XamlXmlnsSource.Using, local.Source);

            var clr = Assert.Single(body.Items, i => i.Prefix == "c");
            Assert.Equal("SampleControls", clr.ClrNamespace);
            Assert.Equal("ControlsLib", clr.AssemblyName);
            Assert.Equal(XamlXmlnsSource.ClrNamespace, clr.Source);

            var avalonia = body.Items.Where(i => i.Prefix == "" && i.Source == XamlXmlnsSource.XmlnsDefinition).ToArray();
            Assert.Contains(avalonia, i => i.ClrNamespace == "SampleControls" && i.AssemblyName == "ControlsLib");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xaml_list_xmlns_unknown_prefix_is_not_missing_document()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="SampleApp.MainWindow" />
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());
            await OpenUntilReadyAsync(fx, solution);

            var unknown = await fx.Client.CallToolAsync(
                "xaml_list_xmlns",
                new Dictionary<string, object?> { ["path"] = axaml, ["prefix"] = "nope" });
            Assert.True(unknown.IsError is true);
            var unknownBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(unknown);
            Assert.Equal(PolicyErrorCodes.UnknownXmlnsPrefix, unknownBody.Error);
            Assert.NotEqual(PolicyErrorCodes.XamlDocumentNotFound, unknownBody.Error);

            var missing = await fx.Client.CallToolAsync(
                "xaml_list_xmlns",
                new Dictionary<string, object?> { ["path"] = Path.Combine(root, "Missing.axaml") });
            Assert.True(missing.IsError is true);
            var missingBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(missing);
            Assert.Equal(PolicyErrorCodes.XamlDocumentNotFound, missingBody.Error);
            Assert.NotEqual(PolicyErrorCodes.UnknownXmlnsPrefix, missingBody.Error);
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
        }
    }
}
