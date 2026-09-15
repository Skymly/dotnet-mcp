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
    public static string AvaloniaProject => Path.Combine(FixturesRoot, "AvaloniaApp", "AvaloniaApp.csproj");
    public static string AvaloniaMainWindow => Path.Combine(FixturesRoot, "AvaloniaApp", "MainWindow.axaml");


    [Fact]
    public async Task workspace_open_slnx_status_ready_lists_sample_projects()
    {
        Assert.True(File.Exists(SampleSlnx), $"Missing fixture: {SampleSlnx}");
        var root = Path.GetDirectoryName(SampleSlnx)!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            solutionLoader: null);

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = SampleSlnx });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
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
            solutionLoader: null);

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = SampleSlnf });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
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
            solutionLoader: null);

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = MultiTfmProject });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
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
            solutionLoader: null);

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = VbProject });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
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
            solutionLoader: null);

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = MixedSlnx });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
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
            solutionLoader: null);

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = FsProject });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
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
            solutionLoader: null);

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = MixedWithFsSlnx });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
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
            solutionLoader: null);

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = MixedWithFsSlnx });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
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

    [Fact]
    public async Task workspace_open_fsproj_find_callers_returns_enclosing_member_not_callee()
    {
        Assert.True(File.Exists(MixedWithFsSlnx), $"Missing fixture: {MixedWithFsSlnx}");
        var root = Path.GetDirectoryName(MixedWithFsSlnx)!;

        await using var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            solutionLoader: null);

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = MixedWithFsSlnx });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));

        var status = await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
        Assert.Equal("ready", status.Phase);

        var pingResolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = "FsLib.Widget.ping" });
        Assert.True(pingResolved.IsError is not true, InProcessMcpFixture.TextOf(pingResolved));
        var ping = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(pingResolved);

        var callerResolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = "FsLib.Widget.pingCaller" });
        Assert.True(callerResolved.IsError is not true, InProcessMcpFixture.TextOf(callerResolved));
        var caller = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(callerResolved);

        var callers = await fx.Client.CallToolAsync(
            "symbol_find_callers",
            new Dictionary<string, object?> { ["handle"] = ping.Handle });
        Assert.True(callers.IsError is not true, InProcessMcpFixture.TextOf(callers));
        var body = InProcessMcpFixture.Deserialize<SymbolFindCallersResultDto>(callers);

        Assert.Contains(body.Items, item => item.CallerHandle == caller.Handle);
        Assert.DoesNotContain(body.Items, item => item.CallerHandle == ping.Handle);
        Assert.Contains(body.Items, item =>
            string.Equals(item.CallerSummary.DisplayName, "pingCaller", StringComparison.Ordinal));
    }

    [Fact]
    public async Task workspace_open_mixed_solution_resolves_vb_widget()
    {
        Assert.True(File.Exists(MixedWithFsSlnx), $"Missing fixture: {MixedWithFsSlnx}");
        var root = Path.GetDirectoryName(MixedWithFsSlnx)!;

        await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = MixedWithFsSlnx });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
        var status = await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
        Assert.Equal("ready", status.Phase);

        var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
        var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list);
        var vb = Assert.Single(projects.Projects, p => p.Language == "vb");

        var resolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = "Widget", ["projectId"] = vb.ProjectId });
        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
        var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);
        Assert.StartsWith("vb:", body.Handle, StringComparison.Ordinal);
        Assert.Equal("vb", body.Summary.Language);

        var gotoDef = await fx.Client.CallToolAsync(
            "symbol_goto_definition",
            new Dictionary<string, object?> { ["handle"] = body.Handle });
        Assert.True(gotoDef.IsError is not true, InProcessMcpFixture.TextOf(gotoDef));
    }

    [Fact]
    public async Task workspace_open_mixed_solution_fsharp_attribution_is_handwritten()
    {
        Assert.True(File.Exists(MixedWithFsSlnx), $"Missing fixture: {MixedWithFsSlnx}");
        var root = Path.GetDirectoryName(MixedWithFsSlnx)!;

        await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = MixedWithFsSlnx });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
        await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);

        var resolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = "FsLib.Widget" });
        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
        var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

        var attr = await fx.Client.CallToolAsync(
            "symbol_attribution",
            new Dictionary<string, object?> { ["handle"] = handle });
        Assert.True(attr.IsError is not true, InProcessMcpFixture.TextOf(attr));
        var body = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attr);
        Assert.Equal("Handwritten", body.OriginKind);
        Assert.Equal("InSource", body.DeclarationAvailability);
    }

    [Fact]
    public async Task workspace_open_avalonia_resolves_xaml_class()
    {
        Assert.True(File.Exists(AvaloniaProject), $"Missing fixture: {AvaloniaProject}");
        Assert.True(File.Exists(AvaloniaMainWindow), $"Missing fixture: {AvaloniaMainWindow}");
        var root = Path.GetDirectoryName(AvaloniaProject)!;

        await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));

        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = AvaloniaProject });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
        await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);

        var resolved = await fx.Client.CallToolAsync(
            "xaml_resolve_class",
            new Dictionary<string, object?> { ["path"] = AvaloniaMainWindow });
        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
        var body = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);
        Assert.Equal("MainWindow", body.Summary.DisplayName);
        Assert.Contains("SampleApp", body.Handle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task workspace_open_msbuild_analyzer_project_attributes_custom_marker()
    {
        var root = CreateTempDir("genhost");
        var dllSrc = Path.Combine(AppContext.BaseDirectory, "CustomGenerator.dll");
        Assert.True(File.Exists(dllSrc), $"Missing CustomGenerator.dll next to tests: {dllSrc}");
        File.Copy(dllSrc, Path.Combine(root, "CustomGenerator.dll"));

        var project = Path.Combine(root, "Host.csproj");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Analyzer Include="CustomGenerator.dll" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "Placeholder.cs"), """
            namespace Host;
            public static class Placeholder
            {
            }
            """);

        try
        {
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = project });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleApp.Generated.CustomMarker" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var attr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(attr.IsError is not true, InProcessMcpFixture.TextOf(attr));
            var body = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attr);
            Assert.Equal("SourceGenerator", body.OriginKind);
            Assert.NotNull(body.Generator);
            Assert.Equal("CustomGenerator.MarkerGenerator", body.Generator!.TypeFullName);
        }
        finally
        {
            TryDelete(root);
        }
    }


    [Fact]
    public async Task workspace_open_fsproj_rename_rejects_illegal_identifiers()
    {
        var source = Path.Combine(FixturesRoot, "MixedCsharpVb", "FsLib");
        Assert.True(Directory.Exists(source), $"Missing fixture: {source}");
        var root = CreateTempDir("fs-ident");
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        }

        var project = Path.Combine(root, "FsLib.fsproj");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                solutionLoader: null);

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = project });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);

            var pingResolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "FsLib.Widget.ping" });
            Assert.True(pingResolved.IsError is not true, InProcessMcpFixture.TextOf(pingResolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(pingResolved).Handle;

            foreach (var bad in new[] { "1bad", "a-b", "Foo.Bar", "let", " " })
            {
                var preview = await fx.Client.CallToolAsync(
                    "symbol_preview_rename",
                    new Dictionary<string, object?>
                    {
                        ["handle"] = handle,
                        ["newName"] = bad
                    });
                Assert.True(preview.IsError is true, "accepted " + bad + " " + InProcessMcpFixture.TextOf(preview));
                Assert.Equal(
                    PolicyErrorCodes.InvalidRenameName,
                    InProcessMcpFixture.Deserialize<PolicyErrorDto>(preview).Error);
            }

            var ok = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["newName"] = "pong"
                });
            Assert.True(ok.IsError is not true, InProcessMcpFixture.TextOf(ok));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_open_fsproj_overload_members_have_distinct_handles()
    {
        var source = Path.Combine(FixturesRoot, "MixedCsharpVb", "FsLib");
        Assert.True(Directory.Exists(source), $"Missing fixture: {source}");
        var root = CreateTempDir("fs-overloads");
        File.Copy(Path.Combine(source, "FsLib.fsproj"), Path.Combine(root, "FsLib.fsproj"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "Widget.fs"),
            """
            module FsLib.Widget

            type Gadget() =
                member _.Ping(x: int) = 1
                member _.Ping(x: string) = 0
            """);
        var project = Path.Combine(root, "FsLib.fsproj");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                solutionLoader: null);

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = project });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "Gadget" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var gadget = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);

            var attribution = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?> { ["handle"] = gadget.Handle });
            Assert.True(attribution.IsError is not true, InProcessMcpFixture.TextOf(attribution));

            var members = await fx.Client.CallToolAsync(
                "symbol_members",
                new Dictionary<string, object?> { ["handle"] = gadget.Handle, ["limit"] = 50 });
            Assert.True(members.IsError is not true, InProcessMcpFixture.TextOf(members));
            var page = InProcessMcpFixture.Deserialize<SymbolMembersResultDto>(members);
            var pings = page.Items.Where(m => m.Summary.DisplayName == "Ping").ToList();
            Assert.True(pings.Count >= 2, "items=" + string.Join(",", page.Items.Select(i => i.Summary.DisplayName + ":" + i.Handle)));
            Assert.Equal(pings.Count, pings.Select(p => p.Handle).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_open_fsproj_deleted_file_is_not_queryable_after_reopen()
    {
        var source = Path.Combine(FixturesRoot, "MixedCsharpVb", "FsLib");
        Assert.True(Directory.Exists(source), $"Missing fixture: {source}");
        var root = CreateTempDir("fs-stale-text");
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        }

        var extra = Path.Combine(root, "Ghost.fs");
        await File.WriteAllTextAsync(extra, "module FsLib.Ghost\n\nlet hidden () = 1\n");
        var project = Path.Combine(root, "FsLib.fsproj");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                solutionLoader: null);

            async Task OpenReadyAsync()
            {
                var open = await fx.Client.CallToolAsync(
                    "workspace_open",
                    new Dictionary<string, object?> { ["path"] = project });
                Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
                await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
            }

            await OpenReadyAsync();
            var found = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "FsLib.Ghost.hidden" });
            Assert.True(found.IsError is not true, InProcessMcpFixture.TextOf(found));

            File.Delete(extra);
            await OpenReadyAsync();
            var gone = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "FsLib.Ghost.hidden" });
            Assert.True(gone.IsError is true, InProcessMcpFixture.TextOf(gone));
            Assert.Equal(
                PolicyErrorCodes.SymbolNotFound,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(gone).Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task workspace_open_fsproj_preview_apply_rename_writes_disk_and_advances_epoch()
    {
        var source = Path.Combine(FixturesRoot, "MixedCsharpVb", "FsLib");
        Assert.True(Directory.Exists(source), $"Missing fixture: {source}");
        var root = CreateTempDir("fs-rename");
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        }

        var project = Path.Combine(root, "FsLib.fsproj");
        var widget = Path.Combine(root, "Widget.fs");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                solutionLoader: null);

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = project });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);

            var epoch = fx.WorkspaceHost.CurrentEpoch;
            var pingResolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "FsLib.Widget.ping" });
            Assert.True(pingResolved.IsError is not true, InProcessMcpFixture.TextOf(pingResolved));
            var ping = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(pingResolved);

            var before = await File.ReadAllTextAsync(widget);
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = ping.Handle,
                    ["newName"] = "pong"
                });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var previewBody = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);
            Assert.Contains(previewBody.Documents, d =>
                d.Path.EndsWith("Widget.fs", StringComparison.OrdinalIgnoreCase) &&
                d.NewText.Contains("pong", StringComparison.Ordinal));
            Assert.Equal(before, await File.ReadAllTextAsync(widget));

            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewBody.PreviewId });
            Assert.True(apply.IsError is not true, InProcessMcpFixture.TextOf(apply));
            var applyBody = InProcessMcpFixture.Deserialize<SymbolApplyRenameResultDto>(apply);
            Assert.True(applyBody.Epoch > epoch, $"epoch {epoch} -> {applyBody.Epoch}");
            Assert.Contains("pong", await File.ReadAllTextAsync(widget), StringComparison.Ordinal);
            Assert.DoesNotContain("let ping ", await File.ReadAllTextAsync(widget), StringComparison.Ordinal);
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
