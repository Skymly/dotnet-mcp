using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// ADR-0001 Amendment 7: the position entry — filePath + line + column from
/// other tools feed symbol_resolve_at verbatim and produce the same
/// addressable-handle set symbol_resolve returns.
/// </summary>
public class SymbolResolveAtSeamTests
{
    // ------------------------------------------------------------------
    // C#: declarations, references, normalized positions
    // ------------------------------------------------------------------

    [Fact]
    public async Task csharp_declaration_and_reference_positions_resolve()
    {
        var root = CreateTempDir("cs");
        try
        {
            await using var fx = await OpenResolveAtFixtureAsync(root);
            var widgetPath = Path.Combine(root, "ResolveApp", "Widget.cs");

            var (declLine, declCol) = PositionOf(widgetPath, "public sealed class Widget", "Widget");
            var decl = await ResolveAtAsync(fx, widgetPath, declLine, declCol);
            Assert.Equal("Declaration", decl.ResolvedFrom);
            Assert.Equal("Widget", decl.Matched.Text);
            Assert.Equal("Widget", decl.Summary.DisplayName);
            Assert.Equal("NamedType", decl.Summary.Kind);
            await AssertSummaryRoundTripsAsync(fx, decl.Handle);

            var (refLine, refCol) = PositionOf(widgetPath, "var local = widget.Ping", "Ping");
            var reference = await ResolveAtAsync(fx, widgetPath, refLine, refCol);
            Assert.Equal("Reference", reference.ResolvedFrom);
            Assert.Equal("Ping", reference.Matched.Text);
            Assert.Equal("Ping", reference.Summary.DisplayName);
            await AssertSummaryRoundTripsAsync(fx, reference.Handle);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task csharp_normalized_positions_resolve_stable_handles()
    {
        var root = CreateTempDir("csnorm");
        try
        {
            await using var fx = await OpenResolveAtFixtureAsync(root);
            var widgetPath = Path.Combine(root, "ResolveApp", "Widget.cs");

            // `var` keyword → the inferred type.
            var (varLine, varCol) = PositionOf(widgetPath, "var w = new Widget()", "var");
            var varHit = await ResolveAtAsync(fx, widgetPath, varLine, varCol);
            Assert.Equal("Widget", varHit.Summary.DisplayName);
            Assert.Equal("var", varHit.Matched.Text);
            Assert.Equal("Reference", varHit.ResolvedFrom);

            // `new Widget()` type name → the named type.
            var (ctorLine, ctorCol) = PositionOf(widgetPath, "var w = new Widget()", "Widget");
            var ctorHit = await ResolveAtAsync(fx, widgetPath, ctorLine, ctorCol);
            Assert.Equal("Widget", ctorHit.Summary.DisplayName);
            Assert.Equal("Widget", ctorHit.Matched.Text);

            // Reduced extension method → method on the declaring type.
            var (twiceLine, twiceCol) = PositionOf(widgetPath, "var twice = w.Twice()", "Twice");
            var twiceHit = await ResolveAtAsync(fx, widgetPath, twiceLine, twiceCol);
            Assert.Equal("Twice", twiceHit.Summary.DisplayName);
            Assert.Equal("ResolveApp.WidgetExtensions", twiceHit.Summary.ContainingSymbol);

            // using alias declaration position → alias target.
            var (aliasLine, aliasCol) = PositionOf(widgetPath, "using AliasedWidget", "AliasedWidget");
            var aliasHit = await ResolveAtAsync(fx, widgetPath, aliasLine, aliasCol);
            Assert.Equal("Widget", aliasHit.Summary.DisplayName);
            Assert.Equal("AliasedWidget", aliasHit.Matched.Text);
            Assert.Equal("Declaration", aliasHit.ResolvedFrom);

            // using alias usage → alias target type.
            var (aliasUseLine, aliasUseCol) = PositionOf(widgetPath, "new AliasedWidget()", "AliasedWidget");
            var aliasUseHit = await ResolveAtAsync(fx, widgetPath, aliasUseLine, aliasUseCol);
            Assert.Equal("Widget", aliasUseHit.Summary.DisplayName);

            // Constructed generic List<int> → List<T> handle.
            var (listLine, listCol) = PositionOf(widgetPath, "private List<int> items", "List");
            var listHit = await ResolveAtAsync(fx, widgetPath, listLine, listCol);
            Assert.Equal("List<T>", listHit.Summary.DisplayName);
            Assert.Equal("System.Collections.Generic", listHit.Summary.ContainingSymbol);

            foreach (var hit in new[] { varHit, ctorHit, twiceHit, aliasHit, aliasUseHit, listHit })
            {
                await AssertSummaryRoundTripsAsync(fx, hit.Handle);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task csharp_local_and_parameter_positions_are_not_addressable()
    {
        var root = CreateTempDir("csnotaddr");
        try
        {
            await using var fx = await OpenResolveAtFixtureAsync(root);
            var widgetPath = Path.Combine(root, "ResolveApp", "Widget.cs");

            var (localLine, localCol) = PositionOf(widgetPath, "widget.Ping (local)", "local");
            var localError = await ResolveAtErrorAsync(fx, widgetPath, localLine, localCol);
            Assert.Equal(PolicyErrorCodes.SymbolNotAddressable, localError.Error);
            Assert.Contains("ResolveApp.Uses.Call", localError.Message, StringComparison.Ordinal);

            var (paramLine, paramCol) = PositionOf(widgetPath, "var local = widget.Ping", "widget");
            var paramError = await ResolveAtErrorAsync(fx, widgetPath, paramLine, paramCol);
            Assert.Equal(PolicyErrorCodes.SymbolNotAddressable, paramError.Error);
            Assert.Contains("ResolveApp.Uses.Call", paramError.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task csharp_position_validation_and_left_token_fallback()
    {
        var root = CreateTempDir("csloc");
        try
        {
            await using var fx = await OpenResolveAtFixtureAsync(root);
            var widgetPath = Path.Combine(root, "ResolveApp", "Widget.cs");

            // Whitespace between `Ping` and `(` hits the identifier to its left.
            var (wsLine, wsCol) = PositionOf(widgetPath, "widget.Ping (local)", "Ping");
            var wsHit = await ResolveAtAsync(fx, widgetPath, wsLine, wsCol + 4);
            Assert.Equal("Ping", wsHit.Matched.Text);
            Assert.Equal("Ping", wsHit.Summary.DisplayName);

            // Column past the line end clamps and still resolves.
            var (eolLine, _) = PositionOf(widgetPath, "public sealed class Widget", "class");
            var eolHit = await ResolveAtAsync(fx, widgetPath, eolLine, 400);
            Assert.Equal("Widget", eolHit.Summary.DisplayName);
            Assert.Equal("Widget", eolHit.Matched.Text);

            // line = 0 / column = 0 / line beyond the document → InvalidPosition.
            var lineZero = await ResolveAtErrorAsync(fx, widgetPath, 0, 1);
            Assert.Equal(PolicyErrorCodes.InvalidPosition, lineZero.Error);
            var colZero = await ResolveAtErrorAsync(fx, widgetPath, 1, 0);
            Assert.Equal(PolicyErrorCodes.InvalidPosition, colZero.Error);
            var farLine = await ResolveAtErrorAsync(fx, widgetPath, 9999, 1);
            Assert.Equal(PolicyErrorCodes.InvalidPosition, farLine.Error);

            // A non-identifier token (=>) whose left neighbour is not an identifier.
            var (arrowLine, arrowCol) = PositionOf(widgetPath, "value) => value + 1", "=>");
            var arrowError = await ResolveAtErrorAsync(fx, widgetPath, arrowLine, arrowCol);
            Assert.Equal(PolicyErrorCodes.NoSymbolAtPosition, arrowError.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task csharp_overload_candidates()
    {
        var root = CreateTempDir("cscand");
        try
        {
            await using var fx = await OpenResolveAtFixtureAsync(root);
            var widgetPath = Path.Combine(root, "ResolveApp", "Widget.cs");

            // Single remaining candidate → success + candidateReason.
            var (singleLine, singleCol) = PositionOf(widgetPath, "widget.Ping(1, 2)", "Ping");
            var single = await ResolveAtAsync(fx, widgetPath, singleLine, singleCol);
            Assert.Equal("Ping", single.Summary.DisplayName);
            Assert.False(string.IsNullOrEmpty(single.CandidateReason));
            await AssertSummaryRoundTripsAsync(fx, single.Handle);

            // Multiple candidates → SymbolAmbiguous listing the signatures.
            var (ambLine, ambCol) = PositionOf(widgetPath, "widget.Overload(1.5)", "Overload");
            var ambiguous = await ResolveAtErrorAsync(fx, widgetPath, ambLine, ambCol);
            Assert.Equal(PolicyErrorCodes.SymbolAmbiguous, ambiguous.Error);
            Assert.Contains("Overload", ambiguous.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // Path / workspace gates
    // ------------------------------------------------------------------

    [Fact]
    public async Task resolve_at_gates_blank_untrusted_xaml_and_missing_documents()
    {
        var root = CreateTempDir("csgates");
        try
        {
            await using var fx = await OpenResolveAtFixtureAsync(root);
            var widgetPath = Path.Combine(root, "ResolveApp", "Widget.cs");
            var (line, col) = PositionOf(widgetPath, "public sealed class Widget", "Widget");

            var blank = await ResolveAtErrorAsync(fx, "", 1, 1);
            Assert.Equal(PolicyErrorCodes.InvalidWorkspacePath, blank.Error);

            var outside = await ResolveAtErrorAsync(
                fx, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Elsewhere.cs"), 1, 1);
            Assert.Equal(PolicyErrorCodes.PathOutsideTrustedRoots, outside.Error);

            var axaml = await ResolveAtErrorAsync(fx, Path.Combine(root, "View.axaml"), 1, 1);
            Assert.Equal(PolicyErrorCodes.UnsupportedDocumentForPosition, axaml.Error);

            var missing = await ResolveAtErrorAsync(fx, Path.Combine(root, "NotHere.cs"), 1, 1);
            Assert.Equal(PolicyErrorCodes.DocumentNotInWorkspace, missing.Error);

            var wrongProject = await ResolveAtErrorAsync(
                fx, widgetPath, line, col, Guid.NewGuid().ToString("D"));
            Assert.Equal(PolicyErrorCodes.DocumentNotInWorkspace, wrongProject.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // Round-trips: other tools' positions feed symbol_resolve_at verbatim
    // ------------------------------------------------------------------

    [Fact]
    public async Task csharp_find_references_goto_definition_find_callers_feed_back()
    {
        var root = CreateTempDir("csround");
        try
        {
            await using var fx = await OpenResolveAtFixtureAsync(root);

            var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
            var projectId = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list)
                .Projects.Single().ProjectId;

            var resolved = await fx.Client.CallToolAsync("symbol_resolve", new Dictionary<string, object?>
            {
                ["name"] = "ResolveApp.Widget.Ping",
                ["projectId"] = projectId
            });
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var references = await fx.Client.CallToolAsync("symbol_find_references", new Dictionary<string, object?>
            {
                ["handle"] = handle
            });
            var refItems = InProcessMcpFixture.Deserialize<SymbolFindReferencesResultDto>(references).Items;
            foreach (var item in refItems.Where(i => i.Kind == "Reference"))
            {
                var refHit = await ResolveAtAsync(
                    fx, item.FilePath!, item.Line!.Value, item.Column!.Value);
                Assert.Equal(handle, refHit.Handle);
            }

            var definition = await fx.Client.CallToolAsync("symbol_goto_definition", new Dictionary<string, object?>
            {
                ["handle"] = handle
            });
            var defLoc = InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(definition).Locations[0];
            var defHit = await ResolveAtAsync(fx, defLoc.FilePath!, defLoc.Line!.Value, defLoc.Column!.Value);
            Assert.Equal(handle, defHit.Handle);
            Assert.Equal("Declaration", defHit.ResolvedFrom);

            var callers = await fx.Client.CallToolAsync("symbol_find_callers", new Dictionary<string, object?>
            {
                ["handle"] = handle
            });
            var callerItems = InProcessMcpFixture.Deserialize<SymbolFindCallersResultDto>(callers).Items;
            Assert.NotEmpty(callerItems);
            foreach (var caller in callerItems)
            {
                var callerHit = await ResolveAtAsync(
                    fx, caller.FilePath!, caller.Line!.Value, caller.Column!.Value);
                Assert.Equal(handle, callerHit.Handle);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task csharp_project_diagnostics_positions_feed_back()
    {
        var root = CreateTempDir("csdiag");
        try
        {
            await using var fx = await OpenResolveAtFixtureAsync(root);

            var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
            var projectId = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list)
                .Projects.Single().ProjectId;

            var diagnostics = await fx.Client.CallToolAsync("project_diagnostics", new Dictionary<string, object?>
            {
                ["projectId"] = projectId
            });
            var items = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(diagnostics).Items;
            Assert.NotEmpty(items);

            var resolvedHandles = new List<string>();
            foreach (var item in items)
            {
                Assert.NotNull(item.FilePath);
                Assert.NotNull(item.Line);
                Assert.NotNull(item.Column);
                var result = await fx.Client.CallToolAsync("symbol_resolve_at", new Dictionary<string, object?>
                {
                    ["path"] = item.FilePath,
                    ["line"] = item.Line.Value,
                    ["column"] = item.Column.Value
                });
                if (result.IsError is true)
                {
                    // Diagnostics that land on non-identifier text (e.g. a literal
                    // argument) deterministically fail.
                    var error = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
                    Assert.Contains(error.Error, new[]
                    {
                        PolicyErrorCodes.NoSymbolAtPosition,
                        PolicyErrorCodes.SymbolNotAddressable,
                        PolicyErrorCodes.SymbolAmbiguous
                    });
                    continue;
                }

                var hit = InProcessMcpFixture.Deserialize<SymbolResolveAtResultDto>(result);
                resolvedHandles.Add(hit.Handle);
                await AssertSummaryRoundTripsAsync(fx, hit.Handle);
            }

            // The identifier-landing warnings/errors resolve to real symbols.
            Assert.NotEmpty(resolvedHandles);
            Assert.Contains(resolvedHandles, h => h.Contains("neverAssigned", StringComparison.Ordinal));
            Assert.Contains(resolvedHandles, h => h.Contains("assignedNotUsed", StringComparison.Ordinal));
            Assert.Contains(resolvedHandles, h => h.Contains("Widget.Ping(int)", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // VB
    // ------------------------------------------------------------------

    [Fact]
    public async Task vb_declaration_reference_and_diagnostics_positions_resolve()
    {
        var root = CreateTempDir("vb");
        var projectDir = Path.Combine(root, "ResolveVb");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithResolveAtVbOnDisk(projectDir));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var widgetPath = Path.Combine(projectDir, "Widget.vb");
            var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
            var projectId = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list)
                .Projects.Single().ProjectId;

            var (declLine, declCol) = PositionOf(widgetPath, "Public Function Ping()", "Ping");
            var decl = await ResolveAtAsync(fx, widgetPath, declLine, declCol);
            Assert.Equal("Declaration", decl.ResolvedFrom);
            Assert.Equal("Ping", decl.Matched.Text);
            Assert.Equal("vb", decl.Summary.Language);
            await AssertSummaryRoundTripsAsync(fx, decl.Handle);

            var (refLine, refCol) = PositionOf(widgetPath, "New Widget().Ping()", "Ping");
            var reference = await ResolveAtAsync(fx, widgetPath, refLine, refCol);
            Assert.Equal("Reference", reference.ResolvedFrom);
            Assert.Equal(decl.Handle, reference.Handle);

            var (paramLine, paramCol) = PositionOf(widgetPath, "Return text", "text");
            var paramError = await ResolveAtErrorAsync(fx, widgetPath, paramLine, paramCol);
            Assert.Equal(PolicyErrorCodes.SymbolNotAddressable, paramError.Error);
            Assert.Contains("Widget.Echo", paramError.Message, StringComparison.Ordinal);

            // Diagnostics that land on identifiers (e.g. the implicit-shadowing
            // warning on `Ping` in SpecialWidget) resolve; rows landing on
            // expressions may fail deterministically.
            var diagnostics = await fx.Client.CallToolAsync("project_diagnostics", new Dictionary<string, object?>
            {
                ["projectId"] = projectId
            });
            var items = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(diagnostics).Items;
            Assert.NotEmpty(items);
            var diagHandles = new List<SymbolResolveAtResultDto>();
            foreach (var item in items)
            {
                var result = await fx.Client.CallToolAsync("symbol_resolve_at", new Dictionary<string, object?>
                {
                    ["path"] = item.FilePath,
                    ["line"] = item.Line,
                    ["column"] = item.Column
                });
                if (result.IsError is not true)
                {
                    var hit = InProcessMcpFixture.Deserialize<SymbolResolveAtResultDto>(result);
                    diagHandles.Add(hit);
                    await AssertSummaryRoundTripsAsync(fx, hit.Handle);
                }
            }

            Assert.Contains(diagHandles, h => h.Matched.Text == "Ping" && h.ResolvedFrom == "Declaration");

            // goto_definition output feeds back verbatim.
            var resolved = await fx.Client.CallToolAsync("symbol_resolve", new Dictionary<string, object?>
            {
                ["name"] = "Widget",
                ["projectId"] = projectId
            });
            var widgetHandle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
            var definition = await fx.Client.CallToolAsync("symbol_goto_definition", new Dictionary<string, object?>
            {
                ["handle"] = widgetHandle
            });
            var defLoc = InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(definition).Locations[0];
            var defHit = await ResolveAtAsync(fx, defLoc.FilePath!, defLoc.Line!.Value, defLoc.Column!.Value);
            Assert.Equal(widgetHandle, defHit.Handle);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // F#
    // ------------------------------------------------------------------

    [Fact]
    public async Task fsharp_declaration_reference_and_locals_resolve_or_fail_cleanly()
    {
        var root = CreateTempDir("fs");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithResolveAtFsharp(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var widgetPath = Path.Combine(root, "ResolveFs", "Widget.fs");
            var usesPath = Path.Combine(root, "ResolveFs", "Uses.fs");

            // `let ping ...` declaration → member handle in the catalog's signature form.
            var decl = await ResolveAtAsync(fx, widgetPath, 3, 5);
            Assert.Equal("Declaration", decl.ResolvedFrom);
            Assert.Equal("ping", decl.Matched.Text);
            Assert.Equal("fsharp", decl.Summary.Language);
            Assert.StartsWith("fsharp:", decl.Handle, StringComparison.Ordinal);
            await AssertSummaryRoundTripsAsync(fx, decl.Handle);

            // `Widget.ping local` reference — position inside `ping` resolves the member.
            var pingColumn = ColumnInLine(usesPath, "Widget.ping local", "ping");
            var reference = await ResolveAtAsync(fx, usesPath, 5, pingColumn);
            Assert.Equal("Reference", reference.ResolvedFrom);
            Assert.Equal(decl.Handle, reference.Handle);

            // `Widget` position resolves the module/entity catalog item.
            var widgetColumn = ColumnInLine(usesPath, "Widget.ping local", "Widget");
            var entity = await ResolveAtAsync(fx, usesPath, 5, widgetColumn);
            Assert.Equal("fsharp", entity.Summary.Language);
            await AssertSummaryRoundTripsAsync(fx, entity.Handle);

            // Inner `let local` and parameter `seed` are not addressable.
            var local = await ResolveAtErrorAsync(fx, usesPath, 4, ColumnInLine(usesPath, "let local", "local"));
            Assert.Equal(PolicyErrorCodes.SymbolNotAddressable, local.Error);
            Assert.Contains("ResolveFs.Uses.go", local.Message, StringComparison.Ordinal);

            var seed = await ResolveAtErrorAsync(fx, usesPath, 4, ColumnInLine(usesPath, "seed + 1", "seed"));
            Assert.Equal(PolicyErrorCodes.SymbolNotAddressable, seed.Error);
            Assert.Contains("ResolveFs.Uses.go", seed.Message, StringComparison.Ordinal);

            // `let go` declaration resolves its own member handle.
            var goDecl = await ResolveAtAsync(fx, usesPath, 3, ColumnInLine(usesPath, "let go", "go"));
            Assert.Equal("Declaration", goDecl.ResolvedFrom);
            Assert.Contains("ResolveFs.Uses.go", goDecl.Handle, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_diagnostics_position_feeds_back()
    {
        var root = CreateTempDir("fsdiag");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithResolveAtFsharp(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
            var projectId = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list)
                .Projects.Single().ProjectId;

            var diagnostics = await fx.Client.CallToolAsync("project_diagnostics", new Dictionary<string, object?>
            {
                ["projectId"] = projectId
            });
            var items = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(diagnostics).Items;
            Assert.NotEmpty(items);
            foreach (var item in items)
            {
                var result = await fx.Client.CallToolAsync("symbol_resolve_at", new Dictionary<string, object?>
                {
                    ["path"] = item.FilePath,
                    ["line"] = item.Line,
                    ["column"] = item.Column
                });
                Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
                var hit = InProcessMcpFixture.Deserialize<SymbolResolveAtResultDto>(result);
                await AssertSummaryRoundTripsAsync(fx, hit.Handle);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // Source-generated documents + multi-TFM shared files
    // ------------------------------------------------------------------

    [Fact]
    public async Task generated_document_position_resolves_and_attributes_generator()
    {
        var root = CreateTempDir("gen");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithGenerators(Path.Combine(root, "GeneratorHost.csproj")));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var resolved = await fx.Client.CallToolAsync("symbol_resolve", new Dictionary<string, object?>
            {
                ["name"] = "SampleApp.Generated.CustomMarker"
            });
            var expected = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var definition = await fx.Client.CallToolAsync("symbol_goto_definition", new Dictionary<string, object?>
            {
                ["handle"] = expected
            });
            var location = InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(definition)
                .Locations.First(l => l.FilePath is not null);
            var hit = await ResolveAtAsync(
                fx, location.FilePath!, location.Line!.Value, location.Column!.Value);
            Assert.Equal(expected, hit.Handle);
            Assert.Equal("Declaration", hit.ResolvedFrom);

            var attribution = await fx.Client.CallToolAsync("symbol_attribution", new Dictionary<string, object?>
            {
                ["handle"] = hit.Handle
            });
            var body = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attribution);
            Assert.Equal("SourceGenerator", body.OriginKind);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task multi_tfm_shared_file_ambiguous_then_projectid_disambiguates()
    {
        var root = CreateTempDir("tfm");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithResolveAtMultiTfmOnDisk(root));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var sharedPath = Path.Combine(root, "Shared", "Shared.cs");
            var list = await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>());
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list).Projects;
            Assert.Equal(2, projects.Count);

            // `Lib.Ping` binds to a different signature in each project.
            var (pingLine, pingCol) = PositionOf(sharedPath, "Lib.Ping(default)", "Ping");
            var ambiguous = await ResolveAtErrorAsync(fx, sharedPath, pingLine, pingCol);
            Assert.Equal(PolicyErrorCodes.SymbolAmbiguous, ambiguous.Error);
            Assert.Contains("Ping(int)", ambiguous.Message, StringComparison.Ordinal);
            Assert.Contains("Ping(string)", ambiguous.Message, StringComparison.Ordinal);

            var perProject = new List<string>();
            foreach (var project in projects)
            {
                var hit = await ResolveAtAsync(
                    fx, sharedPath, pingLine, pingCol, project.ProjectId);
                Assert.Equal("Ping", hit.Matched.Text);
                await AssertSummaryRoundTripsAsync(fx, hit.Handle);
                perProject.Add(hit.Handle);
            }

            Assert.Equal(2, perProject.Distinct(StringComparer.Ordinal).Count());

            // `Shared.Common` has an identical signature in both projects → dedupe.
            var (commonLine, commonCol) = PositionOf(sharedPath, "public static int Common()", "Common");
            var common = await ResolveAtAsync(fx, sharedPath, commonLine, commonCol);
            Assert.Equal("Common", common.Summary.DisplayName);
            await AssertSummaryRoundTripsAsync(fx, common.Handle);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<InProcessMcpFixture> OpenResolveAtFixtureAsync(string root)
    {
        var projectDir = Path.Combine(root, "ResolveApp");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var fx = new InProcessMcpFixture(
            TrustedRoots.Create([root]),
            FakeSolutionLoader.ImmediateWithResolveAtOnDisk(projectDir));
        await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
        return fx;
    }

    private static async Task<SymbolResolveAtResultDto> ResolveAtAsync(
        InProcessMcpFixture fx,
        string path,
        int line,
        int column,
        string? projectId = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["line"] = line,
            ["column"] = column
        };
        if (projectId is not null)
        {
            args["projectId"] = projectId;
        }

        var result = await fx.Client.CallToolAsync("symbol_resolve_at", args);
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<SymbolResolveAtResultDto>(result);
    }

    private static async Task<PolicyErrorDto> ResolveAtErrorAsync(
        InProcessMcpFixture fx,
        string path,
        int line,
        int column,
        string? projectId = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["path"] = path,
            ["line"] = line,
            ["column"] = column
        };
        if (projectId is not null)
        {
            args["projectId"] = projectId;
        }

        var result = await fx.Client.CallToolAsync("symbol_resolve_at", args);
        Assert.True(result.IsError is true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
    }

    private static async Task AssertSummaryRoundTripsAsync(InProcessMcpFixture fx, string handle)
    {
        var summary = await fx.Client.CallToolAsync("symbol_summary", new Dictionary<string, object?>
        {
            ["handle"] = handle
        });
        Assert.True(summary.IsError is not true, InProcessMcpFixture.TextOf(summary));
    }

    /// <summary>1-based (line, column) of <paramref name="token"/> inside the line containing <paramref name="containing"/>.</summary>
    private static (int Line, int Column) PositionOf(string path, string containing, string token)
    {
        var text = File.ReadAllText(path);
        var index = text.IndexOf(containing, StringComparison.Ordinal);
        Assert.True(index >= 0, containing);
        var tokenIndex = text.IndexOf(token, index, StringComparison.Ordinal);
        Assert.True(tokenIndex >= 0 && tokenIndex < index + containing.Length, token);
        var line = 1;
        var column = 1;
        for (var i = 0; i < tokenIndex; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        return (line, column);
    }

    private static int ColumnInLine(string path, string containing, string token) =>
        PositionOf(path, containing, token).Column;

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-resolve-at-{label}-{Guid.NewGuid():N}");
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
