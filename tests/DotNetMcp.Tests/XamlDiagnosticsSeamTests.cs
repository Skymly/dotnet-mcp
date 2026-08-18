using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class XamlDiagnosticsSeamTests
{
    [Fact]
    public async Task xaml_diagnostics_reports_semantic_problems_not_just_wellformed_xml()
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
                <local:NoSuchControl />
                <local:Customer NotAProp="1" />
                <TextBlock Text="{Binding NoSuchPath}" />
                <TextBlock x:Name="GhostName" />
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());
            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_diagnostics",
                new Dictionary<string, object?> { ["path"] = axaml });

            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(result);
            Assert.Contains(body.Items, i => i.Id == "XAML0001" && i.Message.Contains("NoSuchControl", StringComparison.Ordinal));
            Assert.Contains(body.Items, i => i.Id == "XAML0002" && i.Message.Contains("NotAProp", StringComparison.Ordinal));
            Assert.Contains(body.Items, i => i.Id == "XAML0003" && i.Message.Contains("NoSuchPath", StringComparison.Ordinal));
            Assert.Contains(body.Items, i => i.Id == "XAML0004" && i.Message.Contains("GhostName", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xaml_diagnostics_paginates_and_rejects_stale_epoch_cursor()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var unknowns = string.Join("\n", Enumerable.Range(0, 6).Select(i => $"    <local:Missing{i} />"));
        await File.WriteAllTextAsync(axaml, $"""
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:SampleApp"
                    x:Class="SampleApp.MainWindow">
            {unknowns}
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia());
            await OpenUntilReadyAsync(fx, solution);

            var page = await fx.Client.CallToolAsync(
                "xaml_diagnostics",
                new Dictionary<string, object?> { ["path"] = axaml, ["limit"] = 2 });
            Assert.True(page.IsError is not true, InProcessMcpFixture.TextOf(page));
            var body = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(page);
            Assert.True(body.Truncated);
            Assert.False(string.IsNullOrWhiteSpace(body.NextCursor));
            Assert.Equal(2, body.Items.Count);

            var stale = MemberPageCursor.Encode(epoch: 999, offset: 0);
            var staleResult = await fx.Client.CallToolAsync(
                "xaml_diagnostics",
                new Dictionary<string, object?> { ["path"] = axaml, ["cursor"] = stale });
            Assert.True(staleResult.IsError is true);
            var staleBody = InProcessMcpFixture.Deserialize<PolicyErrorDto>(staleResult);
            Assert.Equal(PolicyErrorCodes.StaleCursor, staleBody.Error);
            Assert.Contains("do not retry", staleBody.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task xaml_diagnostics_soft_budget_returns_partial_results()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:local="using:SampleApp"
                    x:Class="SampleApp.MainWindow">
                <local:MissingA />
                <local:MissingB />
            </Window>
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithAvalonia(),
                softBudgetOptions: new SoftBudgetOptions { SingleProjectCompile = TimeSpan.Zero });
            await OpenUntilReadyAsync(fx, solution);

            var result = await fx.Client.CallToolAsync(
                "xaml_diagnostics",
                new Dictionary<string, object?> { ["path"] = axaml });

            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            var body = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(result);
            Assert.True(body.Truncated);
            Assert.False(string.IsNullOrWhiteSpace(body.NextCursor));
            Assert.DoesNotContain("hard", body.Message, StringComparison.OrdinalIgnoreCase);
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
