using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

/// <summary>
/// Deterministic loader for MCP seam tests — no MSBuild.
/// </summary>
public sealed class FakeSolutionLoader : ISolutionLoader
{
    private readonly TimeSpan _delay;
    private readonly Func<LoadedSolution> _factory;

    public FakeSolutionLoader(TimeSpan delay, Func<LoadedSolution> factory)
    {
        _delay = delay;
        _factory = factory;
    }

    public static FakeSolutionLoader ImmediateMultiTfm(string projectFilePath = @"C:\fake\Widget.csproj") =>
        new(TimeSpan.Zero, () => CreateMultiTfmLoaded(projectFilePath));

    public static FakeSolutionLoader DelayedMultiTfm(TimeSpan delay, string projectFilePath = @"C:\fake\Widget.csproj") =>
        new(delay, () => CreateMultiTfmLoaded(projectFilePath));

    public static FakeSolutionLoader ImmediateWithSymbols(string projectFilePath = @"C:\fake\SampleLib.csproj") =>
        new(TimeSpan.Zero, () => CreateSymbolsLoaded(projectFilePath));

    public static FakeSolutionLoader DelayedWithSymbols(
        TimeSpan delay,
        string projectFilePath = @"C:\fake\SampleLib.csproj") =>
        new(delay, () => CreateSymbolsLoaded(projectFilePath));

    public static FakeSolutionLoader ImmediateWithFindRefsGraph(string root = @"C:\fake") =>
        new(TimeSpan.Zero, () => CreateFindRefsGraphLoaded(root));

    public static FakeSolutionLoader ImmediateWithDiagnostics(string projectFilePath = @"C:\fake\BrokenLib.csproj") =>
        new(TimeSpan.Zero, () => CreateDiagnosticsLoaded(projectFilePath));

    public static FakeSolutionLoader DelayedWithDiagnostics(
        TimeSpan delay,
        string projectFilePath = @"C:\fake\BrokenLib.csproj") =>
        new(delay, () => CreateDiagnosticsLoaded(projectFilePath));

    public static FakeSolutionLoader ImmediateWithGenerators(
        string projectFilePath = @"C:\fake\GeneratorHost.csproj") =>
        new(TimeSpan.Zero, () => CreateGeneratorsLoaded(projectFilePath));

    public static FakeSolutionLoader DelayedWithGenerators(
        TimeSpan delay,
        string projectFilePath = @"C:\fake\GeneratorHost.csproj") =>
        new(delay, () => CreateGeneratorsLoaded(projectFilePath));

    public async Task<LoadedSolution> OpenAsync(
        string path,
        IProgress<LoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new LoadProgress(0, 2));
        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new LoadProgress(1, 2));
        var loaded = _factory();
        progress?.Report(new LoadProgress(2, 2));
        return loaded;
    }

    public static LoadedSolution CreateMultiTfmLoaded(string projectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;

        solution = AddEmptyProject(solution, "Widget(net8.0)", projectFilePath);
        solution = AddEmptyProject(solution, "Widget(net9.0)", projectFilePath);

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace changes.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static LoadedSolution CreateSymbolsLoaded(string projectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var handwrittenId = DocumentId.CreateNewId(projectId);
        var generatedId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "SampleLib",
            "SampleLib",
            LanguageNames.CSharp,
            filePath: projectFilePath));

        // Enough members for pagination (limit=2) plus overloads.
        const string handwritten = """
            namespace SampleLib;

            public partial class Calculator
            {
                public int Mode { get; set; }
                public string Name { get; set; } = "calc";
                public int Add(int a, int b) => a + b;
                public double Add(double a, double b) => a + b;
                public int Subtract(int a, int b) => a - b;
                public int Multiply(int a, int b) => a * b;
                public int Divide(int a, int b) => a / b;
                public void Clear() { Mode = 0; }
                public void Reset() { Name = "calc"; Mode = 0; }
            }
            """;

        // Path ends with .g.cs so origin can be labeled SourceGenerated without a real SourceGeneratedDocument.
        const string generated = """
            namespace SampleLib;

            public partial class Calculator
            {
                public int GeneratedAnswer => 42;
            }
            """;

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        var handwrittenPath = Path.Combine(projectDir, "Calculator.cs");
        var generatedPath = Path.Combine(projectDir, "Generated", "FakeGen", "Calculator.Generated.g.cs");

        solution = solution.AddDocument(
            handwrittenId,
            "Calculator.cs",
            SourceText.From(handwritten),
            filePath: handwrittenPath);
        solution = solution.AddDocument(
            generatedId,
            "Calculator.Generated.g.cs",
            SourceText.From(generated),
            filePath: generatedPath);
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Reference mscorlib/runtime so GetCompilationAsync yields usable symbols.
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace symbol fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    /// <summary>
    /// Adhoc project with CustomGenerator attached via AnalyzerFileReference (public GetGenerators path).
    /// </summary>
    public static LoadedSolution CreateGeneratorsLoaded(string projectFilePath = @"C:\fake\GeneratorHost.csproj")
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "GeneratorHost",
            "GeneratorHost",
            LanguageNames.CSharp,
            filePath: projectFilePath));

        const string source = """
            namespace GeneratorHost;

            public static class Host
            {
                public static string Name => "host";
            }
            """;

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        var filePath = Path.Combine(projectDir, "Host.cs");

        solution = solution.AddDocument(
            docId,
            "Host.cs",
            SourceText.From(source),
            filePath: filePath);
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        var generatorAssemblyPath = typeof(CustomGenerator.MarkerGenerator).Assembly.Location;
        solution = solution.AddAnalyzerReference(
            projectId,
            new AnalyzerFileReference(generatorAssemblyPath, TestAnalyzerAssemblyLoader.Instance));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace generators fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    /// <summary>
    /// Minimal public <see cref="IAnalyzerAssemblyLoader"/> for Adhoc fixtures (Roslyn's concrete loader is non-public).
    /// </summary>
    private sealed class TestAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
    {
        public static TestAnalyzerAssemblyLoader Instance { get; } = new();

        public void AddDependencyLocation(string fullPath)
        {
        }

        public System.Reflection.Assembly LoadFromPath(string fullPath) =>
            System.Reflection.Assembly.LoadFrom(fullPath);
    }

    /// <summary>
    /// Deterministic Error + Warning diagnostics for project_diagnostics pagination tests.
    /// </summary>
    public static LoadedSolution CreateDiagnosticsLoaded(string projectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "BrokenLib",
            "BrokenLib",
            LanguageNames.CSharp,
            filePath: projectFilePath));

        // Multiple #warning + type errors → enough Error/Warning rows for limit=2 paging.
        const string source = """
            #warning DiagWarnA
            #warning DiagWarnB
            #warning DiagWarnC
            namespace BrokenLib;

            public class Broken
            {
                public int Alpha = "not-an-int";
                public int Beta = "also-bad";
            }
            """;

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        var filePath = Path.Combine(projectDir, "Broken.cs");

        solution = solution.AddDocument(
            docId,
            "Broken.cs",
            SourceText.From(source),
            filePath: filePath);
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace diagnostics fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    /// <summary>
    /// LibA defines Marker (multiple local refs). LibB and Outsider reference LibA and use Marker.
    /// Dependency closure of LibA is LibA only (no outgoing project refs); consumers need entireSolution.
    /// </summary>
    public static LoadedSolution CreateFindRefsGraphLoaded(string root = @"C:\fake")
    {
        var workspace = new AdhocWorkspace();
        var libAId = ProjectId.CreateNewId();
        var libBId = ProjectId.CreateNewId();
        var outsiderId = ProjectId.CreateNewId();
        var libADoc = DocumentId.CreateNewId(libAId);
        var libADoc2 = DocumentId.CreateNewId(libAId);
        var libBDoc = DocumentId.CreateNewId(libBId);
        var outsiderDoc = DocumentId.CreateNewId(outsiderId);

        var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var solution = workspace.CurrentSolution;

        solution = solution.AddProject(ProjectInfo.Create(
            libAId,
            VersionStamp.Create(),
            "LibA",
            "LibA",
            LanguageNames.CSharp,
            filePath: Path.Combine(root, "LibA", "LibA.csproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            libBId,
            VersionStamp.Create(),
            "LibB",
            "LibB",
            LanguageNames.CSharp,
            filePath: Path.Combine(root, "LibB", "LibB.csproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            outsiderId,
            VersionStamp.Create(),
            "Outsider",
            "Outsider",
            LanguageNames.CSharp,
            filePath: Path.Combine(root, "Outsider", "Outsider.csproj")));

        const string markerSource = """
            namespace LibA;

            public class Marker
            {
                public static int Value => 1;
                public static int Twice => Value + Value;
            }
            """;

        const string moreUses = """
            namespace LibA;

            public static class LocalUses
            {
                public static int Read() => Marker.Value;
            }
            """;

        const string libBSource = """
            namespace LibB;
            using LibA;

            public static class Consumer
            {
                public static int Use() => Marker.Value;
            }
            """;

        const string outsiderSource = """
            namespace Outsider;
            using LibA;

            public static class OutsideConsumer
            {
                public static int Use() => Marker.Value;
            }
            """;

        solution = solution.AddDocument(
            libADoc,
            "Marker.cs",
            SourceText.From(markerSource),
            filePath: Path.Combine(root, "LibA", "Marker.cs"));
        solution = solution.AddDocument(
            libADoc2,
            "LocalUses.cs",
            SourceText.From(moreUses),
            filePath: Path.Combine(root, "LibA", "LocalUses.cs"));
        solution = solution.AddDocument(
            libBDoc,
            "Consumer.cs",
            SourceText.From(libBSource),
            filePath: Path.Combine(root, "LibB", "Consumer.cs"));
        solution = solution.AddDocument(
            outsiderDoc,
            "OutsideConsumer.cs",
            SourceText.From(outsiderSource),
            filePath: Path.Combine(root, "Outsider", "OutsideConsumer.cs"));

        solution = solution.AddProjectReference(libBId, new ProjectReference(libAId));
        solution = solution.AddProjectReference(outsiderId, new ProjectReference(libAId));

        solution = solution.WithProjectCompilationOptions(
            libAId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.WithProjectCompilationOptions(
            libBId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.WithProjectCompilationOptions(
            outsiderId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        solution = solution.AddMetadataReference(libAId, mscorlib);
        solution = solution.AddMetadataReference(libBId, mscorlib);
        solution = solution.AddMetadataReference(outsiderId, mscorlib);

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace find-refs fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    private static Solution AddEmptyProject(Solution solution, string name, string filePath)
    {
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        solution = solution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            filePath: filePath));

        solution = solution.AddDocument(
            docId,
            "Placeholder.cs",
            SourceText.From("// placeholder"));

        return solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
