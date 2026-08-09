using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
