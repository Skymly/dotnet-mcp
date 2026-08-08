using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class MsBuildWorkspaceIntegrationTests
{
    public static string FixturesRoot { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "fixtures"));

    public static string SampleSlnx => Path.Combine(FixturesRoot, "SampleFilter", "Sample.slnx");
    public static string SampleSlnf => Path.Combine(FixturesRoot, "SampleFilter", "Sample.slnf");
    public static string MultiTfmProject => Path.Combine(FixturesRoot, "MultiTfm", "MultiTfm.csproj");

    [Fact]
    public async Task workspace_open_slnx_status_ready_lists_sample_projects()
    {
        Assert.True(File.Exists(SampleSlnx), $"Missing fixture: {SampleSlnx}");
        var root = Path.GetDirectoryName(SampleSlnx)!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            new MsBuildSolutionLoader());

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = SampleSlnx });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await PollReadyAsync(fx, TimeSpan.FromSeconds(90));
        Assert.Equal("ready", status.Phase);

        var list = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
        var body = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);

        Assert.Contains(body.Projects, p => p.Name.Contains("LibA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(body.Projects, p => p.Name.Contains("LibB", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(body.Projects, p => p.Name.Contains("App", StringComparison.OrdinalIgnoreCase));
        Assert.True(body.Projects.Count >= 3);
    }

    [Fact]
    public async Task workspace_open_slnf_loads_filtered_projects()
    {
        Assert.True(File.Exists(SampleSlnf), $"Missing fixture: {SampleSlnf}");
        var root = Path.GetDirectoryName(SampleSlnf)!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            new MsBuildSolutionLoader());

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = SampleSlnf });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await PollReadyAsync(fx, TimeSpan.FromSeconds(90));
        Assert.Equal("ready", status.Phase);

        var list = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
        var body = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);

        Assert.Contains(body.Projects, p => p.Name.Contains("LibA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(body.Projects, p => p.Name.Contains("App", StringComparison.OrdinalIgnoreCase));
        Assert.True(body.Projects.Count >= 2);
    }

    [Fact]
    public async Task workspace_open_multi_tfm_project_lists_separate_rows()
    {
        Assert.True(File.Exists(MultiTfmProject), $"Missing fixture: {MultiTfmProject}");
        var root = Path.GetDirectoryName(MultiTfmProject)!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            new MsBuildSolutionLoader());

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = MultiTfmProject });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await PollReadyAsync(fx, TimeSpan.FromSeconds(90));
        Assert.Equal("ready", status.Phase);

        var list = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
        var body = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);

        Assert.True(body.Projects.Count >= 2, $"Expected >=2 TFM rows, got {body.Projects.Count}: {string.Join(", ", body.Projects.Select(p => p.Name))}");
        Assert.Contains(body.Projects, p =>
            (p.TargetFramework?.Contains("net8", StringComparison.OrdinalIgnoreCase) ?? false) ||
            p.Name.Contains("net8", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(body.Projects, p =>
            (p.TargetFramework?.Contains("net9", StringComparison.OrdinalIgnoreCase) ?? false) ||
            p.Name.Contains("net9", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<WorkspaceStatusDto> PollReadyAsync(InProcessMcpFixture fx, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        WorkspaceStatusDto? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var result = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
            last = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(result);
            if (last.Phase is "ready" or "failed" or "cancelled")
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.NotNull(last);
        if (last!.Phase == "failed")
        {
            Assert.Fail($"Workspace load failed: {last.Error}");
        }

        return last;
    }
}
