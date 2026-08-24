using DotNetMcp.Server;

namespace DotNetMcp.Bench;

internal static class Suites
{
    public static async Task RunFixturesAsync(ScenarioRunner runner)
    {
        await using var host = new McpBenchHost([FixturePaths.Root]);
        await RunSampleFilterAsync(runner, host).ConfigureAwait(false);
        await RunMixedAsync(runner, host).ConfigureAwait(false);
        await RunAvaloniaAsync(runner, host).ConfigureAwait(false);
    }

    public static async Task RunSyntheticAsync(ScenarioRunner runner, BenchOptions options, string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "dotnet-mcp-bench", $"synth-{options.SyntheticProjects}x{options.SyntheticFiles}");
        var slnx = SyntheticWorkspace.Create(root, options.SyntheticProjects, options.SyntheticFiles);
        await using var host = new McpBenchHost([root]);
        var workspace = await runner.OpenWorkspaceAsync(host, "Synthetic", slnx).ConfigureAwait(false);
        if (workspace is null)
        {
            return;
        }

        await MeasureSymbolSurfaceAsync(
            runner,
            workspace,
            symbolName: "P000.IShared",
            projectFragment: "P000",
            language: "csharp",
            includeRename: false).ConfigureAwait(false);

        var first = workspace.FindProjectId("P000");
        if (first is not null)
        {
            await runner.MeasureToolAsync(
                workspace,
                "Synthetic.project.diagnostics.single",
                "project_diagnostics",
                "project",
                BudgetClass.SingleProject,
                new Dictionary<string, object?> { ["projectId"] = first }).ConfigureAwait(false);
        }

        await runner.MeasureToolAsync(
            workspace,
            "Synthetic.project.diagnostics.batch",
            "project_diagnostics",
            "project",
            BudgetClass.BatchDiagnostics).ConfigureAwait(false);
    }

    public static async Task RunScaleAsync(ScenarioRunner runner, BenchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SolutionPath) || !File.Exists(options.SolutionPath))
        {
            throw new ArgumentException(
                "Scale suite requires --solution or DOTNET_MCP_BENCH_SOLUTION pointing at an existing workspace.");
        }

        var path = Path.GetFullPath(options.SolutionPath);
        var root = Path.GetDirectoryName(path)!;
        await using var host = new McpBenchHost([root]);
        var workspace = await runner.OpenWorkspaceAsync(host, "Scale", path).ConfigureAwait(false);
        if (workspace is null)
        {
            return;
        }

        await runner.MeasureToolAsync(
            workspace,
            "Scale.workspace.list_projects",
            "workspace_list_projects",
            "workspace",
            BudgetClass.SingleProject).ConfigureAwait(false);

        await runner.MeasureToolAsync(
            workspace,
            "Scale.workspace.check_drift",
            "workspace_check_drift",
            "workspace",
            BudgetClass.SingleProject).ConfigureAwait(false);

        var symbol = options.SymbolName;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            symbol = await GuessSymbolAsync(workspace).ConfigureAwait(false);
        }

        var projectFragment = string.IsNullOrWhiteSpace(symbol)
            ? null
            : await FindProjectForSymbolAsync(workspace, symbol).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(symbol))
        {
            await MeasureSymbolSurfaceAsync(
                runner,
                workspace,
                symbol,
                projectFragment,
                language: "csharp",
                includeRename: false).ConfigureAwait(false);
        }

        var firstProject = workspace.Projects
            .OrderBy(p => ProjectPreference(p.Name))
            .FirstOrDefault()?.ProjectId;
        if (firstProject is not null)
        {
            await runner.MeasureToolAsync(
                workspace,
                "Scale.project.diagnostics.single",
                "project_diagnostics",
                "project",
                BudgetClass.SingleProject,
                new Dictionary<string, object?> { ["projectId"] = firstProject }).ConfigureAwait(false);
        }

        await runner.MeasureToolAsync(
            workspace,
            "Scale.project.diagnostics.batch",
            "project_diagnostics",
            "project",
            BudgetClass.BatchDiagnostics).ConfigureAwait(false);
    }

    public static Task RunSmokeAsync(ScenarioRunner runner, BenchOptions options)
    {
        var smoke = new BenchOptions
        {
            Suite = "smoke",
            Iterations = 1,
            Warmup = 0,
            Filter = options.Filter,
            OutDir = options.OutDir,
            Cold = options.Cold,
            AllowWrites = false,
            NoGates = options.NoGates,
            JsonOnly = options.JsonOnly,
            SyntheticProjects = 2,
            SyntheticFiles = 2,
            ReadyTimeout = options.ReadyTimeout,
        };

        var root = Path.Combine(Path.GetTempPath(), "dotnet-mcp-bench", "smoke");
        return RunSyntheticAsync(runner, smoke, root);
    }

    private static async Task RunSampleFilterAsync(ScenarioRunner runner, McpBenchHost host)
    {
        var workspace = await runner.OpenWorkspaceAsync(host, "SampleFilter", FixturePaths.SampleSlnx)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return;
        }

        await runner.MeasureToolAsync(
            workspace,
            "SampleFilter.workspace.list_projects",
            "workspace_list_projects",
            "workspace",
            BudgetClass.SingleProject).ConfigureAwait(false);

        await runner.MeasureToolAsync(
            workspace,
            "SampleFilter.workspace.check_drift",
            "workspace_check_drift",
            "workspace",
            BudgetClass.SingleProject).ConfigureAwait(false);

        await MeasureSymbolSurfaceAsync(
            runner,
            workspace,
            symbolName: "LibA.Marker",
            projectFragment: "LibA",
            language: "csharp",
            includeRename: true).ConfigureAwait(false);

        var libA = workspace.FindProjectId("LibA", "csharp");
        if (libA is not null)
        {
            await MeasureProjectSurfaceAsync(runner, workspace, "SampleFilter", libA).ConfigureAwait(false);
        }

        await runner.MeasureToolAsync(
            workspace,
            "SampleFilter.project.diagnostics.batch",
            "project_diagnostics",
            "project",
            BudgetClass.BatchDiagnostics).ConfigureAwait(false);
    }

    private static async Task RunMixedAsync(ScenarioRunner runner, McpBenchHost host)
    {
        var workspace = await runner.OpenWorkspaceAsync(host, "MixedWithFs", FixturePaths.MixedWithFsSlnx)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return;
        }

        await MeasureSymbolSurfaceAsync(
            runner,
            workspace,
            symbolName: "CsLib.Caller",
            projectFragment: "CsLib",
            language: "csharp",
            includeRename: false).ConfigureAwait(false);

        await MeasureSymbolSurfaceAsync(
            runner,
            workspace,
            symbolName: "VbLib.Widget",
            projectFragment: "VbLib",
            language: "vb",
            includeRename: false,
            idPrefix: "MixedWithFs.vb").ConfigureAwait(false);

        await MeasureSymbolSurfaceAsync(
            runner,
            workspace,
            symbolName: "FsLib.Widget",
            projectFragment: "FsLib",
            language: "fsharp",
            includeRename: false,
            idPrefix: "MixedWithFs.fsharp").ConfigureAwait(false);

        foreach (var language in new[] { "vb", "fsharp" })
        {
            var projectId = workspace.Projects.FirstOrDefault(p => p.Language == language)?.ProjectId;
            if (projectId is null)
            {
                continue;
            }

            await runner.MeasureToolAsync(
                workspace,
                $"MixedWithFs.{language}.project.diagnostics.single",
                "project_diagnostics",
                "project",
                BudgetClass.SingleProject,
                new Dictionary<string, object?> { ["projectId"] = projectId }).ConfigureAwait(false);
        }
    }

    private static async Task RunAvaloniaAsync(ScenarioRunner runner, McpBenchHost _)
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet-mcp-bench", "xaml");
        var project = SyntheticWorkspace.CreateXamlApp(root);
        await using var host = new McpBenchHost([root]);
        var workspace = await runner.OpenWorkspaceAsync(host, "XamlApp", project)
            .ConfigureAwait(false);
        if (workspace is null)
        {
            return;
        }

        var axaml = Path.Combine(root, "MainWindow.axaml");
        await runner.MeasureToolAsync(
            workspace,
            "XamlApp.xaml.resolve_class",
            "xaml_resolve_class",
            "xaml",
            BudgetClass.SingleProject,
            new Dictionary<string, object?> { ["path"] = axaml },
            required: false).ConfigureAwait(false);

        await runner.MeasureToolAsync(
            workspace,
            "XamlApp.xaml.list_xmlns",
            "xaml_list_xmlns",
            "xaml",
            BudgetClass.SingleProject,
            new Dictionary<string, object?> { ["path"] = axaml },
            required: false).ConfigureAwait(false);

        await runner.MeasureToolAsync(
            workspace,
            "XamlApp.xaml.resolve_name",
            "xaml_resolve_name",
            "xaml",
            BudgetClass.SingleProject,
            new Dictionary<string, object?> { ["path"] = axaml, ["name"] = "TitleText" },
            required: false).ConfigureAwait(false);

        await runner.MeasureToolAsync(
            workspace,
            "XamlApp.xaml.resolve_binding",
            "xaml_resolve_binding",
            "xaml",
            BudgetClass.SingleProject,
            new Dictionary<string, object?> { ["path"] = axaml, ["bindingPath"] = "Name" },
            required: false).ConfigureAwait(false);

        await runner.MeasureToolAsync(
            workspace,
            "XamlApp.xaml.diagnostics",
            "xaml_diagnostics",
            "xaml",
            BudgetClass.SingleProject,
            new Dictionary<string, object?> { ["path"] = axaml },
            required: false).ConfigureAwait(false);
    }

    private static async Task MeasureSymbolSurfaceAsync(
        ScenarioRunner runner,
        WorkspaceCase workspace,
        string symbolName,
        string? projectFragment,
        string? language,
        bool includeRename,
        string? idPrefix = null,
        bool required = true)
    {
        var prefix = idPrefix ?? workspace.Name;
        var projectId = projectFragment is null ? null : workspace.FindProjectId(projectFragment, language);

        await runner.MeasureToolAsync(
            workspace,
            $"{prefix}.symbol.resolve.warm",
            "symbol_resolve",
            "symbol",
            BudgetClass.SingleProject,
            new Dictionary<string, object?> { ["name"] = symbolName, ["projectId"] = projectId },
            required).ConfigureAwait(false);

        var handle = await workspace.ResolveHandleAsync(symbolName, projectId).ConfigureAwait(false);
        if (handle is null)
        {
            return;
        }

        var handleArgs = new Dictionary<string, object?> { ["handle"] = handle };
        await runner.MeasureToolAsync(workspace, $"{prefix}.symbol.summary", "symbol_summary", "symbol", BudgetClass.SingleProject, handleArgs).ConfigureAwait(false);
        await runner.MeasureToolAsync(workspace, $"{prefix}.symbol.goto_definition", "symbol_goto_definition", "symbol", BudgetClass.SingleProject, handleArgs).ConfigureAwait(false);
        await runner.MeasureToolAsync(workspace, $"{prefix}.symbol.members", "symbol_members", "symbol", BudgetClass.SingleProject, handleArgs).ConfigureAwait(false);
        await runner.MeasureToolAsync(workspace, $"{prefix}.symbol.attribution", "symbol_attribution", "symbol", BudgetClass.SingleProject, handleArgs, required: language is null or "csharp" or "vb").ConfigureAwait(false);
        await runner.MeasureToolAsync(
            workspace,
            $"{prefix}.symbol.find_references.scoped",
            "symbol_find_references",
            "symbol",
            BudgetClass.FindRefsScoped,
            new Dictionary<string, object?> { ["handle"] = handle, ["entireSolution"] = false }).ConfigureAwait(false);
        await runner.MeasureToolAsync(
            workspace,
            $"{prefix}.symbol.find_references.entire",
            "symbol_find_references",
            "symbol",
            BudgetClass.FindRefsEntire,
            new Dictionary<string, object?> { ["handle"] = handle, ["entireSolution"] = true }).ConfigureAwait(false);
        var membersResult = await workspace.Host.CallAsync("symbol_members", handleArgs).ConfigureAwait(false);
        string? methodHandle = null;
        if (membersResult.IsError is not true)
        {
            var members = McpBenchHost.Deserialize<SymbolMembersResultDto>(membersResult);
            methodHandle = members.Items.FirstOrDefault(i =>
                    i.Summary.Kind is "Method" or "Function" or "Property")
                ?.Handle;
        }

        if (methodHandle is not null)
        {
            await runner.MeasureToolAsync(
                workspace,
                $"{prefix}.symbol.find_callers",
                "symbol_find_callers",
                "symbol",
                BudgetClass.FindRefsScoped,
                new Dictionary<string, object?> { ["handle"] = methodHandle }).ConfigureAwait(false);
        }
        await runner.MeasureToolAsync(workspace, $"{prefix}.symbol.find_implementations", "symbol_find_implementations", "symbol", BudgetClass.SingleProject, handleArgs).ConfigureAwait(false);
        await runner.MeasureToolAsync(workspace, $"{prefix}.symbol.type_hierarchy", "symbol_type_hierarchy", "symbol", BudgetClass.SingleProject, handleArgs).ConfigureAwait(false);

        await runner.MeasureParallelAsync(
            workspace,
            $"{prefix}.symbol.summary.parallel4",
            "symbol_summary",
            "symbol",
            BudgetClass.SingleProject,
            _ => handleArgs,
            parallelism: 4).ConfigureAwait(false);

        if (includeRename)
        {
            await runner.MeasureToolAsync(
                workspace,
                $"{prefix}.symbol.preview_rename",
                "symbol_preview_rename",
                "edit",
                BudgetClass.SingleProject,
                new Dictionary<string, object?> { ["handle"] = handle, ["newName"] = "MarkerRenamed" },
                required: false).ConfigureAwait(false);

            await runner.MeasureToolAsync(
                workspace,
                $"{prefix}.symbol.list_refactorings",
                "symbol_list_refactorings",
                "edit",
                BudgetClass.SingleProject,
                handleArgs,
                required: false).ConfigureAwait(false);
        }
    }

    private static async Task MeasureProjectSurfaceAsync(
        ScenarioRunner runner,
        WorkspaceCase workspace,
        string prefix,
        string projectId)
    {
        var args = new Dictionary<string, object?> { ["projectId"] = projectId };
        await runner.MeasureToolAsync(workspace, $"{prefix}.project.diagnostics.single", "project_diagnostics", "project", BudgetClass.SingleProject, args).ConfigureAwait(false);
        await runner.MeasureToolAsync(workspace, $"{prefix}.project.list_generators", "project_list_generators", "project", BudgetClass.SingleProject, args).ConfigureAwait(false);
        await runner.MeasureToolAsync(workspace, $"{prefix}.project.list_generated_sources", "project_list_generated_sources", "project", BudgetClass.SingleProject, args).ConfigureAwait(false);
        await runner.MeasureToolAsync(workspace, $"{prefix}.project.list_generator_diagnostics", "project_list_generator_diagnostics", "project", BudgetClass.SingleProject, args).ConfigureAwait(false);
        await runner.MeasureToolAsync(workspace, $"{prefix}.project.list_dynamic_invocations", "project_list_dynamic_invocations", "project", BudgetClass.SingleProject, args).ConfigureAwait(false);

        var diagnostics = await workspace.Host.CallAsync("project_diagnostics", args).ConfigureAwait(false);
        if (diagnostics.IsError is true)
        {
            return;
        }

        var body = McpBenchHost.Deserialize<ProjectDiagnosticsResultDto>(diagnostics);
        var first = body.Items.FirstOrDefault();
        if (first is null)
        {
            return;
        }

        await runner.MeasureToolAsync(
            workspace,
            $"{prefix}.diagnostics.list_fixes",
            "diagnostics_list_fixes",
            "edit",
            BudgetClass.SingleProject,
            new Dictionary<string, object?>
            {
                ["projectId"] = projectId,
                ["diagnosticId"] = first.Id,
                ["filePath"] = first.FilePath,
                ["startLine"] = first.StartLine,
                ["startCharacter"] = first.StartCharacter,
            },
            required: false).ConfigureAwait(false);
    }

    private static async Task<string?> FindProjectForSymbolAsync(WorkspaceCase workspace, string symbol)
    {
        foreach (var project in workspace.Projects.OrderBy(p => ProjectPreference(p.Name)))
        {
            var handle = await workspace.ResolveHandleAsync(symbol, project.ProjectId).ConfigureAwait(false);
            if (handle is not null)
            {
                return project.Name;
            }
        }

        return workspace.Projects.OrderBy(p => ProjectPreference(p.Name)).FirstOrDefault()?.Name;
    }

    private static int ProjectPreference(string name)
    {
        if (name.Contains("Test", StringComparison.OrdinalIgnoreCase)) return 100;
        if (name.Contains("Bench", StringComparison.OrdinalIgnoreCase)) return 90;
        if (name.Contains("Dummy", StringComparison.OrdinalIgnoreCase)) return 80;
        if (name.Contains("Aot", StringComparison.OrdinalIgnoreCase)) return 70;
        return 0;
    }

    private static async Task<string?> GuessSymbolAsync(WorkspaceCase workspace)
    {
        foreach (var project in workspace.Projects.Where(p => p.Language == "csharp").Take(8))
        {
            var members = await workspace.Host.CallAsync(
                "workspace_list_projects").ConfigureAwait(false);
            _ = members;
            var name = project.Name.Split('(')[0].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var resolved = await workspace.ResolveHandleAsync(name, project.ProjectId).ConfigureAwait(false);
            if (resolved is not null)
            {
                return name;
            }
        }

        return null;
    }
}







