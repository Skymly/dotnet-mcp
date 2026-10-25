using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

/// <summary>
/// Unified position model (#518): every located output uses 1-based lines and
/// 1-based columns counted in UTF-16 code units, a tab is one column, the end
/// column is exclusive, and locations without a source position omit the
/// fields. A position from one tool feeds the next verbatim.
/// </summary>
public class UnifiedPositionSeamTests
{
    [Fact]
    public async Task csharp_symbol_locations_satisfy_the_unified_invariant()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithCallers());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));

            var handle = await ResolveHandleAsync(fx, "SampleLib.MathOps.Add");

            var definition = await CallAsync<SymbolDefinitionResultDto>(
                fx, "symbol_goto_definition", new() { ["handle"] = handle });
            Assert.NotEmpty(definition.Locations);
            foreach (var loc in definition.Locations)
            {
                await AssertInvariantAsync(
                    session!, loc.FilePath, loc.Start, loc.Length, loc.Line, loc.Column, loc.EndLine, loc.EndColumn);
            }

            var references = await CallAsync<SymbolFindReferencesResultDto>(
                fx, "symbol_find_references", new() { ["handle"] = handle });
            Assert.NotEmpty(references.Items);
            foreach (var item in references.Items)
            {
                await AssertInvariantAsync(
                    session!, item.FilePath, item.Start, item.Length, item.Line, item.Column, item.EndLine, item.EndColumn);
            }

            var callers = await CallAsync<SymbolFindCallersResultDto>(
                fx, "symbol_find_callers", new() { ["handle"] = handle });
            Assert.NotEmpty(callers.Items);
            foreach (var item in callers.Items)
            {
                await AssertInvariantAsync(
                    session!, item.FilePath, item.Start, item.Length, item.Line, item.Column, item.EndLine, item.EndColumn);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task csharp_implementation_locations_satisfy_the_unified_invariant()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithHierarchy());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));

            var handle = await ResolveHandleAsync(fx, "SampleLib.IDrawable");
            var implementations = await CallAsync<SymbolFindImplementationsResultDto>(
                fx, "symbol_find_implementations", new() { ["handle"] = handle });

            var locations = implementations.Items.SelectMany(i => i.Locations).ToArray();
            Assert.NotEmpty(locations);
            foreach (var loc in locations)
            {
                await AssertInvariantAsync(
                    session!, loc.FilePath, loc.Start, loc.Length, loc.Line, loc.Column, loc.EndLine, loc.EndColumn);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task vb_symbol_locations_satisfy_the_unified_invariant()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithVbSymbols(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));

            var widget = await ResolveHandleAsync(fx, "VbLib.Widget");
            var definition = await CallAsync<SymbolDefinitionResultDto>(
                fx, "symbol_goto_definition", new() { ["handle"] = widget });
            Assert.NotEmpty(definition.Locations);
            foreach (var loc in definition.Locations)
            {
                await AssertInvariantAsync(
                    session!, loc.FilePath, loc.Start, loc.Length, loc.Line, loc.Column, loc.EndLine, loc.EndColumn);
            }

            var ping = await MemberHandleAsync(fx, widget, "Ping");
            var references = await CallAsync<SymbolFindReferencesResultDto>(
                fx, "symbol_find_references", new() { ["handle"] = ping });
            Assert.NotEmpty(references.Items);
            foreach (var item in references.Items)
            {
                await AssertInvariantAsync(
                    session!, item.FilePath, item.Start, item.Length, item.Line, item.Column, item.EndLine, item.EndColumn);
            }

            var callers = await CallAsync<SymbolFindCallersResultDto>(
                fx, "symbol_find_callers", new() { ["handle"] = ping });
            Assert.NotEmpty(callers.Items);
            foreach (var item in callers.Items)
            {
                await AssertInvariantAsync(
                    session!, item.FilePath, item.Start, item.Length, item.Line, item.Column, item.EndLine, item.EndColumn);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_symbol_locations_satisfy_the_unified_invariant()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "Mixed.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithFsharpSymbols(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));

            var widget = await ResolveHandleAsync(fx, "FsLib.Widget");
            var definition = await CallAsync<SymbolDefinitionResultDto>(
                fx, "symbol_goto_definition", new() { ["handle"] = widget });
            Assert.NotEmpty(definition.Locations);
            foreach (var loc in definition.Locations)
            {
                await AssertInvariantAsync(
                    session!, loc.FilePath, loc.Start, loc.Length, loc.Line, loc.Column, loc.EndLine, loc.EndColumn);
            }

            var ping = await MemberHandleAsync(fx, widget, "ping");
            var references = await CallAsync<SymbolFindReferencesResultDto>(
                fx, "symbol_find_references", new() { ["handle"] = ping });
            Assert.NotEmpty(references.Items);
            foreach (var item in references.Items)
            {
                await AssertInvariantAsync(
                    session!, item.FilePath, item.Start, item.Length, item.Line, item.Column, item.EndLine, item.EndColumn);
            }

            var callers = await CallAsync<SymbolFindCallersResultDto>(
                fx, "symbol_find_callers", new() { ["handle"] = ping });
            Assert.NotEmpty(callers.Items);
            foreach (var item in callers.Items)
            {
                await AssertInvariantAsync(
                    session!, item.FilePath, item.Start, item.Length, item.Line, item.Column, item.EndLine, item.EndColumn);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task dynamic_invocation_locations_satisfy_the_unified_invariant()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithDynamic());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));

            var project = Assert.Single(
                InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
                    await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>())).Projects,
                p => p.Language == "csharp");

            var body = await CallAsync<ProjectListDynamicInvocationsResultDto>(
                fx,
                "project_list_dynamic_invocations",
                new() { ["projectId"] = project.ProjectId });
            Assert.NotEmpty(body.Items);
            foreach (var item in body.Items)
            {
                await AssertInvariantAsync(
                    session!, item.FilePath, item.Start, item.Length, item.Line, item.Column, item.EndLine, item.EndColumn);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task tabs_count_as_one_column()
    {
        const string tabbed =
            "namespace TabLib;\n" +
            "\n" +
            "public static class Widget\n" +
            "{\n" +
            "\tpublic static int Bump(int x) => x + 1;\n" +
            "}\n";

        var loaded = CreateSingleDocumentLoaded("TabLib", "Tabbed.cs", tabbed);
        var service = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        using var session = new WorkspaceSession(loaded, epoch: 1);

        var (resolved, resolveError) = await service.ResolveByNameAsync(session, "TabLib.Widget.Bump");
        Assert.Null(resolveError);
        var (definition, definitionError) = await service.GetDefinitionAsync(session, resolved!.Handle);
        Assert.Null(definitionError);

        var loc = Assert.Single(definition!.Locations);
        Assert.Equal("InSource", loc.DeclarationAvailability);
        var bumpLine = tabbed.Split('\n').Single(l => l.Contains("Bump", StringComparison.Ordinal));
        Assert.Equal(bumpLine.IndexOf("Bump", StringComparison.Ordinal) + 1, loc.Column);
        Assert.Equal(OffsetOf(tabbed, loc.Line!.Value, loc.Column!.Value), loc.Start);
        Assert.Equal(
            OffsetOf(tabbed, loc.EndLine!.Value, loc.EndColumn!.Value),
            loc.Start + loc.Length);
    }

    [Fact]
    public async Task generated_document_locations_use_generated_text_coordinates()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithGenerators());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));

            var handle = await ResolveHandleAsync(fx, "SampleApp.Generated.CustomMarker");
            var definition = await CallAsync<SymbolDefinitionResultDto>(
                fx, "symbol_goto_definition", new() { ["handle"] = handle });

            var generated = Assert.Single(definition.Locations, l =>
                (l.FilePath ?? string.Empty).Contains(".g.cs", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("InSource", generated.DeclarationAvailability);
            Assert.StartsWith("SourceGenerator(", generated.Origin, StringComparison.Ordinal);
            await AssertInvariantAsync(
                session!,
                generated.FilePath,
                generated.Start,
                generated.Length,
                generated.Line,
                generated.Column,
                generated.EndLine,
                generated.EndColumn);

            var references = await CallAsync<SymbolFindReferencesResultDto>(
                fx, "symbol_find_references", new() { ["handle"] = handle });
            Assert.NotEmpty(references.Items);
            foreach (var item in references.Items)
            {
                await AssertInvariantAsync(
                    session!, item.FilePath, item.Start, item.Length, item.Line, item.Column, item.EndLine, item.EndColumn);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task csharp_diagnostic_positions_feed_fix_locators_verbatim()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithMissingUsingOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));

            var projectId = await ProjectIdOfAsync(fx, "csharp");
            var diagnostics = await CallAsync<ProjectDiagnosticsResultDto>(
                fx, "project_diagnostics", new() { ["projectId"] = projectId });

            var errors = diagnostics.Items
                .Where(i => i.Line is not null && i.Column is not null && i.EndColumn is not null)
                .ToArray();
            Assert.NotEmpty(errors);
            var previewed = false;
            foreach (var row in errors)
            {
                // Columns are 1-based: the character under (line, column) is the
                // first character of the underlined span.
                var text = await TextForAsync(session!, row.FilePath!);
                var offset = OffsetOf(text, row.Line!.Value, row.Column!.Value);
                var endOffset = OffsetOf(text, row.EndLine!.Value, row.EndColumn!.Value);
                Assert.True(endOffset > offset, "endColumn must be exclusive and past the start.");

                var fixes = await fx.Client.CallToolAsync(
                    "diagnostics_list_fixes",
                    DiagnosticFixSeamTests.Locator(row));
                Assert.True(fixes.IsError is not true, InProcessMcpFixture.TextOf(fixes));

                var listed = InProcessMcpFixture.Deserialize<DiagnosticsListFixesResultDto>(fixes);
                if (listed.Items.Count == 0)
                {
                    continue;
                }

                var previewArgs = DiagnosticFixSeamTests.Locator(row);
                previewArgs["fixIndex"] = listed.Items[0].FixIndex;
                var preview = await fx.Client.CallToolAsync("diagnostics_preview_fix", previewArgs);
                Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
                previewed = true;
            }

            Assert.True(previewed, "expected at least one row with fixes to round-trip through preview.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task vb_diagnostic_positions_feed_fix_locators_verbatim()
    {
        var root = CreateTempDir("root");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithVbMissingImportOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));

            var projectId = await ProjectIdOfAsync(fx, "vb");
            var diagnostics = await CallAsync<ProjectDiagnosticsResultDto>(
                fx, "project_diagnostics", new() { ["projectId"] = projectId });

            var errors = diagnostics.Items
                .Where(i => i.Line is not null && i.Column is not null && i.EndColumn is not null)
                .ToArray();
            Assert.NotEmpty(errors);
            foreach (var row in errors)
            {
                var text = await TextForAsync(session!, row.FilePath!);
                var offset = OffsetOf(text, row.Line!.Value, row.Column!.Value);
                var endOffset = OffsetOf(text, row.EndLine!.Value, row.EndColumn!.Value);
                Assert.True(endOffset > offset, "endColumn must be exclusive and past the start.");

                var fixes = await fx.Client.CallToolAsync(
                    "diagnostics_list_fixes",
                    DiagnosticFixSeamTests.Locator(row));
                Assert.True(fixes.IsError is not true, InProcessMcpFixture.TextOf(fixes));

                var listed = InProcessMcpFixture.Deserialize<DiagnosticsListFixesResultDto>(fixes);
                if (listed.Items.Count == 0)
                {
                    continue;
                }

                var previewArgs = DiagnosticFixSeamTests.Locator(row);
                previewArgs["fixIndex"] = listed.Items[0].FixIndex;
                var preview = await fx.Client.CallToolAsync("diagnostics_preview_fix", previewArgs);
                Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_diagnostics_use_the_same_1_based_columns()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithFsharpDiagnostics());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            Assert.True(fx.WorkspaceHost.TryGetReadySession(out var session));

            var projectId = await ProjectIdOfAsync(fx, "fsharp");
            var diagnostics = await CallAsync<ProjectDiagnosticsResultDto>(
                fx, "project_diagnostics", new() { ["projectId"] = projectId });

            var rows = diagnostics.Items
                .Where(i => i.Line is not null && i.Column is not null && i.EndColumn is not null)
                .ToArray();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                var text = await TextForAsync(session!, row.FilePath!);
                var offset = OffsetOf(text, row.Line!.Value, row.Column!.Value);
                var endOffset = OffsetOf(text, row.EndLine!.Value, row.EndColumn!.Value);
                Assert.True(endOffset > offset, "endColumn must be exclusive and past the start.");
                Assert.True(row.Column >= 1);
                // A 0-based column would land one character too far left; the
                // underlined literal starts exactly at column - 1.
                Assert.Equal('"', text[offset]);
                Assert.Equal('"', text[endOffset - 1]);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task inmetadata_locations_omit_all_position_fields()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithComInterop());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveHandleAsync(fx, "InteropLib.IMetaCom");
            var result = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));

            var loc = Assert.Single(
                InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(result).Locations);
            Assert.Equal("InMetadata", loc.DeclarationAvailability);
            Assert.Null(loc.FilePath);
            Assert.Null(loc.Start);
            Assert.Null(loc.Length);
            Assert.Null(loc.Line);
            Assert.Null(loc.Column);
            Assert.Null(loc.EndLine);
            Assert.Null(loc.EndColumn);

            // null position fields are omitted from the JSON payload entirely.
            var raw = InProcessMcpFixture.TextOf(result);
            Assert.DoesNotContain("\"line\"", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("\"column\"", raw, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<string> ProjectIdOfAsync(InProcessMcpFixture fx, string language)
    {
        var projects = await fx.Client.CallToolAsync(
            "workspace_list_projects",
            new Dictionary<string, object?>());
        Assert.True(projects.IsError is not true, InProcessMcpFixture.TextOf(projects));
        var list = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(projects);
        return Assert.Single(list.Projects, p => p.Language == language).ProjectId;
    }

    private static async Task<T> CallAsync<T>(
        InProcessMcpFixture fx, string tool, Dictionary<string, object?> args)
    {
        var result = await fx.Client.CallToolAsync(tool, args);
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<T>(result);
    }

    private static async Task<string> ResolveHandleAsync(InProcessMcpFixture fx, string name)
    {
        var resolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name });
        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
        return InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
    }

    private static async Task<string> MemberHandleAsync(InProcessMcpFixture fx, string typeHandle, string displayName)
    {
        var members = await fx.Client.CallToolAsync(
            "symbol_members",
            new Dictionary<string, object?> { ["handle"] = typeHandle });
        Assert.True(members.IsError is not true, InProcessMcpFixture.TextOf(members));
        var body = InProcessMcpFixture.Deserialize<SymbolMembersResultDto>(members);
        return Assert.Single(body.Items, i => i.Summary.DisplayName == displayName).Handle;
    }

    /// <summary>
    /// The invariant: OffsetOf(Line, Column) == Start and
    /// OffsetOf(EndLine, EndColumn) == Start + Length.
    /// </summary>
    private static async Task AssertInvariantAsync(
        IWorkspaceSession session,
        string? filePath,
        int? start,
        int? length,
        int? line,
        int? column,
        int? endLine,
        int? endColumn)
    {
        Assert.True(start is >= 0 && length is >= 0, $"missing offsets at {filePath}");
        Assert.True(
            line is >= 1 && column is >= 1 && endLine is >= 1 && endColumn is >= 1,
            $"missing 1-based positions at {filePath}");
        var text = await TextForAsync(session, filePath!).ConfigureAwait(false);
        Assert.Equal(start!.Value, OffsetOf(text, line!.Value, column!.Value));
        Assert.Equal(start.Value + length!.Value, OffsetOf(text, endLine!.Value, endColumn!.Value));
    }

    private static async Task<string> TextForAsync(IWorkspaceSession session, string path)
    {
        foreach (var project in session.Solution.Projects)
        {
            var document = project.Documents.FirstOrDefault(d => SamePath(d.FilePath, path));
            if (document is not null)
            {
                return (await document.GetTextAsync().ConfigureAwait(false)).ToString();
            }

            var generated = await project.GetSourceGeneratedDocumentsAsync().ConfigureAwait(false);
            var generatedDoc = generated.FirstOrDefault(d => SamePath(d.FilePath, path));
            if (generatedDoc is not null)
            {
                return (await generatedDoc.GetTextAsync().ConfigureAwait(false)).ToString();
            }
        }

        var fsharp = session.FSharpSnapshot.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => SamePath(d.Path, path));
        Assert.True(fsharp is not null, $"No document text for '{path}'.");
        return fsharp.Text;
    }

    /// <summary>
    /// 1-based line and column (UTF-16 code units, one column per tab) to a
    /// 0-based offset into <paramref name="text"/>.
    /// </summary>
    private static int OffsetOf(string text, int line, int column)
    {
        var lineStart = 0;
        for (var current = 1; current < line; current++)
        {
            var next = text.IndexOf('\n', lineStart);
            Assert.True(next >= 0, $"text has no line {line}.");
            lineStart = next + 1;
        }

        var breakStart = text.IndexOf('\n', lineStart);
        var lineEnd = breakStart < 0
            ? text.Length
            : breakStart > lineStart && text[breakStart - 1] == '\r' ? breakStart - 1 : breakStart;
        var lineLength = lineEnd - lineStart;
        Assert.InRange(column, 1, lineLength + 1);
        return lineStart + column - 1;
    }

    private static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static LoadedSolution CreateSingleDocumentLoaded(string projectName, string documentName, string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            projectName,
            projectName,
            LanguageNames.CSharp,
            filePath: $@"C:\fake\{projectName}.csproj"));
        solution = solution.AddDocument(
            documentId,
            documentName,
            SourceText.From(source),
            filePath: $@"C:\fake\{documentName}");
        solution = solution.WithProjectCompilationOptions(
            projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply the tabbed fixture workspace.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-pos-{label}-{Guid.NewGuid():N}");
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
            // best-effort cleanup
        }
    }
}
