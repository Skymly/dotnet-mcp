using DotNetMcp.Core;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// ADR-0006 part C (#523): symbol_resolve_at consumes the wired XAML usage
/// index — a position inside a registered .axaml / MAUI-.xaml document resolves
/// the covering usage point's target handle with resolvedFrom=Reference and
/// matched set to the exact usage span; a position outside every usage reports
/// NoSymbolAtPosition, and a session without an IXamlUsageSource keeps the
/// pre-seam UnsupportedDocumentForPosition refusal.
/// </summary>
public class XamlResolveAtTests
{
    private const string Axaml = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:local="using:SampleApp"
                xmlns:c="clr-namespace:SampleControls"
                x:Class="SampleApp.MainWindow"
                x:DataType="local:Customer">
            <c:FancyButton x:Name="TitleText"
                           Label="{Binding Name}"
                           Click="OnSave" />
            <TextBlock Text="{Binding Home.City}" />
        </Window>
        """;

    private const string MauiXaml = """
        <ContentPage xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     xmlns:local="clr-namespace:MauiPage"
                     x:Class="MauiPage.MainPage"
                     x:DataType="local:MainViewModel">
            <Label x:Name="TitleLabel" Text="{Binding Title}" />
        </ContentPage>
        """;

    private const string WpfXaml = """
        <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                x:Class="SampleApp.MainWindow">
            <TextBlock Text="{Binding Name}" />
        </Window>
        """;

    [Fact]
    public async Task axaml_binding_segments_resolve_to_data_context_members()
    {
        var root = CreateTempDir("xaml-bind");
        try
        {
            await using var fx = await OpenAvaloniaFixtureAsync(root);
            var axaml = Path.Combine(root, "MainWindow.axaml");

            // Each binding-path segment is its own usage span — matched carries
            // the segment token, not the whole {Binding ...} value.
            var namePos = TokenAt(axaml, "Label=\"{Binding Name}\"", "Name");
            var name = await ResolveAtAsync(fx, axaml, namePos.Line, namePos.Column);
            Assert.Equal(SymbolResolveFrom.Reference, name.ResolvedFrom);
            Assert.Equal("Property", name.Summary.Kind);
            Assert.Equal("Name", name.Summary.DisplayName);
            Assert.Equal("SampleApp.Customer", name.Summary.ContainingSymbol);
            AssertMatched(name, axaml, "Label=\"{Binding Name}\"", "Name");
            await AssertSummaryRoundTripsAsync(fx, name.Handle);

            var homePos = TokenAt(axaml, "Text=\"{Binding Home.City}\"", "Home");
            var home = await ResolveAtAsync(fx, axaml, homePos.Line, homePos.Column);
            Assert.Equal(SymbolResolveFrom.Reference, home.ResolvedFrom);
            Assert.Equal("SampleApp.Customer", home.Summary.ContainingSymbol);
            AssertMatched(home, axaml, "Text=\"{Binding Home.City}\"", "Home");

            var cityPos = TokenAt(axaml, "Text=\"{Binding Home.City}\"", "City");
            var city = await ResolveAtAsync(fx, axaml, cityPos.Line, cityPos.Column);
            Assert.Equal("Property", city.Summary.Kind);
            Assert.Equal("City", city.Summary.DisplayName);
            Assert.Equal("SampleApp.Address", city.Summary.ContainingSymbol);
            AssertMatched(city, axaml, "Text=\"{Binding Home.City}\"", "City");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task axaml_xclass_and_element_tags_resolve_to_named_types()
    {
        var root = CreateTempDir("xaml-types");
        try
        {
            await using var fx = await OpenAvaloniaFixtureAsync(root);
            var axaml = Path.Combine(root, "MainWindow.axaml");

            var classPos = TokenAt(axaml, "x:Class=\"SampleApp.MainWindow\"", "SampleApp.MainWindow");
            var xclass = await ResolveAtAsync(fx, axaml, classPos.Line, classPos.Column);
            Assert.Equal(SymbolResolveFrom.Reference, xclass.ResolvedFrom);
            Assert.Equal("NamedType", xclass.Summary.Kind);
            Assert.Equal("MainWindow", xclass.Summary.DisplayName);
            Assert.Equal("SampleApp", xclass.Summary.ContainingSymbol);
            AssertMatched(xclass, axaml, "x:Class=\"SampleApp.MainWindow\"", "SampleApp.MainWindow");
            await AssertSummaryRoundTripsAsync(fx, xclass.Handle);

            // The prefixed tag name is the usage span — a position anywhere
            // inside it resolves to the mapped CLR type.
            var fancyPos = TokenAt(axaml, "<c:FancyButton", "FancyButton");
            var fancy = await ResolveAtAsync(fx, axaml, fancyPos.Line, fancyPos.Column);
            Assert.Equal(SymbolResolveFrom.Reference, fancy.ResolvedFrom);
            Assert.Equal("NamedType", fancy.Summary.Kind);
            Assert.Equal("FancyButton", fancy.Summary.DisplayName);
            Assert.Equal("SampleControls", fancy.Summary.ContainingSymbol);
            AssertMatched(fancy, axaml, "<c:FancyButton", "c:FancyButton");

            var textPos = TokenAt(axaml, "<TextBlock", "TextBlock");
            var text = await ResolveAtAsync(fx, axaml, textPos.Line, textPos.Column);
            Assert.Equal("TextBlock", text.Summary.DisplayName);
            Assert.Equal("SampleControls", text.Summary.ContainingSymbol);
            AssertMatched(text, axaml, "<TextBlock", "TextBlock");

            var windowPos = TokenAt(axaml, "<Window xmlns=", "Window");
            var window = await ResolveAtAsync(fx, axaml, windowPos.Line, windowPos.Column);
            Assert.Equal("Window", window.Summary.DisplayName);
            Assert.Equal("SampleControls", window.Summary.ContainingSymbol);
            AssertMatched(window, axaml, "<Window xmlns=", "Window");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task axaml_event_attribute_value_resolves_to_handler_method()
    {
        var root = CreateTempDir("xaml-event");
        try
        {
            await using var fx = await OpenAvaloniaFixtureAsync(root);
            var axaml = Path.Combine(root, "MainWindow.axaml");

            var handlerPos = TokenAt(axaml, "Click=\"OnSave\"", "OnSave");
            var handler = await ResolveAtAsync(fx, axaml, handlerPos.Line, handlerPos.Column);
            Assert.Equal(SymbolResolveFrom.Reference, handler.ResolvedFrom);
            Assert.Equal("Method", handler.Summary.Kind);
            Assert.Equal("OnSave", handler.Summary.DisplayName);
            Assert.Equal("SampleApp.MainWindow", handler.Summary.ContainingSymbol);
            AssertMatched(handler, axaml, "Click=\"OnSave\"", "OnSave");
            await AssertSummaryRoundTripsAsync(fx, handler.Handle);

            // The attribute name itself resolves to the element's event member.
            var eventPos = TokenAt(axaml, "Click=\"OnSave\"", "Click");
            var click = await ResolveAtAsync(fx, axaml, eventPos.Line, eventPos.Column);
            Assert.Equal("Event", click.Summary.Kind);
            Assert.Equal("Click", click.Summary.DisplayName);
            Assert.Equal("SampleControls.FancyButton", click.Summary.ContainingSymbol);
            AssertMatched(click, axaml, "Click=\"OnSave\"", "Click");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task axaml_xname_value_resolves_to_the_generated_field()
    {
        var root = CreateTempDir("xaml-name");
        try
        {
            await using var fx = await OpenAvaloniaFixtureAsync(root);
            var axaml = Path.Combine(root, "MainWindow.axaml");

            var namePos = TokenAt(axaml, "x:Name=\"TitleText\"", "TitleText");
            var hit = await ResolveAtAsync(fx, axaml, namePos.Line, namePos.Column);
            Assert.Equal(SymbolResolveFrom.Reference, hit.ResolvedFrom);
            Assert.Equal("Field", hit.Summary.Kind);
            Assert.Equal("TitleText", hit.Summary.DisplayName);
            Assert.Equal("Internal", hit.Summary.Accessibility);
            Assert.Equal("SampleApp.MainWindow", hit.Summary.ContainingSymbol);
            AssertMatched(hit, axaml, "x:Name=\"TitleText\"", "TitleText");

            var summary = await fx.Client.CallToolAsync("symbol_summary", new Dictionary<string, object?>
            {
                ["handle"] = hit.Handle
            });
            Assert.True(summary.IsError is not true, InProcessMcpFixture.TextOf(summary));
            var roundTripped = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(summary);
            Assert.Equal("TitleText", roundTripped.Summary.DisplayName);
            Assert.Equal("Field", roundTripped.Summary.Kind);

            var attribution = await fx.Client.CallToolAsync("symbol_attribution", new Dictionary<string, object?>
            {
                ["handle"] = hit.Handle
            });
            Assert.True(attribution.IsError is not true, InProcessMcpFixture.TextOf(attribution));
            var attr = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attribution);
            Assert.Equal(SymbolOrigin.SourceGenerator, attr.OriginKind);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task axaml_positions_outside_usage_spans_report_no_symbol()
    {
        var root = CreateTempDir("xaml-miss");
        try
        {
            await using var fx = await OpenAvaloniaFixtureAsync(root);
            var axaml = Path.Combine(root, "MainWindow.axaml");

            // xmlns declarations are never usage points.
            var xmlnsPos = TokenAt(axaml, "xmlns:c=\"clr-namespace:SampleControls\"", "xmlns:c");
            var xmlns = await ResolveAtErrorAsync(fx, axaml, xmlnsPos.Line, xmlnsPos.Column);
            Assert.Equal(PolicyErrorCodes.NoSymbolAtPosition, xmlns.Error);

            // Plain markup between usages is not a usage either.
            var bracketPos = TokenAt(axaml, "x:DataType=\"local:Customer\">", ">");
            var bracket = await ResolveAtErrorAsync(fx, axaml, bracketPos.Line, bracketPos.Column);
            Assert.Equal(PolicyErrorCodes.NoSymbolAtPosition, bracket.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task axaml_project_id_scopes_the_lookup()
    {
        var root = CreateTempDir("xaml-pid");
        try
        {
            await using var fx = await OpenAvaloniaFixtureAsync(root);
            var axaml = Path.Combine(root, "MainWindow.axaml");

            var projects = await fx.Client.CallToolAsync(
                "workspace_list_projects", new Dictionary<string, object?>());
            Assert.True(projects.IsError is not true, InProcessMcpFixture.TextOf(projects));
            var listed = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(projects);
            var projectId = Assert.Single(listed.Projects).ProjectId;

            var savePos = TokenAt(axaml, "Click=\"OnSave\"", "OnSave");
            var scoped = await ResolveAtAsync(fx, axaml, savePos.Line, savePos.Column, projectId);
            Assert.Equal("OnSave", scoped.Summary.DisplayName);
            Assert.Equal(projectId, scoped.Summary.ProjectId);

            var other = await ResolveAtErrorAsync(
                fx, axaml, savePos.Line, savePos.Column, Guid.NewGuid().ToString("D"));
            Assert.Equal(PolicyErrorCodes.NoSymbolAtPosition, other.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task maui_xaml_positions_resolve_through_the_same_branch()
    {
        var root = CreateTempDir("xaml-maui");
        try
        {
            var solution = Path.Combine(root, "App.slnx");
            var xaml = Path.Combine(root, "MainPage.xaml");
            await File.WriteAllTextAsync(solution, "<Solution></Solution>");
            await File.WriteAllTextAsync(xaml, MauiXaml);

            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithMaui());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var classPos = TokenAt(xaml, "x:Class=\"MauiPage.MainPage\"", "MauiPage.MainPage");
            var xclass = await ResolveAtAsync(fx, xaml, classPos.Line, classPos.Column);
            Assert.Equal(SymbolResolveFrom.Reference, xclass.ResolvedFrom);
            Assert.Equal("NamedType", xclass.Summary.Kind);
            Assert.Equal("MainPage", xclass.Summary.DisplayName);
            AssertMatched(xclass, xaml, "x:Class=\"MauiPage.MainPage\"", "MauiPage.MainPage");

            var titlePos = TokenAt(xaml, "Text=\"{Binding Title}\"", "Title");
            var title = await ResolveAtAsync(fx, xaml, titlePos.Line, titlePos.Column);
            Assert.Equal(SymbolResolveFrom.Reference, title.ResolvedFrom);
            Assert.Equal("Property", title.Summary.Kind);
            Assert.Equal("Title", title.Summary.DisplayName);
            Assert.Equal("MauiPage.MainViewModel", title.Summary.ContainingSymbol);
            AssertMatched(title, xaml, "Text=\"{Binding Title}\"", "Title");
            await AssertSummaryRoundTripsAsync(fx, title.Handle);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task non_maui_xaml_is_not_registered_and_reports_no_symbol()
    {
        var root = CreateTempDir("xaml-wpf");
        try
        {
            // A WPF-flavored .xaml attaches as an AdditionalDocument but the
            // usage index rejects its root xmlns — every position misses.
            await using var fx = await OpenAvaloniaFixtureAsync(
                root, extraDocument: ("MainWindow.xaml", WpfXaml));
            var xaml = Path.Combine(root, "MainWindow.xaml");

            var textPos = TokenAt(xaml, "<TextBlock", "TextBlock");
            var element = await ResolveAtErrorAsync(fx, xaml, textPos.Line, textPos.Column);
            Assert.Equal(PolicyErrorCodes.NoSymbolAtPosition, element.Error);

            var classPos = TokenAt(xaml, "x:Class=\"SampleApp.MainWindow\"", "SampleApp.MainWindow");
            var xclass = await ResolveAtErrorAsync(fx, xaml, classPos.Line, classPos.Column);
            Assert.Equal(PolicyErrorCodes.NoSymbolAtPosition, xclass.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task unwired_session_keeps_unsupported_document_for_position()
    {
        var root = CreateTempDir("xaml-unwired");
        try
        {
            var solution = Path.Combine(root, "App.slnx");
            var axaml = Path.Combine(root, "MainWindow.axaml");
            await File.WriteAllTextAsync(solution, "<Solution></Solution>");
            await File.WriteAllTextAsync(axaml, Axaml);

            var roots = TestTrustedRoots.Create(root);
            await using var host = new WorkspaceHost(
                FakeSolutionLoader.ImmediateWithAvaloniaXamlResolveAt(root),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.Zero,
                    FileWatcher = new ManualWorkspaceFileWatcher(),
                },
                roots); // no IXamlUsageSource — sessions expose no provider
            await WorkspaceReady.OpenUntilReadyAsync(host, solution);

            var roslyn = new RoslynLanguageAdapter(new GeneratorQueryService());
            var tools = new SymbolTools(
                host,
                new WorkspaceEdit(host, roots, TimeProvider.System, TimeSpan.FromMinutes(5)),
                new LanguageAdapters([roslyn]),
                roots,
                new RecordingAuditLogger());

            var result = await tools.SymbolResolveAt(axaml, 1, 1);
            Assert.True(result.IsError is true, InProcessMcpFixture.TextOf(result));
            var error = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.UnsupportedDocumentForPosition, error.Error);
            Assert.Contains("later XAML ticket", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<InProcessMcpFixture> OpenAvaloniaFixtureAsync(
        string root,
        (string Name, string Content)? extraDocument = null)
    {
        var solution = Path.Combine(root, "App.slnx");
        var axaml = Path.Combine(root, "MainWindow.axaml");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axaml, Axaml);
        if (extraDocument is { } extra)
        {
            await File.WriteAllTextAsync(Path.Combine(root, extra.Name), extra.Content);
        }

        var fx = new InProcessMcpFixture(
            TestTrustedRoots.Create(root),
            FakeSolutionLoader.ImmediateWithAvaloniaXamlResolveAt(root));
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

    /// <summary>
    /// matched must be the exact usage span — the same unified quad the index
    /// reports (1-based, UTF-16 columns, endColumn exclusive), never the whole
    /// source line.
    /// </summary>
    private static void AssertMatched(
        SymbolResolveAtResultDto result,
        string path,
        string containing,
        string spanText)
    {
        var expected = TokenAt(path, containing, spanText);
        Assert.Equal(expected.Line, result.Matched.Line);
        Assert.Equal(expected.Column, result.Matched.Column);
        Assert.Equal(expected.Line, result.Matched.EndLine);
        Assert.Equal(expected.Column + spanText.Length, result.Matched.EndColumn);
        Assert.Equal(spanText, result.Matched.Text);
    }

    /// <summary>1-based (line, column) of <paramref name="token"/> inside the line containing <paramref name="containing"/>.</summary>
    private static (int Line, int Column) TokenAt(string path, string containing, string token)
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

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-xaml-resolve-at-{label}-{Guid.NewGuid():N}");
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
