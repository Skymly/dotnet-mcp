using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class XamlResolveNameSeamTests
{
    [Fact]
    public async Task xaml_resolve_name_maps_xname_to_namegenerator_field_handle()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="SampleApp.MainWindow">
                <TextBlock x:Name="TitleText" Text="Hi" />
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());
            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_resolve_name",
                new Dictionary<string, object?> { ["path"] = axaml, ["name"] = "TitleText" });

            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result);
            Assert.Equal("TitleText", body.Summary.DisplayName);
            Assert.Equal("Field", body.Summary.Kind);

            var attr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?> { ["handle"] = body.Handle });
            Assert.True(attr.IsError is not true, InProcessMcpFixture.TextOf(attr));
            var attribution = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attr);
            Assert.Equal("SourceGenerator", attribution.OriginKind);
            Assert.NotNull(attribution.Generator);
            Assert.Equal("CustomGenerator", attribution.Generator!.AssemblyName);
            Assert.Equal("Avalonia.NameGenerator.NameGenerator", attribution.Generator.TypeFullName);
            Assert.Equal("1.2.3.0", attribution.Generator.Version);

            var typeResolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleApp.MainWindow" });
            var typeOk = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(typeResolved);
            var typeAttr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?> { ["handle"] = typeOk.Handle });
            var typeBody = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(typeAttr);
            Assert.Equal("Handwritten", typeBody.OriginKind);
            var handwritten = typeBody.Members.Values.Count(m => m.OriginKind == "Handwritten");
            var generated = typeBody.Members.Values.Count(m => m.OriginKind == "SourceGenerator");
            Assert.True(handwritten > 0);
            Assert.True(generated > 0);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xaml_resolve_name_distinguishes_missing_name_from_generator_not_run()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="SampleApp.MainWindow">
                <TextBlock x:Name="MissingField" Text="Hi" />
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());
            await OpenUntilReadyAsync(fx, solution);

            var missing = await fx.Client.CallToolAsync(
                "xaml_resolve_name",
                new Dictionary<string, object?> { ["path"] = axaml, ["name"] = "NoSuchName" });
            Assert.True(missing.IsError is true);
            var missingBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(missing);
            Assert.Equal(PolicyErrorCodes.MissingXamlName, missingBody.Error);

            var notRun = await fx.Client.CallToolAsync(
                "xaml_resolve_name",
                new Dictionary<string, object?> { ["path"] = axaml, ["name"] = "MissingField" });
            Assert.True(notRun.IsError is true);
            var notRunBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(notRun);
            Assert.Equal(PolicyErrorCodes.NameGeneratorNotRun, notRunBody.Error);
            Assert.NotEqual(missingBody.Error, notRunBody.Error);
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
