using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class XamlResolveBindingSeamTests
{
    [Fact]
    public async Task xaml_resolve_binding_walks_nested_path_to_property_handles()
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
                <TextBlock Text="{Binding Home.City}" />
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithAvalonia());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_resolve_binding",
                new Dictionary<string, object?> { ["path"] = axaml, ["bindingPath"] = "Home.City" });

            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<XamlResolveBindingResultDto>(result);
            Assert.Equal(2, body.Items.Count);
            Assert.Equal("Home", body.Items[0].Name);
            Assert.Equal("City", body.Items[1].Name);
            Assert.True(SymbolHandle.TryParse(body.Items[0].Handle, out _, out _));
            Assert.True(SymbolHandle.TryParse(body.Items[1].Handle, out _, out _));
            Assert.Equal("Property", body.Items[0].Summary.Kind);
            Assert.Equal("Property", body.Items[1].Summary.Kind);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xaml_resolve_binding_resolves_parent_interface_property_on_interface_datatype()
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
                    x:DataType="local:ICustomer">
                <TextBlock Text="{Binding Name}" />
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithAvalonia());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_resolve_binding",
                new Dictionary<string, object?> { ["path"] = axaml, ["bindingPath"] = "Name" });

            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<XamlResolveBindingResultDto>(result);
            var item = Assert.Single(body.Items);
            Assert.Equal("Name", item.Name);
            Assert.Equal("Property", item.Summary.Kind);
            Assert.True(SymbolHandle.TryParse(item.Handle, out _, out _));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xaml_resolve_binding_distinguishes_missing_property_from_type_mismatch()
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
                    x:DataType="local:Customer" />
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithAvalonia());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var missing = await fx.Client.CallToolAsync(
                "xaml_resolve_binding",
                new Dictionary<string, object?> { ["path"] = axaml, ["bindingPath"] = "NoSuch" });
            Assert.True(missing.IsError is true);
            var missingBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(missing);
            Assert.Equal(PolicyErrorCodes.BindingPropertyNotFound, missingBody.Error);
            Assert.NotEqual(PolicyErrorCodes.InvalidSymbolHandle, missingBody.Error);

            var mismatch = await fx.Client.CallToolAsync(
                "xaml_resolve_binding",
                new Dictionary<string, object?> { ["path"] = axaml, ["bindingPath"] = "Save" });
            Assert.True(mismatch.IsError is true);
            var mismatchBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(mismatch);
            Assert.Equal(PolicyErrorCodes.BindingTypeMismatch, mismatchBody.Error);
            Assert.NotEqual(missingBody.Error, mismatchBody.Error);
        }
        finally
        {
            TryDelete(root);
        }
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
