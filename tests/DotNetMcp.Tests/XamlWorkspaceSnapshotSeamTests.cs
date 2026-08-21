using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class XamlWorkspaceSnapshotSeamTests
{
    [Fact]
    public async Task xaml_binding_and_diagnostics_use_workspace_snapshot_not_disk()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        const string diskText = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:SampleApp"
                    x:Class="SampleApp.DoesNotExist"
                    x:DataType="local:Address">
                <local:DiskOnlyControl />
                <TextBlock Text="{Binding NoSuch}" />
            </Window>
            """;
        const string snapshotText = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:SampleApp"
                    x:Class="SampleApp.MainWindow"
                    x:DataType="local:Customer">
                <local:SnapshotOnlyControl />
                <TextBlock Text="{Binding Home.City}" />
            </Window>
            """;
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, diskText);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvaloniaXamlSnapshot(axaml, snapshotText));
            await OpenUntilReadyAsync(fx, solution);

            var binding = await fx.Client.CallToolAsync(
                "xaml_resolve_binding",
                new Dictionary<string, object?> { ["path"] = axaml, ["bindingPath"] = "Home.City" });
            Assert.True(binding.IsError is not true, InProcessMcpFixture.TextOf(binding));
            var bindBody = InProcessMcpFixture.Deserialize<XamlResolveBindingResultDto>(binding);
            Assert.Equal(2, bindBody.Items.Count);
            Assert.Equal("Home", bindBody.Items[0].Name);
            Assert.Equal("City", bindBody.Items[1].Name);

            var diagnostics = await fx.Client.CallToolAsync(
                "xaml_diagnostics",
                new Dictionary<string, object?> { ["path"] = axaml });
            Assert.True(diagnostics.IsError is not true, InProcessMcpFixture.TextOf(diagnostics));
            var diagBody = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(diagnostics);
            Assert.Contains(diagBody.Items, i => i.Id == "XAML0001" && i.Message.Contains("SnapshotOnlyControl", StringComparison.Ordinal));
            Assert.DoesNotContain(diagBody.Items, i => i.Message.Contains("DiskOnlyControl", StringComparison.Ordinal));
            Assert.DoesNotContain(diagBody.Items, i => i.Message.Contains("NoSuch", StringComparison.Ordinal));

            Assert.Equal(diskText.Replace("\r\n", "\n"), (await File.ReadAllTextAsync(axaml)).Replace("\r\n", "\n"));
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
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
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
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-xaml-snap-{label}-{Guid.NewGuid():N}");
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
