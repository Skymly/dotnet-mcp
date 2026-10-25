using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace DotNetMcp.Tests;

public sealed partial class FakeSolutionLoader
{
    /// <summary>
    /// symbol_resolve_at fixture: writes Widget.cs under <paramref name="projectDir"/>
    /// (trusted root) with every construct the position tests need — declarations,
    /// references, var, alias, ctor, reduced extension, constructed generic,
    /// overload failures, locals/params, and identifier-landing diagnostics.
    /// </summary>
    public static FakeSolutionLoader ImmediateWithResolveAtOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateResolveAtLoadedOnDisk(projectDir));

    public static LoadedSolution CreateResolveAtLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "ResolveApp.csproj");
        var widgetPath = Path.Combine(projectDir, "Widget.cs");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(widgetPath, """
            using AliasedWidget = ResolveApp.Widget;
            using System.Collections.Generic;

            namespace ResolveApp;

            public sealed class Widget
            {
                public int Ping(int value) => value + 1;
                public int Overload(int a) => a;
                public string Overload(string s) => s;
                private int neverAssigned;
                private int assignedNotUsed = 3;
                private List<int> items;
            }

            public sealed class Box<T>
            {
                public T Value = default!;
            }

            public static class WidgetExtensions
            {
                public static int Twice(this Widget widget) => 2;
            }

            public static class Uses
            {
                public static int Call(Widget widget)
                {
                    var w = new Widget();
                    var list = new List<int>();
                    var boxed = new Box<int> { Value = widget.Ping(list.Count) };
                    var local = widget.Ping(2);
                    var twice = w.Twice();
                    var fromAlias = new AliasedWidget();
                    return boxed.Value + widget.Ping (local) + twice + fromAlias.Ping(0);
                }

                public static int SingleCandidate(Widget widget) => widget.Ping(1, 2);

                public static int Ambiguous(Widget widget) => widget.Overload(1.5);
            }
            """);

        return CreateOnDiskProject(
            projectDir,
            projectFilePath,
            "ResolveApp",
            LanguageNames.CSharp,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            ("Widget.cs", widgetPath),
            "Failed to apply AdhocWorkspace symbol_resolve_at fixture.");
    }

    /// <summary>
    /// Two C# projects share one FilePath (linked/multi-TFM shape). Each project
    /// references a different metadata Lib: Ping resolves to a different
    /// signature per project (→ SymbolAmbiguous without projectId), while
    /// Common resolves to an identical signature (→ dedupe success).
    /// </summary>
    public static FakeSolutionLoader ImmediateWithResolveAtMultiTfmOnDisk(string root) =>
        new(TimeSpan.Zero, () => CreateResolveAtMultiTfmLoadedOnDisk(root));

    public static LoadedSolution CreateResolveAtMultiTfmLoadedOnDisk(string root)
    {
        var sharedDir = Path.Combine(root, "Shared");
        Directory.CreateDirectory(sharedDir);
        var sharedPath = Path.Combine(sharedDir, "Shared.cs");
        const string sharedSource = """
            namespace SharedLib;

            public static class Shared
            {
                public static int Call() => SharedDeps.Lib.Ping(default);
                public static int Common() => 42;
            }
            """;
        File.WriteAllText(sharedPath, sharedSource);

        var libA = BuildLibReference(
            "SharedDepsA",
            "namespace SharedDeps; public static class Lib { public static int Ping(int value) => value; }");
        var libB = BuildLibReference(
            "SharedDepsB",
            "namespace SharedDeps; public static class Lib { public static string Ping(string text) => text; }");

        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var projectA = ProjectId.CreateNewId();
        var projectB = ProjectId.CreateNewId();
        var dirA = Path.Combine(root, "SharedNet8");
        var dirB = Path.Combine(root, "SharedNet9");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var projFileA = Path.Combine(dirA, "SharedNet8.csproj");
        var projFileB = Path.Combine(dirB, "SharedNet9.csproj");
        File.WriteAllText(projFileA, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(projFileB, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        foreach (var (id, name, file, lib) in new[]
        {
            (projectA, "SharedNet8", projFileA, libA),
            (projectB, "SharedNet9", projFileB, libB),
        })
        {
            solution = solution.AddProject(ProjectInfo.Create(
                id,
                VersionStamp.Create(),
                name,
                name,
                LanguageNames.CSharp,
                filePath: file));
            solution = solution.AddDocument(
                DocumentId.CreateNewId(id),
                "Shared.cs",
                SourceText.From(sharedSource),
                filePath: sharedPath);
            solution = solution.WithProjectCompilationOptions(
                id, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            solution = solution.AddMetadataReference(id, lib);
            foreach (var metadata in TrustedPlatformReferences().Concat(CollectionsReference()))
            {
                solution = solution.AddMetadataReference(id, metadata);
            }
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace multi-TFM shared-file fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    /// <summary>
    /// VB fixture: declarations + references plus an &lt;Obsolete&gt; member whose
    /// call-site diagnostic lands on the member identifier.
    /// </summary>
    public static FakeSolutionLoader ImmediateWithResolveAtVbOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateResolveAtVbLoadedOnDisk(projectDir));

    public static LoadedSolution CreateResolveAtVbLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "ResolveVb.vbproj");
        var widgetPath = Path.Combine(projectDir, "Widget.vb");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(widgetPath, """
            Public Class Widget
                Public Function Ping() As String
                    Return "vb"
                End Function

                Public Function Echo(text As String) As String
                    Return text
                End Function

                <System.Obsolete("use Ping")>
                Public Function Old() As String
                    Return "old"
                End Function
            End Class

            Public Class Uses
                Public Shared Function CallPing() As String
                    Return New Widget().Ping()
                End Function

                Public Shared Function CallOld() As String
                    Return New Widget().Old()
                End Function
            End Class

            Public Class SpecialWidget
                Inherits Widget
                Public Function Ping() As String
                    Return "special"
                End Function
            End Class
            """);

        return CreateOnDiskProject(
            projectDir,
            projectFilePath,
            "ResolveVb",
            LanguageNames.VisualBasic,
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            ("Widget.vb", widgetPath),
            "Failed to apply AdhocWorkspace VB symbol_resolve_at fixture.");
    }

    /// <summary>
    /// F# fixture: module-level member declarations plus inner let bindings and
    /// parameters for the SymbolNotAddressable cases.
    /// </summary>
    public static FakeSolutionLoader ImmediateWithResolveAtFsharp(string root) =>
        new(TimeSpan.Zero, () => CreateResolveAtFsharpLoaded(root));

    public static LoadedSolution CreateResolveAtFsharpLoaded(string root)
    {
        var workspace = new AdhocWorkspace();
        var fsId = ProjectId.CreateNewId();
        var widgetDoc = DocumentId.CreateNewId(fsId);
        var usesDoc = DocumentId.CreateNewId(fsId);

        var fsDir = Path.Combine(root, "ResolveFs");
        Directory.CreateDirectory(fsDir);
        var widgetPath = Path.Combine(fsDir, "Widget.fs");
        var usesPath = Path.Combine(fsDir, "Uses.fs");
        const string widgetSource = """
            module ResolveFs.Widget

            let ping (value: int) = value + 1
            """;
        const string usesSource = """
            module ResolveFs.Uses

            let go (seed: int) =
                let local = seed + 1
                Widget.ping local

            let bad: int = Widget.ping
            """;
        File.WriteAllText(widgetPath, widgetSource);
        File.WriteAllText(usesPath, usesSource);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            fsId,
            VersionStamp.Create(),
            "ResolveFs",
            "ResolveFs",
            LanguageNames.FSharp,
            filePath: Path.Combine(fsDir, "ResolveFs.fsproj")));
        solution = solution.AddDocument(widgetDoc, "Widget.fs", SourceText.From(widgetSource), filePath: widgetPath);
        solution = solution.AddDocument(usesDoc, "Uses.fs", SourceText.From(usesSource), filePath: usesPath);

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace F# resolve_at fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    /// <summary>
    /// XAML symbol_resolve_at fixture (ADR-0006 part C): one C# project under
    /// <paramref name="root"/> carrying the xmlns-mapped controls (Window,
    /// FancyButton with Label + Click, TextBlock), the code-behind type with an
    /// OnSave handler, the x:DataType Customer/Address graph, and the
    /// NameGenerator analyzer so x:Name="TitleText" resolves to the generated
    /// field. The .axaml document itself is attached by
    /// AttachWorkspaceXamlDocuments from the solution directory the test writes
    /// it into.
    /// </summary>
    public static FakeSolutionLoader ImmediateWithAvaloniaXamlResolveAt(string root) =>
        new(TimeSpan.Zero, () => CreateAvaloniaXamlResolveAtLoaded(root));

    public static LoadedSolution CreateAvaloniaXamlResolveAtLoaded(string root)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        const string source = """
            using System;
            using Avalonia.Metadata;

            [assembly: XmlnsDefinition("https://github.com/avaloniaui", "SampleControls")]

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

            namespace SampleApp
            {
                public partial class MainWindow
                {
                    public MainWindow() { }

                    public void OnSave() { }
                }

                public class Address
                {
                    public string City { get; set; } = "";
                }

                public class Customer
                {
                    public Address Home { get; set; } = new();
                    public string Name { get; set; } = "";
                }
            }

            namespace SampleControls
            {
                public class Window
                {
                    public string HandwrittenTitle { get; set; } = "";
                }

                public class FancyButton
                {
                    public string Label { get; set; } = "";

                    public event EventHandler? Click;
                }

                public class TextBlock
                {
                    public string Text { get; set; } = "";
                }
            }
            """;

        var projectFilePath = Path.Combine(root, "XamlResolveApp.csproj");
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "XamlResolveApp",
            "XamlResolveApp",
            LanguageNames.CSharp,
            filePath: projectFilePath));
        solution = solution.AddDocument(
            docId,
            "MainWindow.axaml.cs",
            SourceText.From(source),
            filePath: Path.Combine(root, "MainWindow.axaml.cs"));
        solution = solution.WithProjectCompilationOptions(
            projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in TrustedPlatformReferences().Concat(CollectionsReference()))
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        var generatorAssemblyPath = typeof(Avalonia.NameGenerator.NameGenerator).Assembly.Location;
        solution = solution.AddAnalyzerReference(
            projectId,
            new AnalyzerFileReference(generatorAssemblyPath, TestAnalyzerAssemblyLoader.Instance));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace XAML symbol_resolve_at fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    private static MetadataReference BuildLibReference(string assemblyName, string source)
    {
        var references = TrustedPlatformReferences().Concat(CollectionsReference()).ToList();
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.ToMetadataReference();
    }
}
