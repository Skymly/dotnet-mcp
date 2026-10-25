using DotNetMcp.Core;
using DotNetMcp.FSharp;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

/// <summary>
/// symbol_source seam tests (#520): verbatim declaration text for C# / VB / F#
/// handles, source-generated declarations (the only way to read generated
/// code), line-boundary truncation, and the unified 1-based position quad that
/// bounds the text actually returned.
/// </summary>
public class SymbolSourceSeamTests
{
    [Fact]
    public async Task csharp_method_source_includes_xml_doc_and_attributes_verbatim()
    {
        const string source =
            "namespace DocLib;\n" +
            "\n" +
            "public class Widget\n" +
            "{\n" +
            "    /// <summary>Sums two ints.</summary>\n" +
            "    /// <param name=\"a\">Left.</param>\n" +
            "    [System.Obsolete(\"use Add2\")]\n" +
            "    public int Add(int a, int b) => a + b;\n" +
            "}\n";

        using var session = SessionFor(source, out var languages);
        var handle = await ResolveHandleAsync(session, languages, "DocLib.Widget.Add");
        var (success, error) = await languages.GetSourceAsync(session, handle, maxChars: null);
        Assert.Null(error);

        var decl = Assert.Single(success!.Declarations);
        Assert.Equal(SymbolOrigin.Handwritten, decl.Origin);
        Assert.Null(decl.Generator);
        const string expected =
            "/// <summary>Sums two ints.</summary>\n" +
            "    /// <param name=\"a\">Left.</param>\n" +
            "    [System.Obsolete(\"use Add2\")]\n" +
            "    public int Add(int a, int b) => a + b;";
        Assert.Equal(expected, decl.Text);
        Assert.False(decl.Truncated);
        Assert.False(success.Truncated);
        Assert.Null(success.Message);
        AssertQuadBoundsText(source, decl);
    }

    [Fact]
    public async Task csharp_field_source_covers_the_whole_multi_declarator_statement()
    {
        const string source =
            "namespace DocLib;\n" +
            "\n" +
            "public class Fields\n" +
            "{\n" +
            "    public int Alpha = 1, Beta = 2;\n" +
            "}\n";

        using var session = SessionFor(source, out var languages);
        var handle = await ResolveHandleAsync(session, languages, "DocLib.Fields.Beta");
        var (success, error) = await languages.GetSourceAsync(session, handle, maxChars: null);
        Assert.Null(error);

        var decl = Assert.Single(success!.Declarations);
        Assert.Equal("public int Alpha = 1, Beta = 2;", decl.Text);
        AssertQuadBoundsText(source, decl);
    }

    [Fact]
    public async Task csharp_property_source_includes_both_accessors()
    {
        const string source =
            "namespace DocLib;\n" +
            "\n" +
            "public class Counter\n" +
            "{\n" +
            "    private int _count;\n" +
            "    public int Count\n" +
            "    {\n" +
            "        get { return _count; }\n" +
            "        set { _count = value; }\n" +
            "    }\n" +
            "}\n";

        using var session = SessionFor(source, out var languages);
        var handle = await ResolveHandleAsync(session, languages, "DocLib.Counter.Count");
        var (success, error) = await languages.GetSourceAsync(session, handle, maxChars: null);
        Assert.Null(error);

        var decl = Assert.Single(success!.Declarations);
        Assert.StartsWith("public int Count", decl.Text);
        Assert.Contains("get { return _count; }", decl.Text);
        Assert.Contains("set { _count = value; }", decl.Text);
        Assert.True(decl.Text.TrimEnd().EndsWith('}'), "property slice must end after the closing brace");
        AssertQuadBoundsText(source, decl);
    }

    [Fact]
    public async Task csharp_type_source_covers_the_whole_declaration()
    {
        const string source =
            "namespace DocLib;\n" +
            "\n" +
            "public class Widget\n" +
            "{\n" +
            "    public int Add(int a, int b) => a + b;\n" +
            "}\n" +
            "\n" +
            "public class Other { }\n";

        using var session = SessionFor(source, out var languages);
        var handle = await ResolveHandleAsync(session, languages, "DocLib.Widget");
        var (success, error) = await languages.GetSourceAsync(session, handle, maxChars: null);
        Assert.Null(error);

        var decl = Assert.Single(success!.Declarations);
        Assert.Equal(
            "public class Widget\n{\n    public int Add(int a, int b) => a + b;\n}",
            decl.Text);
        AssertQuadBoundsText(source, decl);
    }

    [Fact]
    public async Task partial_type_lists_handwritten_declaration_before_generated()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded(@"C:\fake\GeneratorHost.csproj");
        var languages = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        using var session = new WorkspaceSession(loaded, epoch: 1);

        var handle = await ResolveHandleAsync(session, languages, "GeneratorHost.PartialThing");
        var (success, error) = await languages.GetSourceAsync(session, handle, maxChars: null);
        Assert.Null(error);

        Assert.Equal(2, success!.Declarations.Count);
        var handwritten = success.Declarations[0];
        var generated = success.Declarations[1];

        Assert.Equal(SymbolOrigin.Handwritten, handwritten.Origin);
        Assert.Null(handwritten.Generator);
        Assert.Contains("public string Format() => \"hw\";", handwritten.Text);
        Assert.Contains(".cs", handwritten.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".g.", handwritten.FilePath, StringComparison.OrdinalIgnoreCase);

        Assert.StartsWith("SourceGenerator(", generated.Origin, StringComparison.Ordinal);
        Assert.NotNull(generated.Generator);
        Assert.Equal("CustomGenerator", generated.Generator!.AssemblyName);
        Assert.Equal("CustomGenerator.PartialAugmentGenerator", generated.Generator.TypeFullName);
        Assert.Equal("1.2.3.0", generated.Generator.Version);
        Assert.Contains("public int GeneratedValue => 99;", generated.Text);
    }

    [Fact]
    public async Task generated_member_source_comes_from_generated_text_not_disk()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded(@"C:\fake\GeneratorHost.csproj");
        var languages = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        using var session = new WorkspaceSession(loaded, epoch: 1);

        var handle = await ResolveHandleAsync(session, languages, "GeneratorHost.PartialThing.GeneratedValue");
        var (success, error) = await languages.GetSourceAsync(session, handle, maxChars: null);
        Assert.Null(error);

        var decl = Assert.Single(success!.Declarations);
        Assert.StartsWith("SourceGenerator(", decl.Origin, StringComparison.Ordinal);
        Assert.Equal("public int GeneratedValue => 99;", decl.Text);
        // Generated code never lands on disk — host file tools cannot read it;
        // this slice is the only way to read the generated declaration.
        Assert.False(File.Exists(decl.FilePath), $"generated path should not exist on disk: {decl.FilePath}");
    }

    [Fact]
    public async Task generated_type_source_reports_generator_identity()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded(@"C:\fake\GeneratorHost.csproj");
        var languages = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        using var session = new WorkspaceSession(loaded, epoch: 1);

        var handle = await ResolveHandleAsync(session, languages, "SampleApp.Generated.CustomMarker");
        var (success, error) = await languages.GetSourceAsync(session, handle, maxChars: null);
        Assert.Null(error);

        var decl = Assert.Single(success!.Declarations);
        Assert.StartsWith("SourceGenerator(CustomGenerator::CustomGenerator.MarkerGenerator@", decl.Origin, StringComparison.Ordinal);
        Assert.Equal("CustomGenerator.MarkerGenerator", decl.Generator!.TypeFullName);
        Assert.Contains("public static string Who => \"CustomGenerator.MarkerGenerator\";", decl.Text);
    }

    [Fact]
    public async Task truncation_cuts_at_a_line_boundary_and_reports_where_text_stopped()
    {
        const string source =
            "namespace DocLib;\n" +
            "\n" +
            "public class Widget\n" +
            "{\n" +
            "    /// <summary>Sums two ints.</summary>\n" +
            "    /// <param name=\"a\">Left.</param>\n" +
            "    public int Add(int a, int b) => a + b;\n" +
            "}\n";

        using var session = SessionFor(source, out var languages);
        var handle = await ResolveHandleAsync(session, languages, "DocLib.Widget.Add");

        // Budget covers the first doc line and its newline but not line two.
        const int maxChars = 40;
        var (success, error) = await languages.GetSourceAsync(session, handle, maxChars);
        Assert.Null(error);

        var decl = Assert.Single(success!.Declarations);
        Assert.True(decl.Truncated);
        Assert.True(success.Truncated);
        Assert.NotNull(success.Message);
        Assert.Equal("/// <summary>Sums two ints.</summary>\n", decl.Text);
        Assert.True(decl.Text.Length <= maxChars);
        // The truncated quad bounds the text actually returned: the end marks
        // the cut point (start of the next line), not the full declaration.
        AssertQuadBoundsText(source, decl);
    }

    [Fact]
    public async Task later_declarations_still_list_with_empty_text_after_budget()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "PartLib",
            "PartLib",
            LanguageNames.CSharp,
            filePath: @"C:\fake\PartLib.csproj"));
        const string partA =
            "namespace PartLib;\n" +
            "\n" +
            "public partial class Big\n" +
            "{\n" +
            "    public int First() => 1;\n" +
            "    public int Second() => 2;\n" +
            "}\n";
        const string partB =
            "namespace PartLib;\n" +
            "\n" +
            "public partial class Big\n" +
            "{\n" +
            "    public int Third() => 3;\n" +
            "}\n";
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId), "A.cs", SourceText.From(partA), filePath: @"C:\fake\PartLib\A.cs");
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId), "B.cs", SourceText.From(partB), filePath: @"C:\fake\PartLib\B.cs");
        solution = solution.WithProjectCompilationOptions(
            projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        Assert.True(workspace.TryApplyChanges(solution));
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);

        var languages = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        using var session = new WorkspaceSession(loaded, epoch: 1);

        var handle = await ResolveHandleAsync(session, languages, "PartLib.Big");
        // Budget covers only a prefix of the A.cs declaration.
        var (success, error) = await languages.GetSourceAsync(session, handle, maxChars: 60);
        Assert.Null(error);

        Assert.Equal(2, success!.Declarations.Count);
        var first = success.Declarations[0];
        var second = success.Declarations[1];
        Assert.True(first.Truncated);
        Assert.True(first.Text.Length <= 60);
        Assert.EndsWith("\n", first.Text);
        Assert.Equal(string.Empty, second.Text);
        Assert.True(second.Truncated);
        Assert.True(success.Truncated);
        // A truncated type points the caller at per-member reads.
        Assert.Contains("symbol_members", success.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task metadata_only_symbol_returns_source_unavailable_over_mcp()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithComInterop());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveHandleAsync(fx, "InteropLib.IMetaCom");
            var result = await fx.Client.CallToolAsync(
                "symbol_source",
                new Dictionary<string, object?> { ["handle"] = handle });
            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.SourceUnavailable, body.Error);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task nonpositive_maxchars_falls_back_to_default()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithSymbols());
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);

            var handle = await ResolveHandleAsync(fx, "SampleLib.Calculator.Mode");
            var baseline = await SourceAsync(fx, handle, maxChars: null);
            var zero = await SourceAsync(fx, handle, maxChars: 0);
            var negative = await SourceAsync(fx, handle, maxChars: -7);

            Assert.Equal(baseline.Declarations.Select(d => d.Text), zero.Declarations.Select(d => d.Text));
            Assert.Equal(baseline.Declarations.Select(d => d.Text), negative.Declarations.Select(d => d.Text));
            Assert.False(baseline.Truncated);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task vb_method_source_returns_the_whole_block_verbatim()
    {
        var root = CreateTempDir("vb");
        try
        {
            var loaded = FakeSolutionLoader.CreateVbSymbolsLoaded(root);
            var languages = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
            using var session = new WorkspaceSession(loaded, epoch: 1);

            var handle = await ResolveHandleAsync(session, languages, "VbLib.Widget.Echo");
            var (success, error) = await languages.GetSourceAsync(session, handle, maxChars: null);
            Assert.Null(error);

            var decl = Assert.Single(success!.Declarations);
            Assert.Equal(SymbolOrigin.Handwritten, decl.Origin);
            Assert.StartsWith("Public Function Echo(text As String) As String", decl.Text.TrimStart());
            Assert.Contains("Return text", decl.Text);
            Assert.EndsWith("End Function", decl.Text.TrimEnd());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task vb_type_source_covers_the_whole_class_block()
    {
        var root = CreateTempDir("vb");
        try
        {
            var loaded = FakeSolutionLoader.CreateVbSymbolsLoaded(root);
            var languages = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
            using var session = new WorkspaceSession(loaded, epoch: 1);

            var handle = await ResolveHandleAsync(session, languages, "VbLib.Widget");
            var (success, error) = await languages.GetSourceAsync(session, handle, maxChars: null);
            Assert.Null(error);

            var decl = Assert.Single(success!.Declarations);
            Assert.StartsWith("Public Class Widget", decl.Text.TrimStart());
            Assert.Contains("Public Function Ping() As String Implements IPingable.Ping", decl.Text);
            Assert.EndsWith("End Class", decl.Text.TrimEnd());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task fsharp_function_source_returns_the_binding()
    {
        const string fsSource =
            "module FsLib.Widget\n" +
            "\n" +
            "let ping () = \"fs\"\n" +
            "\n" +
            "let pingCaller () = ping ()\n";

        using var session = FsharpSessionFor(fsSource, "Widget.fs");
        var service = new FSharpSymbolQueryService();
        var (resolved, resolveError) = await service.ResolveByNameAsync(session, "FsLib.Widget.ping");
        Assert.Null(resolveError);

        var (success, error) = await service.GetSourceAsync(session, resolved!.Handle, maxChars: 60_000);
        Assert.Null(error);
        var decl = Assert.Single(success!.Declarations);
        Assert.Equal(SymbolOrigin.Handwritten, decl.Origin);
        Assert.Equal("let ping () = \"fs\"", decl.Text);
        AssertQuadBoundsText(fsSource, decl);
    }

    [Fact]
    public async Task fsharp_type_source_expands_to_members_and_attaches_doc_and_attributes()
    {
        const string fsSource =
            "module FsLib.Widget\n" +
            "\n" +
            "/// A pingable contract.\n" +
            "[<System.Obsolete(\"old\")>]\n" +
            "type OldContract =\n" +
            "    abstract member Ping: unit -> string\n" +
            "\n" +
            "type Other() =\n" +
            "    member _.Tag = \"t\"\n";

        using var session = FsharpSessionFor(fsSource, "Widget.fs");
        var service = new FSharpSymbolQueryService();
        var (resolved, resolveError) = await service.ResolveByNameAsync(session, "FsLib.Widget.OldContract");
        Assert.Null(resolveError);

        var (success, error) = await service.GetSourceAsync(session, resolved!.Handle, maxChars: 60_000);
        Assert.Null(error);
        var decl = Assert.Single(success!.Declarations);
        Assert.Equal(
            "/// A pingable contract.\n" +
            "[<System.Obsolete(\"old\")>]\n" +
            "type OldContract =\n" +
            "    abstract member Ping: unit -> string",
            decl.Text);
        AssertQuadBoundsText(fsSource, decl);
    }

    [Fact]
    public async Task fsharp_type_source_stops_before_the_next_top_level_declaration()
    {
        const string fsSource =
            "module FsLib.Widget\n" +
            "\n" +
            "type IPingable =\n" +
            "    abstract member Ping: unit -> string\n" +
            "\n" +
            "type PingWidget() =\n" +
            "    interface IPingable with\n" +
            "        member _.Ping() = \"ok\"\n";

        using var session = FsharpSessionFor(fsSource, "Widget.fs");
        var service = new FSharpSymbolQueryService();
        var (resolved, resolveError) = await service.ResolveByNameAsync(session, "FsLib.Widget.PingWidget");
        Assert.Null(resolveError);

        var (success, error) = await service.GetSourceAsync(session, resolved!.Handle, maxChars: 60_000);
        Assert.Null(error);
        var decl = Assert.Single(success!.Declarations);
        Assert.Equal(
            "type PingWidget() =\n" +
            "    interface IPingable with\n" +
            "        member _.Ping() = \"ok\"",
            decl.Text);
        AssertQuadBoundsText(fsSource, decl);
    }

    private static async Task<SymbolSourceResultDto> SourceAsync(
        InProcessMcpFixture fx, string handle, int? maxChars)
    {
        var args = new Dictionary<string, object?> { ["handle"] = handle };
        if (maxChars is not null)
        {
            args["maxChars"] = maxChars.Value;
        }

        var result = await fx.Client.CallToolAsync("symbol_source", args);
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<SymbolSourceResultDto>(result);
    }

    private static async Task<string> ResolveHandleAsync(InProcessMcpFixture fx, string name)
    {
        var result = await fx.Client.CallToolAsync(
            "symbol_resolve",
            new Dictionary<string, object?> { ["name"] = name });
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        return InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(result).Handle;
    }

    private static async Task<string> ResolveHandleAsync(
        IWorkspaceSession session, LanguageAdapters languages, string name)
    {
        var (success, error) = await languages.ResolveByNameAsync(session, name);
        Assert.Null(error);
        return success!.Handle;
    }

    /// <summary>
    /// Invariant shared with UnifiedPositionSeamTests: the quad bounds the text
    /// actually returned — OffsetOf(Line, Column) is the slice start and
    /// OffsetOf(EndLine, EndColumn) the end, with Text occupying exactly that span.
    /// </summary>
    private static void AssertQuadBoundsText(string source, SymbolSourceDeclaration decl)
    {
        var start = OffsetOf(source, decl.Line, decl.Column);
        var end = OffsetOf(source, decl.EndLine, decl.EndColumn);
        Assert.Equal(decl.Text, source.Substring(start, end - start));
    }

    private static int OffsetOf(string text, int line, int column)
    {
        var lineStart = 0;
        for (var current = 1; current < line; current++)
        {
            var next = text.IndexOf('\n', lineStart);
            Assert.True(next >= 0, $"text has no line {line}.");
            lineStart = next + 1;
        }

        return lineStart + column - 1;
    }

    private static WorkspaceSession SessionFor(string source, out LanguageAdapters languages)
    {
        var loaded = CreateSingleDocumentLoaded("DocLib", "Doc.cs", source);
        languages = new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())]);
        return new WorkspaceSession(loaded, epoch: 1);
    }

    private static WorkspaceSession FsharpSessionFor(string source, string documentName)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var fsDir = @"C:\fake\FsLib";
        var filePath = Path.Combine(fsDir, documentName);
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "FsLib",
            "FsLib",
            LanguageNames.FSharp,
            filePath: Path.Combine(fsDir, "FsLib.fsproj")));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId), documentName, SourceText.From(source), filePath: filePath);
        Assert.True(workspace.TryApplyChanges(solution));
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var snapshot = new FSharpWorkspaceSnapshot(1,
        [
            new FSharpProjectSnapshot(
                projectId.Id.ToString("D"),
                "FsLib",
                Path.Combine(fsDir, "FsLib.fsproj"),
                [new FSharpDocumentSnapshot(filePath, source)])
        ]);
        return new WorkspaceSession(loaded, epoch: 1, fsharpSnapshot: snapshot);
    }

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
        Assert.True(workspace.TryApplyChanges(solution));
        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-src-{label}-{Guid.NewGuid():N}");
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
