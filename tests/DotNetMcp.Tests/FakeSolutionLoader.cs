using DotNetMcp.Server;
using DotNetMcp.FSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.VisualBasic;
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

    /// <summary>
    /// Writes handwritten source to disk under <paramref name="projectDir"/> so FSW/drift tests can mutate files.
    /// </summary>
    public static FakeSolutionLoader ImmediateWithSymbolsOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateSymbolsLoadedOnDisk(projectDir));

    public static FakeSolutionLoader DelayedWithSymbols(
        TimeSpan delay,
        string projectFilePath = @"C:\fake\SampleLib.csproj") =>
        new(delay, () => CreateSymbolsLoaded(projectFilePath));

    public static FakeSolutionLoader ImmediateWithFindRefsGraph(string root = @"C:\fake") =>
        new(TimeSpan.Zero, () => CreateFindRefsGraphLoaded(root));

    public static FakeSolutionLoader ImmediateWithFsharpDiagnostics(
        string projectFilePath = @"C:\fake\BrokenFs.fsproj") =>
        new(TimeSpan.Zero, () => CreateFsharpDiagnosticsLoaded(projectFilePath));

    public static LoadedSolution CreateFsharpDiagnosticsLoaded(string projectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var projectDir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-broken-fs");
        var filePath = Path.Combine(projectDir, "Broken.fs");
        const string source = """
            module Broken

            let alpha: int = "not-an-int"
            let beta: int = "also-bad"
            let gamma: int = "still-bad"
            """;

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "BrokenFs",
            "BrokenFs",
            LanguageNames.FSharp,
            filePath: projectFilePath));
        solution = solution.AddDocument(docId, "Broken.fs", SourceText.From(source), filePath: filePath);

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace F# diagnostics fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithVbDiagnostics(
        string projectFilePath = @"C:\fake\BrokenVb.vbproj") =>
        new(TimeSpan.Zero, () => CreateVbDiagnosticsLoaded(projectFilePath));

    public static FakeSolutionLoader ImmediateWithDiagnostics(string projectFilePath = @"C:\fake\BrokenLib.csproj") =>
        new(TimeSpan.Zero, () => CreateDiagnosticsLoaded(projectFilePath));

    public static FakeSolutionLoader DelayedWithDiagnostics(
        TimeSpan delay,
        string projectFilePath = @"C:\fake\BrokenLib.csproj") =>
        new(delay, () => CreateDiagnosticsLoaded(projectFilePath));

    public static FakeSolutionLoader ImmediateWithVbAndCSharp(
        string csharpProjectFilePath = @"C:\fake\CsLib.csproj",
        string vbProjectFilePath = @"C:\fake\VbLib.vbproj") =>
        new(TimeSpan.Zero, () => CreateVbAndCSharpLoaded(csharpProjectFilePath, vbProjectFilePath));

    public static LoadedSolution CreateVbAndCSharpLoaded(string csharpProjectFilePath, string vbProjectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;

        solution = AddEmptyProject(solution, "CsLib", csharpProjectFilePath);
        solution = AddEmptyVbProject(solution, "VbLib", vbProjectFilePath);

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace VB+C# fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithFsharpSymbols(string root = @"C:\fake") =>
        new(TimeSpan.Zero, () => CreateFsharpSymbolsLoaded(root));

    public static LoadedSolution CreateFsharpSymbolsLoaded(string root)
    {
        var workspace = new AdhocWorkspace();
        var fsId = ProjectId.CreateNewId();
        var csId = ProjectId.CreateNewId();
        var widgetDoc = DocumentId.CreateNewId(fsId);
        var usesDoc = DocumentId.CreateNewId(fsId);
        var callerDoc = DocumentId.CreateNewId(csId);

        var fsDir = Path.Combine(root, "FsLib");
        var csDir = Path.Combine(root, "CsLib");
        Directory.CreateDirectory(fsDir);
        Directory.CreateDirectory(csDir);
        var widgetPath = Path.Combine(fsDir, "Widget.fs");
        var usesPath = Path.Combine(fsDir, "Uses.fs");
        var callerPath = Path.Combine(csDir, "Caller.cs");

        const string widgetSource = """
            module FsLib.Widget

            let ping () = "fs"

            type IPingable =
                abstract member Ping: unit -> string

            type PingWidget() =
                interface IPingable with
                    member _.Ping() = "ok"

            type SpecialPingWidget() =
                inherit PingWidget()
            """;
        const string usesSource = """
            module FsLib.Uses

            let go () = Widget.ping()
            """;
        const string callerSource = """
            namespace CsLib;
            public static class Caller
            {
                public static string Hi() => "cs";
                public static string UseFs() => FsLib.Widget.ping();
            }
            """;

        File.WriteAllText(widgetPath, widgetSource);
        File.WriteAllText(usesPath, usesSource);
        File.WriteAllText(callerPath, callerSource);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            fsId,
            VersionStamp.Create(),
            "FsLib",
            "FsLib",
            LanguageNames.FSharp,
            filePath: Path.Combine(fsDir, "FsLib.fsproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            csId,
            VersionStamp.Create(),
            "CsLib",
            "CsLib",
            LanguageNames.CSharp,
            filePath: Path.Combine(csDir, "CsLib.csproj")));
        solution = solution.AddDocument(widgetDoc, "Widget.fs", SourceText.From(widgetSource), filePath: widgetPath);
        solution = solution.AddDocument(usesDoc, "Uses.fs", SourceText.From(usesSource), filePath: usesPath);
        solution = solution.AddProjectReference(fsId, new ProjectReference(csId));
        solution = solution.AddDocument(callerDoc, "Caller.cs", SourceText.From(callerSource), filePath: callerPath);
        solution = solution.WithProjectCompilationOptions(
            csId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        try
        {
            var fsDll = Path.Combine(fsDir, "FsLib.dll");
            FSharpSymbolQueryService.CompileLibrary(fsDll, [widgetPath, usesPath]);
            solution = solution.AddMetadataReference(csId, MetadataReference.CreateFromFile(fsDll));
        }
        catch (Exception)
        {
            // Mixed C# metadata refs are best-effort; F# source analysis still works.
        }
        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(csId, metadata);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace F# symbols fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithFsharpAndCSharp(
        string csharpProjectFilePath = @"C:\fake\CsLib.csproj",
        string fsharpProjectFilePath = @"C:\fake\FsLib.fsproj") =>
        new(TimeSpan.Zero, () => CreateFsharpAndCSharpLoaded(csharpProjectFilePath, fsharpProjectFilePath));

    public static LoadedSolution CreateFsharpAndCSharpLoaded(string csharpProjectFilePath, string fsharpProjectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;

        solution = AddEmptyProject(solution, "CsLib", csharpProjectFilePath);
        solution = AddEmptyFsharpProject(solution, "FsLib", fsharpProjectFilePath);

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace F#+C# fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithVbSymbols(
        string root = @"C:\fake") =>
        new(TimeSpan.Zero, () => CreateVbSymbolsLoaded(root));

    public static LoadedSolution CreateVbSymbolsLoaded(string root)
    {
        var workspace = new AdhocWorkspace();
        var vbId = ProjectId.CreateNewId();
        var csId = ProjectId.CreateNewId();
        var widgetDoc = DocumentId.CreateNewId(vbId);
        var usesDoc = DocumentId.CreateNewId(vbId);
        var callerDoc = DocumentId.CreateNewId(csId);
        var refs = TrustedPlatformReferences();

        var solution = workspace.CurrentSolution;
        solution = solution.AddProject(ProjectInfo.Create(
            vbId,
            VersionStamp.Create(),
            "VbLib",
            "VbLib",
            LanguageNames.VisualBasic,
            filePath: Path.Combine(root, "VbLib", "VbLib.vbproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            csId,
            VersionStamp.Create(),
            "CsLib",
            "CsLib",
            LanguageNames.CSharp,
            filePath: Path.Combine(root, "CsLib", "CsLib.csproj")));

        const string widgetSource = """
            Namespace VbLib
                Public Interface IPingable
                    Function Ping() As String
                End Interface

                Public Class Widget
                    Implements IPingable

                    Public Function Ping() As String Implements IPingable.Ping
                        Return "vb"
                    End Function

                    Public Function Echo(text As String) As String
                        Return text
                    End Function
                End Class

                Public Class SpecialWidget
                    Inherits Widget
                End Class
            End Namespace
            """;

        const string usesSource = """
            Namespace VbLib
                Public Class Uses
                    Public Shared Function CallPing() As String
                        Return New Widget().Ping()
                    End Function
                End Class
            End Namespace
            """;

        const string callerSource = """
            namespace CsLib;

            public static class Caller
            {
                public static string Use() => new VbLib.Widget().Ping();
            }
            """;

        solution = solution.AddDocument(
            widgetDoc,
            "Widget.vb",
            SourceText.From(widgetSource),
            filePath: Path.Combine(root, "VbLib", "Widget.vb"));
        solution = solution.AddDocument(
            usesDoc,
            "Uses.vb",
            SourceText.From(usesSource),
            filePath: Path.Combine(root, "VbLib", "Uses.vb"));
        solution = solution.AddDocument(
            callerDoc,
            "Caller.cs",
            SourceText.From(callerSource),
            filePath: Path.Combine(root, "CsLib", "Caller.cs"));

        solution = solution.AddProjectReference(csId, new ProjectReference(vbId));
        solution = solution.WithProjectCompilationOptions(
            vbId, new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.WithProjectCompilationOptions(
            csId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in refs)
        {
            solution = solution.AddMetadataReference(vbId, metadata);
            solution = solution.AddMetadataReference(csId, metadata);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace VB symbols fixture.");
        }

        ThrowIfCompilationErrors(workspace.CurrentSolution.GetProject(vbId)!);
        ThrowIfCompilationErrors(workspace.CurrentSolution.GetProject(csId)!);
        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithVbGenerators(
        string projectFilePath = @"C:\fake\VbGeneratorHost.vbproj") =>
        new(TimeSpan.Zero, () => CreateVbGeneratorsLoaded(projectFilePath));

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
    /// Types with nested property/field types for in-process Binding-path walks.
    /// </summary>
    public static LoadedSolution CreateViewModelLoaded(string projectFilePath = @"C:\fake\ViewModels.csproj")
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "ViewModels",
            "ViewModels",
            LanguageNames.CSharp,
            filePath: projectFilePath));

        const string source = """
            namespace SampleApp;

            public class Person
            {
                public string Name { get; set; } = "";
            }

            public class Address
            {
                public string City { get; set; } = "";
                public string Postal;
            }

            public class Customer : Person
            {
                public Address Home { get; set; } = new();
                public string Nickname;
            }
            """;

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        solution = solution.AddDocument(
            docId,
            "ViewModels.cs",
            SourceText.From(source),
            filePath: Path.Combine(projectDir, "ViewModels.cs"));
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace view-model fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithAvalonia(
        string projectFilePath = @"C:\fake\AvaloniaApp.csproj") =>
        new(TimeSpan.Zero, () => CreateAvaloniaLoaded(projectFilePath));

    /// <summary>
    /// Avalonia-shaped code-behind type for x:Class resolution. Does not load WPF/MAUI/WinUI.
    /// </summary>
    public static LoadedSolution CreateAvaloniaLoaded(string projectFilePath = @"C:\fake\AvaloniaApp.csproj")
    {
        var workspace = new AdhocWorkspace();
        var appId = ProjectId.CreateNewId();
        var controlsId = ProjectId.CreateNewId();
        var appDocId = DocumentId.CreateNewId(appId);
        var attrDocId = DocumentId.CreateNewId(controlsId);
        var controlsDocId = DocumentId.CreateNewId(controlsId);

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        var controlsPath = Path.Combine(projectDir, "ControlsLib.csproj");

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                appId,
                VersionStamp.Create(),
                "AvaloniaApp",
                "AvaloniaApp",
                LanguageNames.CSharp,
                filePath: projectFilePath))
            .AddProject(ProjectInfo.Create(
                controlsId,
                VersionStamp.Create(),
                "ControlsLib",
                "ControlsLib",
                LanguageNames.CSharp,
                filePath: controlsPath));

        const string appSource = """
            namespace SampleApp;

            public partial class MainWindow
            {
                public MainWindow()
                {
                }

                public string HandwrittenTitle { get; set; } = "";
            }

            public class Address
            {
                public string City { get; set; } = "";
            }

            public class Customer
            {
                public Address Home { get; set; } = new();
                public string Name { get; set; } = "";
                public void Save() { }
            }
            """;

        const string attrSource = """
            using System;

            namespace Avalonia.Metadata
            {
                [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                public sealed class XmlnsDefinitionAttribute : Attribute
                {
                    public XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace)
                    {
                        XmlNamespace = xmlNamespace;
                        ClrNamespace = clrNamespace;
                    }

                    public string XmlNamespace { get; }
                    public string ClrNamespace { get; }
                }
            }
            """;

        const string controlsSource = """
            using Avalonia.Metadata;

            [assembly: XmlnsDefinition("https://github.com/avaloniaui", "SampleControls")]

            namespace SampleControls
            {
                public class FancyButton
                {
                }
            }
            """;

        var runtime = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        solution = solution.AddDocument(
            appDocId,
            "MainWindow.axaml.cs",
            SourceText.From(appSource),
            filePath: Path.Combine(projectDir, "MainWindow.axaml.cs"));
        solution = solution.AddDocument(
            attrDocId,
            "XmlnsDefinitionAttribute.cs",
            SourceText.From(attrSource),
            filePath: Path.Combine(projectDir, "XmlnsDefinitionAttribute.cs"));
        solution = solution.AddDocument(
            controlsDocId,
            "FancyButton.cs",
            SourceText.From(controlsSource),
            filePath: Path.Combine(projectDir, "FancyButton.cs"));
        solution = solution.WithProjectCompilationOptions(appId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.WithProjectCompilationOptions(controlsId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(appId, runtime);
        solution = solution.AddMetadataReference(controlsId, runtime);
        solution = solution.AddProjectReference(appId, new ProjectReference(controlsId));
        var generatorAssemblyPath = typeof(Avalonia.NameGenerator.NameGenerator).Assembly.Location;
        solution = solution.AddAnalyzerReference(
            appId,
            new AnalyzerFileReference(generatorAssemblyPath, TestAnalyzerAssemblyLoader.Instance));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace Avalonia fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static LoadedSolution CreateSymbolsLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "SampleLib.csproj");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

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

        const string generated = """
            namespace SampleLib;

            public partial class Calculator
            {
                public int GeneratedAnswer => 42;
            }
            """;

        var handwrittenPath = Path.Combine(projectDir, "Calculator.cs");
        var generatedDir = Path.Combine(projectDir, "Generated", "FakeGen");
        Directory.CreateDirectory(generatedDir);
        var generatedPath = Path.Combine(generatedDir, "Calculator.Generated.g.cs");
        File.WriteAllText(handwrittenPath, handwritten);
        File.WriteAllText(generatedPath, generated);

        return CreateSymbolsLoaded(projectFilePath);
    }


    public static LoadedSolution CreateVbGeneratorsLoaded(string projectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "VbGeneratorHost",
            "VbGeneratorHost",
            LanguageNames.VisualBasic,
            filePath: projectFilePath));

        const string source = """
            Namespace GeneratorHost
                Public Class Host
                    Public Shared Function Name() As String
                        Return "host"
                    End Function
                End Class

                Partial Public Class VbPartialThing
                    Public Function Format() As String
                        Return "hw"
                    End Function

                    Public Function Format(x As String) As String
                        Return x
                    End Function
                End Class
            End Namespace
            """;

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        solution = solution.AddDocument(
            docId,
            "Host.vb",
            SourceText.From(source),
            filePath: Path.Combine(projectDir, "Host.vb"));
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        var generatorAssemblyPath = typeof(CustomGenerator.VbMarkerGenerator).Assembly.Location;
        solution = solution.AddAnalyzerReference(
            projectId,
            new AnalyzerFileReference(generatorAssemblyPath, TestAnalyzerAssemblyLoader.Instance));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace VB generators fixture.");
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

            public partial class PartialThing
            {
                public string Format() => "hw";
                public string Format(string x) => x;
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


    public static LoadedSolution CreateVbDiagnosticsLoaded(string projectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "BrokenVb",
            "BrokenVb",
            LanguageNames.VisualBasic,
            filePath: projectFilePath));

        const string source = """
            Public Class Broken
                Public Alpha As Integer = "not-an-int"
                Public Beta As Integer = "also-bad"
                Public Gamma As Integer = "still-bad"
            End Class
            """;

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        solution = solution.AddDocument(
            docId,
            "Broken.vb",
            SourceText.From(source),
            filePath: Path.Combine(projectDir, "Broken.vb"));
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optionStrict: OptionStrict.On));
        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace VB diagnostics fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
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

    public static FakeSolutionLoader ImmediateWithHierarchy(string projectFilePath = @"C:\fake\HierarchyLib.csproj") =>
        new(TimeSpan.Zero, () => CreateHierarchyLoaded(projectFilePath));

    public static FakeSolutionLoader DelayedWithHierarchy(
        TimeSpan delay,
        string projectFilePath = @"C:\fake\HierarchyLib.csproj") =>
        new(delay, () => CreateHierarchyLoaded(projectFilePath));

    public static LoadedSolution CreateHierarchyLoaded(string projectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "HierarchyLib",
            "HierarchyLib",
            LanguageNames.CSharp,
            filePath: projectFilePath));

        const string source = """
            namespace SampleLib;

            public interface IDrawable
            {
                void Draw();
            }

            public interface IWidget : IDrawable
            {
                int Size { get; }
            }

            public abstract class Shape : IWidget
            {
                public abstract void Draw();
                public abstract int Size { get; }
            }

            public class Circle : Shape
            {
                public override void Draw() { }
                public override int Size => 1;
            }

            public class Square : Shape
            {
                public override void Draw() { }
                public override int Size => 2;
            }

            public class SpecialCircle : Circle
            {
            }
            """;

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        var sourcePath = Path.Combine(projectDir, "Shapes.cs");

        solution = solution.AddDocument(
            docId,
            "Shapes.cs",
            SourceText.From(source),
            filePath: sourcePath);
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace hierarchy fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithCallers(string projectFilePath = @"C:\fake\CallerLib.csproj") =>
        new(TimeSpan.Zero, () => CreateCallersLoaded(projectFilePath));

    public static FakeSolutionLoader DelayedWithCallers(
        TimeSpan delay,
        string projectFilePath = @"C:\fake\CallerLib.csproj") =>
        new(delay, () => CreateCallersLoaded(projectFilePath));

    public static LoadedSolution CreateCallersLoaded(string projectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var opsId = DocumentId.CreateNewId(projectId);
        var usesId = DocumentId.CreateNewId(projectId);
        var moreId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "CallerLib",
            "CallerLib",
            LanguageNames.CSharp,
            filePath: projectFilePath));

        const string ops = """
            namespace SampleLib;

            public static class MathOps
            {
                public static int Add(int a, int b) => a + b;
            }
            """;

        const string uses = """
            namespace SampleLib;

            public static class Uses
            {
                public static int Twice(int x) => MathOps.Add(x, x);
                public static int Triple(int x) => MathOps.Add(x, x) + x;
            }
            """;

        const string more = """
            namespace SampleLib;

            public static class MoreUses
            {
                public static int One() => MathOps.Add(1, 0);
                public static int Two() => MathOps.Add(1, 1);
            }
            """;

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        solution = solution.AddDocument(
            opsId,
            "MathOps.cs",
            SourceText.From(ops),
            filePath: Path.Combine(projectDir, "MathOps.cs"));
        solution = solution.AddDocument(
            usesId,
            "Uses.cs",
            SourceText.From(uses),
            filePath: Path.Combine(projectDir, "Uses.cs"));
        solution = solution.AddDocument(
            moreId,
            "MoreUses.cs",
            SourceText.From(more),
            filePath: Path.Combine(projectDir, "MoreUses.cs"));
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace callers fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }


    private static IReadOnlyList<MetadataReference> TrustedPlatformReferences()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
            "System.Console.dll",
            "netstandard.dll",
            "Microsoft.VisualBasic.dll",
            "Microsoft.VisualBasic.Core.dll",
        };
        var refs = tpa
            .Where(path => names.Contains(Path.GetFileName(path)))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        if (refs.Count == 0)
        {
            refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        }

        return refs;
    }

    private static void ThrowIfCompilationErrors(Project project)
    {
        var compilation = project.GetCompilationAsync().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException($"No compilation for {project.Name}.");
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException($"{project.Name} compile errors: {string.Join(" | ", errors)}");
        }
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

    private static Solution AddEmptyVbProject(Solution solution, string name, string filePath)
    {
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        solution = solution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.VisualBasic,
            filePath: filePath));

        solution = solution.AddDocument(
            docId,
            "Placeholder.vb",
            SourceText.From("' placeholder"));

        return solution.WithProjectCompilationOptions(
            projectId,
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static Solution AddEmptyFsharpProject(Solution solution, string name, string filePath)
    {
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        solution = solution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.FSharp,
            filePath: filePath));

        solution = solution.AddDocument(
            docId,
            "Placeholder.fs",
            SourceText.From("module Placeholder"));

        return solution;
    }}
