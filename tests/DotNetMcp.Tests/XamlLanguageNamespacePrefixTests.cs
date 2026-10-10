using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class XamlLanguageNamespacePrefixTests
{
    [Fact]
    public async Task diagnostics_follow_xaml_namespace_when_prefix_is_not_x()
    {
        var root = CreateTempDir();
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:lang="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:x="using:SampleApp"
                    lang:Class="SampleApp.MainWindow">
                <lang:NullExtension />
                <x:NoSuchControl />
                <TextBlock lang:Name="GhostName" />
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithAvalonia());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_diagnostics",
                new Dictionary<string, object?> { ["path"] = axaml });
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(result);

            Assert.Contains(body.Items, i => i.Id == "XAML0004" && i.Message.Contains("GhostName", StringComparison.Ordinal));
            Assert.DoesNotContain(body.Items, i => i.Id == "XAML0001" && i.Message.Contains("NullExtension", StringComparison.Ordinal));
            Assert.Contains(body.Items, i => i.Id == "XAML0001" && i.Message.Contains("NoSuchControl", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "dotnet-mcp-xaml-ns-" + Guid.NewGuid().ToString("N"));
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}