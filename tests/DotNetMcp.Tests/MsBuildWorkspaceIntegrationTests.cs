using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class MsBuildWorkspaceIntegrationTests
{
    public static string FixturesRoot { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "fixtures"));

    public static string SampleSlnx => Path.Combine(FixturesRoot, "SampleFilter", "Sample.slnx");
    public static string SampleSlnf => Path.Combine(FixturesRoot, "SampleFilter", "Sample.slnf");
    public static string MultiTfmProject => Path.Combine(FixturesRoot, "MultiTfm", "MultiTfm.csproj");
    public static string VbProject => Path.Combine(FixturesRoot, "MixedCsharpVb", "VbLib", "VbLib.vbproj");
    public static string MixedSlnx => Path.Combine(FixturesRoot, "MixedCsharpVb", "Mixed.slnx");
    public static string FsProject => Path.Combine(FixturesRoot, "MixedCsharpVb", "FsLib", "FsLib.fsproj");
    public static string MixedWithFsSlnx => Path.Combine(FixturesRoot, "MixedCsharpVb", "MixedWithFs.slnx");

    [Fact]
    public async Task workspace_open_slnx_status_ready_lists_sample_projects()
    {
        Assert.True(File.Exists(SampleSlnx), $"Missing fixture: {SampleSlnx}");
        var root = Path.GetDirectoryName(SampleSlnx)!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            new MsBuildSolutionLoader(TrustedRoots.Create([Directory.GetCurrentDirectory()])));

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
            new MsBuildSolutionLoader(TrustedRoots.Create([Directory.GetCurrentDirectory()])));

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
            new MsBuildSolutionLoader(TrustedRoots.Create([Directory.GetCurrentDirectory()])));

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


    [Fact]
    public async Task workspace_open_vbproj_reaches_ready_and_lists_vb_language()
    {
        Assert.True(File.Exists(VbProject), $"Missing fixture: {VbProject}");
        var root = Path.GetDirectoryName(Path.GetDirectoryName(VbProject))!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            new MsBuildSolutionLoader(TrustedRoots.Create([Directory.GetCurrentDirectory()])));

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = VbProject });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await PollReadyAsync(fx, TimeSpan.FromSeconds(90));
        Assert.Equal("ready", status.Phase);

        var list = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
        var body = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);

        Assert.Contains(body.Projects, p =>
            p.Name.Contains("VbLib", StringComparison.OrdinalIgnoreCase) && p.Language == "vb");
        Assert.All(body.Projects, p => Assert.Equal("vb", p.Language));
    }

    [Fact]
    public async Task workspace_open_mixed_solution_lists_csharp_and_vb_languages()
    {
        Assert.True(File.Exists(MixedSlnx), $"Missing fixture: {MixedSlnx}");
        var root = Path.GetDirectoryName(MixedSlnx)!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            new MsBuildSolutionLoader(TrustedRoots.Create([Directory.GetCurrentDirectory()])));

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = MixedSlnx });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await PollReadyAsync(fx, TimeSpan.FromSeconds(90));
        Assert.Equal("ready", status.Phase);

        var list = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
        var body = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);

        Assert.Contains(body.Projects, p =>
            p.Name.Contains("CsLib", StringComparison.OrdinalIgnoreCase) && p.Language == "csharp");
        Assert.Contains(body.Projects, p =>
            p.Name.Contains("VbLib", StringComparison.OrdinalIgnoreCase) && p.Language == "vb");
    }


    [Fact]
    public async Task workspace_open_fsproj_reaches_ready_and_lists_fsharp_language()
    {
        Assert.True(File.Exists(FsProject), $"Missing fixture: {FsProject}");
        var root = Path.GetDirectoryName(Path.GetDirectoryName(FsProject))!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            new MsBuildSolutionLoader(TrustedRoots.Create([Directory.GetCurrentDirectory()])));

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = FsProject });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await PollReadyAsync(fx, TimeSpan.FromSeconds(90));
        Assert.Equal("ready", status.Phase);

        var list = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
        var body = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);

        Assert.Contains(body.Projects, p =>
            p.Name.Contains("FsLib", StringComparison.OrdinalIgnoreCase) && p.Language == "fsharp");
        Assert.All(body.Projects, p => Assert.Equal("fsharp", p.Language));
    }

    [Fact]
    public async Task workspace_open_mixed_solution_lists_csharp_vb_and_fsharp_languages()
    {
        Assert.True(File.Exists(MixedWithFsSlnx), $"Missing fixture: {MixedWithFsSlnx}");
        var root = Path.GetDirectoryName(MixedWithFsSlnx)!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            new MsBuildSolutionLoader(TrustedRoots.Create([Directory.GetCurrentDirectory()])));

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = MixedWithFsSlnx });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await PollReadyAsync(fx, TimeSpan.FromSeconds(90));
        Assert.Equal("ready", status.Phase);

        var list = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(list.IsError is not true, InProcessMcpFixture.TextOf(list));
        var body = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);

        Assert.Contains(body.Projects, p =>
            p.Name.Contains("CsLib", StringComparison.OrdinalIgnoreCase) && p.Language == "csharp");
        Assert.Contains(body.Projects, p =>
            p.Name.Contains("VbLib", StringComparison.OrdinalIgnoreCase) && p.Language == "vb");
        Assert.Contains(body.Projects, p =>
            p.Name.Contains("FsLib", StringComparison.OrdinalIgnoreCase) && p.Language == "fsharp");
    }
    [Fact]
    public async Task workspace_open_mixed_solution_resolves_fsharp_widget()
    {
        Assert.True(File.Exists(MixedWithFsSlnx), $"Missing fixture: {MixedWithFsSlnx}");
        var root = Path.GetDirectoryName(MixedWithFsSlnx)!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            new MsBuildSolutionLoader(TrustedRoots.Create([Directory.GetCurrentDirectory()])));

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = MixedWithFsSlnx });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await PollReadyAsync(fx, TimeSpan.FromSeconds(90));
        Assert.Equal("ready", status.Phase);

        var resolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = "FsLib.Widget" });
        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
        var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);
        Assert.StartsWith("fsharp:", body.Handle, StringComparison.Ordinal);
        Assert.Equal("Widget", body.Summary.DisplayName);
        Assert.Equal("fsharp", body.Summary.Language);
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
