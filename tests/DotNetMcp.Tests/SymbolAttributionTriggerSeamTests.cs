using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// symbol_attribution third-axis triggers (issue #524) over the Adhoc fixtures:
/// universal partial pairing, XAML index evidence, and honest degradation.
/// </summary>
public class SymbolAttributionTriggerSeamTests
{
    [Fact]
    public async Task generated_partial_definition_pairs_to_handwritten_implementation()
    {
        var root = CreateTempDir("trigger-partial");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithGenerators());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            // The generated tree holds the partial method *definition*; the
            // handwritten implementation pairs back through Roslyn's partial APIs.
            var body = await AttributionAsync(fx, "GeneratorHost.PartialThing.OnValueChanged(int)");

            Assert.Equal("SourceGenerator", body.OriginKind);
            var trigger = Assert.Single(body.Triggers);
            Assert.Equal("PartialDefinition", trigger.Kind);
            Assert.Equal("Exact", trigger.Confidence);
            Assert.Equal("partial implementation", trigger.Detail);
            Assert.EndsWith("Host.cs", trigger.Path, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(trigger.Handle);

            // The span is the handwritten identifier in the (virtual) fixture file.
            Assert.Equal("OnValueChanged".Length, trigger.Length);
            Assert.True(trigger.Line > 0 && trigger.Column > 0);

            // Code trigger handles re-resolve through symbol_summary.
            var summary = await fx.Client.CallToolAsync(
                "symbol_summary",
                new Dictionary<string, object?> { ["handle"] = trigger.Handle! });
            Assert.True(summary.IsError is not true, InProcessMcpFixture.TextOf(summary));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task unknown_generator_yields_empty_triggers_with_message()
    {
        var root = CreateTempDir("trigger-unknown");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithGenerators());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            // The fully generated marker type reports per-member attribution.
            var body = await AttributionAsync(fx, "SampleApp.Generated.CustomMarker");

            var member = Assert.Single(body.Members, kv => kv.Key.EndsWith(".Who", StringComparison.Ordinal));
            Assert.Equal("SourceGenerator", member.Value.OriginKind);
            Assert.Equal("CustomGenerator.MarkerGenerator", member.Value.Generator!.TypeFullName);
            Assert.Empty(member.Value.Triggers);
            Assert.NotNull(member.Value.TriggerMessage);
            Assert.Contains("CustomGenerator.MarkerGenerator", member.Value.TriggerMessage, StringComparison.Ordinal);
            Assert.Contains("No trigger rule", member.Value.TriggerMessage, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task avalonia_xname_generated_field_reports_xaml_element_trigger()
    {
        var root = CreateTempDir("trigger-xname");
        var solution = Path.Combine(root, "App.slnx");
        var axamlPath = Path.Combine(root, "MainWindow.axaml");
        const string axaml = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="SampleApp.MainWindow">
                <TextBlock x:Name="TitleText" />
            </Window>
            """;
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axamlPath, axaml);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithGenerators());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            // Avalonia.NameGenerator (CustomGenerator stand-in) emits the field.
            var body = await AttributionAsync(fx, "SampleApp.MainWindow.TitleText");

            Assert.Equal("SourceGenerator", body.OriginKind);
            var trigger = Assert.Single(body.Triggers);
            Assert.Equal("XamlElement", trigger.Kind);
            Assert.Equal(Path.GetFullPath(axamlPath), Path.GetFullPath(trigger.Path!));
            Assert.Null(trigger.Handle);
            Assert.Contains("TitleText", trigger.Detail, StringComparison.Ordinal);
            Assert.Equal("TitleText", axaml.Substring(trigger.Start!.Value, trigger.Length!.Value));
            AssertTriggerQuad(trigger, axaml);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task initialize_component_reports_xaml_root_trigger()
    {
        var root = CreateTempDir("trigger-init");
        var solution = Path.Combine(root, "App.slnx");
        var axamlPath = Path.Combine(root, "MainView.axaml");
        const string axaml = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="GeneratorHost.PartialThing">
                <TextBlock />
            </Window>
            """;
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        await File.WriteAllTextAsync(axamlPath, axaml);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithGenerators());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var body = await AttributionAsync(fx, "GeneratorHost.PartialThing.InitializeComponent()");

            Assert.Equal("SourceGenerator", body.OriginKind);
            var trigger = Assert.Single(body.Triggers);
            Assert.Equal("XamlElement", trigger.Kind);
            Assert.Equal(Path.GetFullPath(axamlPath), Path.GetFullPath(trigger.Path!));
            // Anchored at the document root element name (the first ElementType
            // usage in the document), not merely at the file.
            Assert.Equal("Window", axaml.Substring(trigger.Start!.Value, trigger.Length!.Value));
            AssertTriggerQuad(trigger, axaml);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task vb_generated_partial_declaration_pairs_to_handwritten_implementation()
    {
        var root = CreateTempDir("trigger-vb");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithVbGenerators());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var body = await AttributionAsync(fx, "GeneratorHost.VbPartialThing");

            var member = Assert.Single(
                body.Members, kv => kv.Key.Contains("VbCallback", StringComparison.Ordinal));
            Assert.Equal("SourceGenerator", member.Value.OriginKind);
            var trigger = Assert.Single(member.Value.Triggers);
            Assert.Equal("PartialDefinition", trigger.Kind);
            Assert.Equal("Exact", trigger.Confidence);
            Assert.EndsWith("Host.vb", trigger.Path, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(trigger.Handle);
            Assert.Equal("VbCallback".Length, trigger.Length);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task handwritten_symbols_report_empty_triggers()
    {
        var root = CreateTempDir("trigger-none");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TestTrustedRoots.Create(root),
                FakeSolutionLoader.ImmediateWithGenerators());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var body = await AttributionAsync(fx, "GeneratorHost.PartialThing");

            Assert.Equal("Handwritten", body.OriginKind);
            Assert.Empty(body.Triggers);
            Assert.Null(body.TriggerMessage);
            Assert.All(body.Members.Values, member =>
            {
                if (member.OriginKind == "Handwritten")
                {
                    Assert.Empty(member.Triggers);
                    Assert.Null(member.TriggerMessage);
                }
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<SymbolAttributionResultDto> AttributionAsync(
        InProcessMcpFixture fx, string name)
    {
        var resolved = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name });
        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
        var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

        var attr = await fx.Client.CallToolAsync(
            "symbol_attribution",
            new Dictionary<string, object?> { ["handle"] = handle });
        Assert.True(attr.IsError is not true, InProcessMcpFixture.TextOf(attr));
        return InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attr);
    }

    private static void AssertTriggerQuad(GenerationTriggerDto trigger, string text)
    {
        // Unified position model: start/length raw offsets, 1-based line/column,
        // endColumn exclusive — both describe the same span. Split only on '\n'
        // so a kept '\r' still counts toward the line's length: the document may
        // be checked out CRLF on some platforms, and the index reports offsets
        // into the verbatim text.
        var lines = text.Split('\n');
        var start = 0;
        for (var i = 0; i < trigger.Line!.Value - 1; i++)
        {
            start += lines[i].Length + 1;
        }

        start += trigger.Column!.Value - 1;
        var end = 0;
        for (var i = 0; i < trigger.EndLine!.Value - 1; i++)
        {
            end += lines[i].Length + 1;
        }

        end += trigger.EndColumn!.Value - 1;
        Assert.Equal(trigger.Start!.Value, start);
        Assert.Equal(trigger.Start.Value + trigger.Length!.Value, end);
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
            // best-effort cleanup
        }
    }
}
