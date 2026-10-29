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
public sealed partial class FakeSolutionLoader : ISolutionLoader
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

    public static FakeSolutionLoader ImmediateWithDynamic(string projectFilePath = @"C:\fake\DynLib.csproj") =>
        new(TimeSpan.Zero, () => CreateDynamicLoaded(projectFilePath));

    public static LoadedSolution CreateDynamicLoaded(string projectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        const string source = """
            namespace DynLib;

            public static class Host
            {
                public static object Run(dynamic d)
                {
                    var a = d.Foo(1, "x");
                    var b = 1 + 2;
                    return a;
                }
            }
            """;
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "DynLib",
            "DynLib",
            LanguageNames.CSharp,
            filePath: projectFilePath));
        solution = solution.AddDocument(docId, "Host.cs", SourceText.From(source), filePath: Path.Combine(projectDir, "Host.cs"));
        solution = solution.WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace dynamic fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithComInterop(string projectFilePath = @"C:\fake\ComLib.csproj") =>
        new(TimeSpan.Zero, () => CreateComInteropLoaded(projectFilePath));

    public static LoadedSolution CreateComInteropLoaded(string projectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var hostId = ProjectId.CreateNewId();
        var hostDoc = DocumentId.CreateNewId(hostId);
        var refs = TrustedPlatformReferences();

        const string hostSource = """
            using System.Runtime.InteropServices;

            namespace ComLib;

            [ComImport]
            [Guid("11111111-1111-1111-1111-111111111111")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IComThing
            {
                void Ping();
            }

            public sealed class Ordinary
            {
                public int Value => 1;
            }

            public static class Uses
            {
                public static void Touch(IComThing thing) => thing.Ping();
            }
            """;

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            hostId,
            VersionStamp.Create(),
            "ComLib",
            "ComLib",
            LanguageNames.CSharp,
            filePath: projectFilePath));
        solution = solution.AddDocument(hostDoc, "ComLib.cs", SourceText.From(hostSource), filePath: Path.Combine(projectDir, "ComLib.cs"));
        solution = solution.WithProjectCompilationOptions(hostId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in refs)
        {
            solution = solution.AddMetadataReference(hostId, metadata);
        }

        // Metadata-only wrapper: compile a sibling compilation and reference the image.
        var interop = CSharpCompilation.Create(
            "FakeTlbImp",
            new[] { CSharpSyntaxTree.ParseText("""
                using System.Runtime.InteropServices;
                [assembly: ImportedFromTypeLib("FakeLib")]
                namespace InteropLib;
                [ComImport, Guid("22222222-2222-2222-2222-222222222222")]
                public interface IMetaCom { void Go(); }
                """) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream();
        var emit = interop.Emit(pe);
        if (!emit.Success)
        {
            throw new InvalidOperationException("Failed to emit metadata COM fixture: " + string.Join(" | ", emit.Diagnostics));
        }

        solution = solution.AddMetadataReference(hostId, MetadataReference.CreateFromImage(pe.ToArray()));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace COM fixture.");
        }

        ThrowIfCompilationErrors(solution.GetProject(hostId)!);
        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithSymbols(string projectFilePath = @"C:\fake\SampleLib.csproj") =>
        new(TimeSpan.Zero, () => CreateSymbolsLoaded(projectFilePath));

    /// <summary>
    /// Writes handwritten source to disk under <paramref name="projectDir"/> so FSW/drift tests can mutate files.
    /// </summary>
    public static FakeSolutionLoader ImmediateWithSymbolsOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateSymbolsLoadedOnDisk(projectDir));

    public static FakeSolutionLoader ImmediateWithRenameOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateRenameLoadedOnDisk(projectDir));

    public static FakeSolutionLoader ImmediateWithVbRenameOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateVbRenameLoadedOnDisk(projectDir));

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

    public static FakeSolutionLoader ImmediateWithMissingMetadataReferences(
        string projectFilePath = @"C:\fake\DegradedLib.csproj") =>
        new(TimeSpan.Zero, () => CreateMissingMetadataReferencesLoaded(projectFilePath));

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

    public static FakeSolutionLoader ImmediateWithFsharpSignature(string root) =>
        new(TimeSpan.Zero, () => CreateFsharpSymbolsLoaded(root, includeSignature: true));

    public static LoadedSolution CreateFsharpSymbolsLoaded(string root, bool includeSignature = false)
    {
        var workspace = new AdhocWorkspace();
        var fsId = ProjectId.CreateNewId();
        var csId = ProjectId.CreateNewId();
        var widgetDoc = DocumentId.CreateNewId(fsId);
        var signatureDoc = DocumentId.CreateNewId(fsId);
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

        var signaturePath = Path.Combine(fsDir, "Widget.fsi");
        const string signatureSource = "module FsLib.Widget\nval ping: unit -> string\n";
        File.WriteAllText(widgetPath, widgetSource);
        File.WriteAllText(usesPath, usesSource);
        if (includeSignature)
        {
            File.WriteAllText(signaturePath, signatureSource);
        }
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
        if (includeSignature)
        {
            solution = solution.AddDocument(signatureDoc, "Widget.fsi", SourceText.From(signatureSource), filePath: signaturePath);
        }

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


    public static FakeSolutionLoader ImmediateWithFsharpCrossProject(string root) =>
        new(TimeSpan.Zero, () => CreateFsharpCrossProjectLoaded(root));

    /// <summary>
    /// FsLib ← FsApp ← FsTop (transitive F# chain with an interface implemented
    /// across the boundary) plus FsOnlyCs ← CsConsumer (C#-only dependent for
    /// cross-language refusal / gap-message assertions).
    /// </summary>
    public static LoadedSolution CreateFsharpCrossProjectLoaded(string root)
    {
        var workspace = new AdhocWorkspace();
        var libId = ProjectId.CreateNewId();
        var appId = ProjectId.CreateNewId();
        var topId = ProjectId.CreateNewId();
        var onlyId = ProjectId.CreateNewId();
        var csId = ProjectId.CreateNewId();
        var libDoc = DocumentId.CreateNewId(libId);
        var appDoc = DocumentId.CreateNewId(appId);
        var topDoc = DocumentId.CreateNewId(topId);
        var onlyDoc = DocumentId.CreateNewId(onlyId);
        var csDoc = DocumentId.CreateNewId(csId);

        var libDir = Path.Combine(root, "FsLib");
        var appDir = Path.Combine(root, "FsApp");
        var topDir = Path.Combine(root, "FsTop");
        var onlyDir = Path.Combine(root, "FsOnlyCs");
        var csDir = Path.Combine(root, "CsConsumer");
        foreach (var dir in new[] { libDir, appDir, topDir, onlyDir, csDir })
        {
            Directory.CreateDirectory(dir);
        }

        var libPath = Path.Combine(libDir, "Library.fs");
        var appPath = Path.Combine(appDir, "Use.fs");
        var topPath = Path.Combine(topDir, "Top.fs");
        var onlyPath = Path.Combine(onlyDir, "Library.fs");
        var csPath = Path.Combine(csDir, "Use.cs");

        const string libSource = """
            module FsLib.Core

            let private bump (n: int) = n + 1

            let doubleThenBump (n: int) = bump (n + n)

            type Greeter =
                static member Greet (name: string) = "hi " + name

            type IShape =
                abstract member Area: float
            """;
        const string appSource = """
            module FsApp.Use

            let doubled (n: int) = FsLib.Core.doubleThenBump n

            let viaParam (f: int -> int) (n: int) = FsLib.Core.doubleThenBump (f n)

            let greeting (name: string) = FsLib.Core.Greeter.Greet name

            type Coin (r: float) =
                interface FsLib.Core.IShape with
                    member _.Area = System.Math.PI * r * r

            let own (n: int) = n * 3
            """;
        const string topSource = """
            module FsTop.Top

            let run (n: int) = FsApp.Use.doubled n + FsLib.Core.doubleThenBump n
            """;
        const string onlySource = """
            module FsOnlyCs.Scaling

            let scale (n: int) = n * 2
            """;
        const string csSource = """
            namespace CsConsumer;

            public static class Use
            {
                public static int Apply(int n) => FsOnlyCs.Scaling.scale(n);
            }
            """;

        File.WriteAllText(libPath, libSource);
        File.WriteAllText(appPath, appSource);
        File.WriteAllText(topPath, topSource);
        File.WriteAllText(onlyPath, onlySource);
        File.WriteAllText(csPath, csSource);

        var solution = workspace.CurrentSolution;
        solution = solution.AddProject(ProjectInfo.Create(
            libId, VersionStamp.Create(), "FsLib", "FsLib", LanguageNames.FSharp,
            filePath: Path.Combine(libDir, "FsLib.fsproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            appId, VersionStamp.Create(), "FsApp", "FsApp", LanguageNames.FSharp,
            filePath: Path.Combine(appDir, "FsApp.fsproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            topId, VersionStamp.Create(), "FsTop", "FsTop", LanguageNames.FSharp,
            filePath: Path.Combine(topDir, "FsTop.fsproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            onlyId, VersionStamp.Create(), "FsOnlyCs", "FsOnlyCs", LanguageNames.FSharp,
            filePath: Path.Combine(onlyDir, "FsOnlyCs.fsproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            csId, VersionStamp.Create(), "CsConsumer", "CsConsumer", LanguageNames.CSharp,
            filePath: Path.Combine(csDir, "CsConsumer.csproj")));

        solution = solution.AddDocument(libDoc, "Library.fs", SourceText.From(libSource), filePath: libPath);
        solution = solution.AddDocument(appDoc, "Use.fs", SourceText.From(appSource), filePath: appPath);
        solution = solution.AddDocument(topDoc, "Top.fs", SourceText.From(topSource), filePath: topPath);
        solution = solution.AddDocument(onlyDoc, "Library.fs", SourceText.From(onlySource), filePath: onlyPath);
        solution = solution.AddDocument(csDoc, "Use.cs", SourceText.From(csSource), filePath: csPath);

        solution = solution.AddProjectReference(appId, new ProjectReference(libId));
        solution = solution.AddProjectReference(topId, new ProjectReference(appId));
        solution = solution.AddProjectReference(csId, new ProjectReference(onlyId));
        solution = solution.WithProjectCompilationOptions(
            csId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace F# cross-project fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithFsharpCollidingFileNames(string root) =>
        new(TimeSpan.Zero, () => CreateFsharpCollidingFileNamesLoaded(root));

    public static LoadedSolution CreateFsharpCollidingFileNamesLoaded(string root)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var alphaDoc = DocumentId.CreateNewId(projectId);
        var betaDoc = DocumentId.CreateNewId(projectId);
        var alphaDir = Path.Combine(root, "A");
        var betaDir = Path.Combine(root, "B");
        Directory.CreateDirectory(alphaDir);
        Directory.CreateDirectory(betaDir);
        var alphaPath = Path.Combine(alphaDir, "Widget.fs");
        var betaPath = Path.Combine(betaDir, "Widget.fs");
        const string alphaSource = """
            module Collide.Alpha

            let ping () = "alpha"
            """;
        const string betaSource = """
            module Collide.Beta

            let ping () = "beta"
            """;
        File.WriteAllText(alphaPath, alphaSource);
        File.WriteAllText(betaPath, betaSource);

        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "CollideFs",
            "CollideFs",
            LanguageNames.FSharp,
            filePath: Path.Combine(root, "CollideFs.fsproj")));
        solution = solution.AddDocument(alphaDoc, "A/Widget.fs", SourceText.From(alphaSource), filePath: alphaPath);
        solution = solution.AddDocument(betaDoc, "B/Widget.fs", SourceText.From(betaSource), filePath: betaPath);

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace F# colliding-filename fixture.");
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

    /// <summary>
    /// In-workspace analyzer refresh seam (issue #549): sources and the analyzer DLL all
    /// live under the test's trusted temp root so a test can overwrite the DLL mid-session
    /// and watch the next epoch pick it up.
    /// </summary>
    public static FakeSolutionLoader ImmediateWithAnalyzerRefreshOnDisk(
        string projectDir,
        string analyzerAssemblyPath) =>
        new(TimeSpan.Zero, () => CreateAnalyzerRefreshLoadedOnDisk(projectDir, analyzerAssemblyPath));

    public static FakeSolutionLoader DelayedWithGenerators(
        TimeSpan delay,
        string projectFilePath = @"C:\fake\GeneratorHost.csproj") =>
        new(delay, () => CreateGeneratorsLoaded(projectFilePath));

    public async Task<LoadedSolution> OpenAsync(
        string path,
        IProgress<LoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceExtension(path);
        progress?.Report(new LoadProgress(0, 1));
        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var loaded = AttachWorkspaceXamlDocuments(_factory(), path);
        var count = Math.Max(1, loaded.Solution.ProjectIds.Count);
        progress?.Report(new LoadProgress(count, count));
        return loaded;
    }

    private static void EnsureWorkspaceExtension(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".slnf", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidDataException(
            $"Unsupported workspace path extension '{ext}'. Use .sln, .slnx, .slnf, or a project file.");
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

            public interface INamed
            {
                string Name { get; }
            }

            public interface ICustomer : INamed
            {
                string Email { get; }
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

    public static FakeSolutionLoader ImmediateWithAvaloniaXamlSnapshot(
        string xamlPath,
        string snapshotText,
        string projectFilePath = @"C:\fake\AvaloniaApp.csproj") =>
        new(TimeSpan.Zero, () =>
        {
            var loaded = CreateAvaloniaLoaded(projectFilePath);
            var project = loaded.Solution.Projects.First();
            var documentId = DocumentId.CreateNewId(project.Id);
            var fullPath = Path.GetFullPath(xamlPath);
            var solution = loaded.Solution.AddAdditionalDocument(
                documentId,
                Path.GetFileName(fullPath),
                SourceText.From(snapshotText),
                filePath: fullPath);
            if (!loaded.Workspace.TryApplyChanges(solution))
            {
                throw new InvalidOperationException("Failed to apply Avalonia XAML snapshot document.");
            }

            return new LoadedSolution(loaded.Workspace, loaded.Workspace.CurrentSolution, loaded.Warnings);
        });

    internal static LoadedSolution AttachWorkspaceXamlDocuments(LoadedSolution loaded, string solutionPath)
    {
        string? directory;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(solutionPath));
        }
        catch (Exception)
        {
            return loaded;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return loaded;
        }

        var project = loaded.Solution.Projects.FirstOrDefault();
        if (project is null)
        {
            return loaded;
        }

        var solution = loaded.Solution;
        var added = false;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var extension = Path.GetExtension(file);
            if (!extension.Equals(".axaml", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(file);
            if (solution.GetDocumentIdsWithFilePath(fullPath).Length > 0 ||
                solution.GetDocumentIdsWithFilePath(file).Length > 0)
            {
                continue;
            }

            var documentId = DocumentId.CreateNewId(project.Id);
            solution = solution.AddAdditionalDocument(
                documentId,
                Path.GetFileName(fullPath),
                SourceText.From(File.ReadAllText(fullPath)),
                filePath: fullPath);
            added = true;
        }

        if (!added)
        {
            return loaded;
        }

        if (!loaded.Workspace.TryApplyChanges(solution))
        {
            return loaded;
        }

        return new LoadedSolution(loaded.Workspace, loaded.Workspace.CurrentSolution, loaded.Warnings);
    }

    public static FakeSolutionLoader ImmediateWithAvalonia(
        string projectFilePath = @"C:\fake\AvaloniaApp.csproj") =>
        new(TimeSpan.Zero, () => CreateAvaloniaLoaded(projectFilePath));

    public static FakeSolutionLoader ImmediateWithMaui(
        string projectFilePath = @"C:\fake\MauiApp.csproj") =>
        new(TimeSpan.Zero, () => CreateMauiLoaded(projectFilePath));

    public static FakeSolutionLoader ImmediateWithDataContext(
        string projectFilePath = @"C:\fake\DcApp.csproj") =>
        new(TimeSpan.Zero, () => CreateDataContextLoaded(projectFilePath));

    public static FakeSolutionLoader ImmediateWithVbXaml(
        string projectFilePath = @"C:\fake\VbXaml.vbproj") =>
        new(TimeSpan.Zero, () => CreateVbXamlLoaded(projectFilePath));

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

            public interface INamed
            {
                string Name { get; }
            }

            public interface ICustomer : INamed
            {
                string Email { get; }
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

    public static LoadedSolution CreateDataContextLoaded(string projectFilePath = @"C:\fake\DcApp.csproj")
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Create(), "DcApp", "DcApp", LanguageNames.CSharp, filePath: projectFilePath));
        const string source = """
            namespace SampleApp;

            public class MainWindow
            {
                public Customer DataContext { get; set; } = new();
            }

            public class Customer
            {
                public string Name { get; set; } = "";
            }
            """;
        solution = solution.AddDocument(docId, "MainWindow.axaml.cs", SourceText.From(source), filePath: Path.Combine(projectDir, "MainWindow.axaml.cs"));
        solution = solution.WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply DataContext fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static LoadedSolution CreateVbXamlLoaded(string projectFilePath = @"C:\fake\VbXaml.vbproj")
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Create(), "VbXaml", "VbXaml", LanguageNames.VisualBasic, filePath: projectFilePath));
        const string source = """
            Public Class MainWindow
                Public Property TitleText As String = "vb"
            End Class

            Namespace SampleControls
                Public Class FancyPanel
                    Public Property Title As String = "panel"
                End Class
            End Namespace
            """;
        solution = solution.AddDocument(docId, "MainWindow.axaml.vb", SourceText.From(source), filePath: Path.Combine(projectDir, "MainWindow.axaml.vb"));
        solution = solution.WithProjectCompilationOptions(projectId, new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply VB XAML fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static LoadedSolution CreateMauiLoaded(string projectFilePath = @"C:\fake\MauiApp.csproj")
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "MauiApp",
            "MauiApp",
            LanguageNames.CSharp,
            filePath: projectFilePath));
        const string source = """
            using System;
            using Microsoft.Maui.Controls;

            [assembly: XmlnsDefinition("http://schemas.microsoft.com/dotnet/2021/maui", "Microsoft.Maui.Controls")]

            namespace Microsoft.Maui.Controls
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

                public class ContentPage
                {
                }

                public class Label
                {
                    public string Text { get; set; } = "";
                }
            }

            namespace MauiPage
            {
                public partial class MainPage
                {
                    public MainPage()
                    {
                    }

                    // Stands in for Microsoft.Maui.Controls.SourceGen x:Name field (Spike S5).
                    private object TitleLabel = new();
                }

                public sealed class MainViewModel
                {
                    public string Title { get; set; } = "hello";
                }
            }
            """;
        solution = solution.AddDocument(
            docId,
            "MainPage.xaml.cs",
            SourceText.From(source),
            filePath: Path.Combine(projectDir, "MainPage.xaml.cs"));
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace MAUI fixture.");
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


    public static FakeSolutionLoader ImmediateWithAvaloniaRenameOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateAvaloniaRenameLoadedOnDisk(projectDir));

    /// <summary>
    /// On-disk Avalonia-shaped rename fixture (#525): one project whose
    /// code-behind carries a handwritten reference to the generated
    /// <c>x:Name</c> field plus the view-model and control types the XAML
    /// usages resolve to. The <c>.axaml</c> document is attached by
    /// <see cref="AttachWorkspaceXamlDocuments"/> from the opened solution's
    /// directory, so the test writes it (and controls its encoding).
    /// </summary>
    public static LoadedSolution CreateAvaloniaRenameLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "AvaloniaApp.csproj");
        var codeBehindPath = Path.Combine(projectDir, "MainWindow.axaml.cs");
        var controlsPath = Path.Combine(projectDir, "Controls.cs");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(codeBehindPath, """
            namespace SampleApp;

            public partial class MainWindow
            {
                public MainWindow()
                {
                }

                // Handwritten reference to the generated x:Name field — renames with it.
                public object Named() => TitleText;

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

            public class Vm
            {
                public string Title { get; set; } = "";
            }
            """);
        File.WriteAllText(controlsPath, """
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

            namespace SampleControls
            {
                public class Window
                {
                    public string HandwrittenTitle { get; set; } = "";
                }

                public class FancyButton
                {
                    public object DataContext { get; set; } = new();

                    public string Label { get; set; } = "";

                    public event EventHandler Click;
                }

                public class TextBlock
                {
                    public object Tag { get; set; } = new();

                    public string Text { get; set; } = "";
                }
            }
            """);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "AvaloniaApp",
            "AvaloniaApp",
            LanguageNames.CSharp,
            filePath: projectFilePath));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "MainWindow.axaml.cs",
            SourceText.From(File.ReadAllText(codeBehindPath)),
            filePath: codeBehindPath);
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "Controls.cs",
            SourceText.From(File.ReadAllText(controlsPath)),
            filePath: controlsPath);
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        var generatorAssemblyPath = typeof(Avalonia.NameGenerator.NameGenerator).Assembly.Location;
        solution = solution.AddAnalyzerReference(
            projectId,
            new AnalyzerFileReference(generatorAssemblyPath, TestAnalyzerAssemblyLoader.Instance));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace Avalonia rename fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static LoadedSolution CreateRenameLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "RenameApp.csproj");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var widgetPath = Path.Combine(projectDir, "Widget.cs");
        var callerPath = Path.Combine(projectDir, "Caller.cs");
        File.WriteAllText(widgetPath, """
            namespace RenameApp;

            public sealed class Widget
            {
                public int Ping(int value) => value + 1;
            }
            """);
        File.WriteAllText(callerPath, """
            namespace RenameApp;

            public static class Caller
            {
                public static int Use(Widget widget) => widget.Ping(2);
            }
            """);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "RenameApp",
            "RenameApp",
            LanguageNames.CSharp,
            filePath: projectFilePath));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "Widget.cs",
            SourceText.From(File.ReadAllText(widgetPath)),
            filePath: widgetPath);
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "Caller.cs",
            SourceText.From(File.ReadAllText(callerPath)),
            filePath: callerPath);
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace rename fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static LoadedSolution CreateVbRenameLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "VbRename.vbproj");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var widgetPath = Path.Combine(projectDir, "Widget.vb");
        var callerPath = Path.Combine(projectDir, "Caller.vb");
        File.WriteAllText(widgetPath, """
            Public Class Widget
                Public Function Ping(value As Integer) As Integer
                    Return value + 1
                End Function
            End Class
            """);
        File.WriteAllText(callerPath, """
            Public Module Caller
                Public Function Use(widget As Widget) As Integer
                    Return widget.Ping(2)
                End Function
            End Module
            """);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "VbRename",
            "VbRename",
            LanguageNames.VisualBasic,
            filePath: projectFilePath));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "Widget.vb",
            SourceText.From(File.ReadAllText(widgetPath)),
            filePath: widgetPath);
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "Caller.vb",
            SourceText.From(File.ReadAllText(callerPath)),
            filePath: callerPath);
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace VB rename fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
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

                    ' Handwritten implementation of the generated partial declaration
                    ' (generation-trigger seam, issue #524).
                    Private Sub VbCallback()
                    End Sub
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
            using System;

            [assembly: Avalonia.Metadata.XmlnsDefinition("https://github.com/avaloniaui", "SampleControls")]

            namespace Avalonia.Metadata
            {
                [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                public sealed class XmlnsDefinitionAttribute : Attribute
                {
                    public XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace)
                    {
                    }
                }
            }

            namespace SampleControls
            {
                public class Window
                {
                }

                public class TextBlock
                {
                }
            }

            namespace SampleApp
            {
                // Code-behind part; Avalonia.NameGenerator adds the x:Name'd fields.
                public partial class MainWindow
                {
                }
            }

            namespace GeneratorHost
            {
                public static class Host
                {
                    public static string Name => "host";
                }

                public partial class PartialThing
                {
                    public string Format() => "hw";
                    public string Format(string x) => x;

                    // Handwritten implementation of the generated partial definition
                    // (generation-trigger seam, issue #524).
                    partial void OnValueChanged(int value)
                    {
                    }

                    // Handwritten *definition* of the generated partial implementation
                    // (the GeneratedRegex shape — the trigger side of the pair).
                    partial void GeneratedImpl();
                }
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
    /// Issue #549 fixture: <paramref name="analyzerAssemblyPath"/> is attached as a real
    /// on-disk <see cref="AnalyzerFileReference"/>. <c>Consumer.cs</c> references the
    /// CustomGenerator-emitted <c>SampleApp.Generated.CustomMarker.Who</c>, so a refresh
    /// to an analyzer that stops emitting the marker surfaces as CS0246; the
    /// <c>Renamable.Ping</c> symbol drives a preview/apply write to advance the epoch.
    /// </summary>
    public static LoadedSolution CreateAnalyzerRefreshLoadedOnDisk(string projectDir, string analyzerAssemblyPath)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "GenHost.csproj");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var consumerPath = Path.Combine(projectDir, "Consumer.cs");
        var renamablePath = Path.Combine(projectDir, "Renamable.cs");
        File.WriteAllText(consumerPath, """
            namespace GenHost;

            public static class Consumer
            {
                public static string Who => SampleApp.Generated.CustomMarker.Who;
            }
            """);
        File.WriteAllText(renamablePath, """
            namespace GenHost;

            public sealed class Renamable
            {
                public static int Ping(int value) => value + 1;
            }
            """);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "GenHost",
            "GenHost",
            LanguageNames.CSharp,
            filePath: projectFilePath));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "Consumer.cs",
            SourceText.From(File.ReadAllText(consumerPath)),
            filePath: consumerPath);
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId),
            "Renamable.cs",
            SourceText.From(File.ReadAllText(renamablePath)),
            filePath: renamablePath);
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in TrustedPlatformReferences())
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        solution = solution.AddAnalyzerReference(
            projectId,
            new AnalyzerFileReference(analyzerAssemblyPath, TestAnalyzerAssemblyLoader.Instance));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace analyzer-refresh fixture.");
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
    /// Degraded-compilation fixture (issue #546): DegradedLib references the
    /// emitted MidLib image while DepLib — whose DepType appears in MidLib's
    /// public API — is not referenced, so the compilation reports CS0012. An
    /// ordinary CS0246 typo stays listed beside it so tests prove the flag does
    /// not filter diagnostics. (CS0006 cannot be produced through public
    /// AdhocWorkspace APIs — MetadataReference.CreateFromFile reads eagerly and
    /// there is no unresolved-reference surface — so that id is covered by the
    /// detection predicate unit test instead.)
    /// </summary>
    public static LoadedSolution CreateMissingMetadataReferencesLoaded(string projectFilePath)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var refs = TrustedPlatformReferences();

        var depImage = EmitLibrary(
            "DepLib",
            "namespace DepLib; public sealed class DepType { public int Value => 1; }",
            refs);
        var midImage = EmitLibrary(
            "MidLib",
            "namespace MidLib; public static class Holder { public static DepLib.DepType Get() => new DepLib.DepType(); }",
            [.. refs, MetadataReference.CreateFromImage(depImage)]);

        var projectDir = Path.GetDirectoryName(projectFilePath) ?? @"C:\fake";
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "DegradedLib",
            "DegradedLib",
            LanguageNames.CSharp,
            filePath: projectFilePath));

        const string source = """
            namespace DegradedLib;

            public static class Use
            {
                public static int Read() => MidLib.Holder.Get().Value;
                public static Unknown.Type? Typed => null;
            }
            """;

        solution = solution.AddDocument(
            docId,
            "Use.cs",
            SourceText.From(source),
            filePath: Path.Combine(projectDir, "Use.cs"));
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in refs)
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        // MidLib is referenced but its DepLib dependency is not → CS0012.
        solution = solution.AddMetadataReference(projectId, MetadataReference.CreateFromImage(midImage));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace degraded fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithUnmaterializedProjectReference(
        string root = @"C:\fake") =>
        new(TimeSpan.Zero, () => CreateUnmaterializedProjectReferenceLoaded(root));

    /// <summary>
    /// Failed project-reference fixture (issue #560): GhostConsumer declares a
    /// ProjectReference whose target was never loaded — a ghost ProjectId, the
    /// shape MSBuildWorkspace leaves behind when a reference resolves to no
    /// matching metadata reference — so uses of the missing assembly degrade to
    /// CS0246 cascade errors while no CS0006/CS0012 fires. PlainLib reports the
    /// ambiguous CS0234/CS1061 ids with every reference resolved, proving the
    /// flag is structural and not keyed on those diagnostic ids.
    /// </summary>
    public static LoadedSolution CreateUnmaterializedProjectReferenceLoaded(string root)
    {
        var workspace = new AdhocWorkspace();
        var consumerId = ProjectId.CreateNewId();
        var plainId = ProjectId.CreateNewId();
        var consumerDoc = DocumentId.CreateNewId(consumerId);
        var plainDoc = DocumentId.CreateNewId(plainId);
        var refs = TrustedPlatformReferences();

        // MSBuildWorkspace keeps an unresolved project reference as a ProjectId
        // no loaded project stands behind; its debug name is the declared path.
        // TryApplyChanges would drop it — ApplyProjectAdded re-adds through
        // Project.ToProjectInfo(), which carries only the filtered
        // Project.ProjectReferences — so the workspace mutators apply the
        // ProjectInfo (and its declared ghost reference) directly.
        var ghostId = ProjectId.CreateNewId(
            debugName: Path.Combine(root, "GhostDep", "GhostDep.csproj"));

        const string consumerSource = """
            namespace GhostConsumer;

            public static class Use
            {
                public static GhostDep.GhostType? Typed => null;
                public static int Read(GhostDep.GhostType t) => t.Value;
            }
            """;

        const string plainSource = """
            namespace PlainLib;

            public static class Use
            {
                public static int Read(string s) => s.MissingMethod();
                public static System.MissingNs.DeepType? Deep => null;
            }
            """;

        workspace.AddProject(ProjectInfo.Create(
            consumerId,
            VersionStamp.Create(),
            "GhostConsumer",
            "GhostConsumer",
            LanguageNames.CSharp,
            filePath: Path.Combine(root, "GhostConsumer", "GhostConsumer.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            documents:
            [
                DocumentInfo.Create(
                    consumerDoc,
                    "Use.cs",
                    loader: TextLoader.From(
                        TextAndVersion.Create(SourceText.From(consumerSource), VersionStamp.Create())),
                    filePath: Path.Combine(root, "GhostConsumer", "Use.cs")),
            ],
            projectReferences: [new ProjectReference(ghostId)],
            metadataReferences: refs));
        workspace.AddProject(ProjectInfo.Create(
            plainId,
            VersionStamp.Create(),
            "PlainLib",
            "PlainLib",
            LanguageNames.CSharp,
            filePath: Path.Combine(root, "PlainLib", "PlainLib.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            documents:
            [
                DocumentInfo.Create(
                    plainDoc,
                    "Use.cs",
                    loader: TextLoader.From(
                        TextAndVersion.Create(SourceText.From(plainSource), VersionStamp.Create())),
                    filePath: Path.Combine(root, "PlainLib", "Use.cs")),
            ],
            metadataReferences: refs));

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static FakeSolutionLoader ImmediateWithUnresolvedAnalyzerDegraded(
        string root = @"C:\fake",
        string? missingAnalyzerPath = null,
        bool keepStubAttached = false) =>
        new(TimeSpan.Zero, () => CreateUnresolvedAnalyzerDegradedLoaded(root, missingAnalyzerPath, keepStubAttached));

    /// <summary>
    /// Missing analyzer/generator fixture (issue #566): GenConsumer declares an
    /// analyzer reference whose dll never materialized — recorded on the session
    /// the way the loader records stripped <see cref="UnresolvedAnalyzerReference"/>
    /// stubs (issue #548) — while uses of the generated API degrade to
    /// CS0246/CS0103/CS1061 cascade errors with no CS0006/CS0012 and every
    /// project reference resolved. CleanLib reports ambiguous ids with all
    /// analyzer inputs materialized, proving the flag keys on the declared
    /// path, not the diagnostic ids. <paramref name="keepStubAttached"/> leaves
    /// the stub on the project instead of recording it — the shape a workspace
    /// that never ran the load-time detachment exposes.
    /// </summary>
    public static LoadedSolution CreateUnresolvedAnalyzerDegradedLoaded(
        string root,
        string? missingAnalyzerPath = null,
        bool keepStubAttached = false)
    {
        var workspace = new AdhocWorkspace();
        var consumerId = ProjectId.CreateNewId();
        var cleanId = ProjectId.CreateNewId();
        var consumerDoc = DocumentId.CreateNewId(consumerId);
        var cleanDoc = DocumentId.CreateNewId(cleanId);
        var refs = TrustedPlatformReferences();
        var analyzerPath = missingAnalyzerPath
            ?? Path.Combine(root, "SourceGen", "bin", "Debug", "netstandard2.0", "SourceGen.Generators.dll");

        const string consumerSource = """
            namespace GenConsumer;

            public static class Use
            {
                public static SourceGen.Generated.Widget? Typed => null;
                public static string Call() => GeneratedRegistry.Names;
                public static int Read(string s) => s.GeneratedExtension();
            }
            """;

        const string cleanSource = """
            namespace CleanLib;

            public static class Use
            {
                public static int Read(string s) => s.MissingMethod();
            }
            """;

        workspace.AddProject(ProjectInfo.Create(
            consumerId,
            VersionStamp.Create(),
            "GenConsumer",
            "GenConsumer",
            LanguageNames.CSharp,
            filePath: Path.Combine(root, "GenConsumer", "GenConsumer.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            documents:
            [
                DocumentInfo.Create(
                    consumerDoc,
                    "Use.cs",
                    loader: TextLoader.From(
                        TextAndVersion.Create(SourceText.From(consumerSource), VersionStamp.Create())),
                    filePath: Path.Combine(root, "GenConsumer", "Use.cs")),
            ],
            metadataReferences: refs));
        workspace.AddProject(ProjectInfo.Create(
            cleanId,
            VersionStamp.Create(),
            "CleanLib",
            "CleanLib",
            LanguageNames.CSharp,
            filePath: Path.Combine(root, "CleanLib", "CleanLib.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            documents:
            [
                DocumentInfo.Create(
                    cleanDoc,
                    "Use.cs",
                    loader: TextLoader.From(
                        TextAndVersion.Create(SourceText.From(cleanSource), VersionStamp.Create())),
                    filePath: Path.Combine(root, "CleanLib", "Use.cs")),
            ],
            metadataReferences: refs));

        if (keepStubAttached)
        {
            var withStub = workspace.CurrentSolution.AddAnalyzerReference(
                consumerId, new UnresolvedAnalyzerReference(analyzerPath));
            if (!workspace.TryApplyChanges(withStub))
            {
                throw new InvalidOperationException("Failed to attach the unresolved analyzer fixture stub.");
            }

            return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        }

        var unresolvedAnalyzerPaths = new Dictionary<ProjectId, IReadOnlyList<string>>
        {
            [consumerId] = [analyzerPath],
        };
        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: [], unresolvedAnalyzerPaths);
    }

    public static FakeSolutionLoader ImmediateWithFsharpUnresolvedAnalyzer(
        string projectFilePath = @"C:\fake\BrokenFs.fsproj",
        string missingAnalyzerPath = @"C:\fake\gen\Fs.Generators.dll") =>
        new(TimeSpan.Zero, () => CreateFsharpUnresolvedAnalyzerLoaded(projectFilePath, missingAnalyzerPath));

    /// <summary>
    /// F# variant of the missing-analyzer fixture (issue #566): the F# project
    /// declares an analyzer path that never materialized — recorded on the
    /// session under the same project id the F# snapshot carries — and reports
    /// real FS0001 errors beside the flag.
    /// </summary>
    public static LoadedSolution CreateFsharpUnresolvedAnalyzerLoaded(
        string projectFilePath,
        string missingAnalyzerPath)
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
            throw new InvalidOperationException("Failed to apply AdhocWorkspace F# unresolved-analyzer fixture.");
        }

        var unresolvedAnalyzerPaths = new Dictionary<ProjectId, IReadOnlyList<string>>
        {
            [projectId] = [missingAnalyzerPath],
        };
        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: [], unresolvedAnalyzerPaths);
    }

    private static byte[] EmitLibrary(string assemblyName, string source, IReadOnlyList<MetadataReference> refs)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        if (!emit.Success)
        {
            throw new InvalidOperationException(
                $"Failed to emit {assemblyName} fixture: {string.Join(" | ", emit.Diagnostics)}");
        }

        return pe.ToArray();
    }

    /// <summary>
    /// LibA defines Marker (multiple local refs). LibB and Outsider reference LibA and use Marker.
    /// Default scoped search is LibA plus its dependents (LibB, Outsider).
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

    public static FakeSolutionLoader ImmediateWithCallersGraph(string root = @"C:\fake") =>
        new(TimeSpan.Zero, () => CreateCallersGraphLoaded(root));

    public static LoadedSolution CreateCallersGraphLoaded(string root = @"C:\fake")
    {
        var workspace = new AdhocWorkspace();
        var libAId = ProjectId.CreateNewId();
        var outsiderId = ProjectId.CreateNewId();
        var libAOps = DocumentId.CreateNewId(libAId);
        var libAUses = DocumentId.CreateNewId(libAId);
        var outsiderDoc = DocumentId.CreateNewId(outsiderId);
        var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var solution = workspace.CurrentSolution;

        solution = solution.AddProject(ProjectInfo.Create(
            libAId, VersionStamp.Create(), "LibA", "LibA", LanguageNames.CSharp,
            filePath: Path.Combine(root, "LibA", "LibA.csproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            outsiderId, VersionStamp.Create(), "Outsider", "Outsider", LanguageNames.CSharp,
            filePath: Path.Combine(root, "Outsider", "Outsider.csproj")));

        const string ops = """
            namespace LibA;

            public static class MathOps
            {
                public static int Add(int a, int b) => a + b;
            }
            """;
        const string local = """
            namespace LibA;

            public static class LocalUses
            {
                public static int Twice(int x) => MathOps.Add(x, x);
            }
            """;
        const string outside = """
            namespace Outsider;
            using LibA;

            public static class OutsideUses
            {
                public static int Remote(int x) => MathOps.Add(x, 1);
            }
            """;

        solution = solution.AddDocument(libAOps, "MathOps.cs", SourceText.From(ops), filePath: Path.Combine(root, "LibA", "MathOps.cs"));
        solution = solution.AddDocument(libAUses, "LocalUses.cs", SourceText.From(local), filePath: Path.Combine(root, "LibA", "LocalUses.cs"));
        solution = solution.AddDocument(outsiderDoc, "OutsideUses.cs", SourceText.From(outside), filePath: Path.Combine(root, "Outsider", "OutsideUses.cs"));
        solution = solution.AddProjectReference(outsiderId, new ProjectReference(libAId));
        solution = solution.WithProjectCompilationOptions(libAId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.WithProjectCompilationOptions(outsiderId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(libAId, mscorlib);
        solution = solution.AddMetadataReference(outsiderId, mscorlib);
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace callers-graph fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    /// <summary>
    /// Coverage-gap fixture (issue #559): LibA defines MathOps.Add.
    /// CallerHealthy holds a project reference to LibA and calls it — searched.
    /// CallerBroken declares a project chain through Mid to LibA, but no usable
    /// reference to LibA materialized (its call cannot bind — the evaluation
    /// "project reference without matching metadata reference" shape).
    /// CallerMeta references the emitted LibA image only — its calls bind, yet
    /// a source-originated search still skips it (the nuget-consumer shape).
    /// The default scope covers LibA plus dependency-graph dependents (Mid,
    /// CallerBroken, CallerHealthy); CallerMeta only enters via entireSolution.
    /// </summary>
    public static FakeSolutionLoader ImmediateWithCallersCoverageGap(string root = @"C:\fake") =>
        new(TimeSpan.Zero, () => CreateCallersCoverageGapLoaded(root));

    public static LoadedSolution CreateCallersCoverageGapLoaded(string root = @"C:\fake")
    {
        var workspace = new AdhocWorkspace();
        var libAId = ProjectId.CreateNewId();
        var midId = ProjectId.CreateNewId();
        var healthyId = ProjectId.CreateNewId();
        var brokenId = ProjectId.CreateNewId();
        var metaId = ProjectId.CreateNewId();
        var libAOps = DocumentId.CreateNewId(libAId);
        var libAUses = DocumentId.CreateNewId(libAId);
        var midDoc = DocumentId.CreateNewId(midId);
        var healthyDoc = DocumentId.CreateNewId(healthyId);
        var brokenDoc = DocumentId.CreateNewId(brokenId);
        var metaDoc = DocumentId.CreateNewId(metaId);
        var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var solution = workspace.CurrentSolution;

        var libAImage = EmitLibrary(
            "LibA",
            "namespace LibA; public static class MathOps { public static int Add(int a, int b) => a + b; }",
            [mscorlib]);

        solution = solution.AddProject(ProjectInfo.Create(
            libAId, VersionStamp.Create(), "LibA", "LibA", LanguageNames.CSharp,
            filePath: Path.Combine(root, "LibA", "LibA.csproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            midId, VersionStamp.Create(), "Mid", "Mid", LanguageNames.CSharp,
            filePath: Path.Combine(root, "Mid", "Mid.csproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            healthyId, VersionStamp.Create(), "CallerHealthy", "CallerHealthy", LanguageNames.CSharp,
            filePath: Path.Combine(root, "CallerHealthy", "CallerHealthy.csproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            brokenId, VersionStamp.Create(), "CallerBroken", "CallerBroken", LanguageNames.CSharp,
            filePath: Path.Combine(root, "CallerBroken", "CallerBroken.csproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            metaId, VersionStamp.Create(), "CallerMeta", "CallerMeta", LanguageNames.CSharp,
            filePath: Path.Combine(root, "CallerMeta", "CallerMeta.csproj")));

        const string ops = """
            namespace LibA;

            public static class MathOps
            {
                public static int Add(int a, int b) => a + b;
            }
            """;
        const string local = """
            namespace LibA;

            public static class LocalUses
            {
                public static int Twice(int x) => MathOps.Add(x, x);
            }
            """;
        const string midUses = """
            namespace Mid;
            using LibA;

            public static class MidUses
            {
                public static int Deep(int x) => MathOps.Add(x, 1);
            }
            """;
        const string healthyUses = """
            namespace CallerHealthy;
            using LibA;

            public static class HealthyUses
            {
                public static int Remote(int x) => MathOps.Add(x, 2);
            }
            """;
        const string brokenUses = """
            namespace CallerBroken;
            using LibA;

            public static class BrokenUses
            {
                public static int Remote(int x) => MathOps.Add(x, 3);
            }
            """;
        const string metaUses = """
            namespace CallerMeta;
            using LibA;

            public static class MetaUses
            {
                public static int Remote(int x) => MathOps.Add(x, 4);
            }
            """;

        solution = solution.AddDocument(libAOps, "MathOps.cs", SourceText.From(ops), filePath: Path.Combine(root, "LibA", "MathOps.cs"));
        solution = solution.AddDocument(libAUses, "LocalUses.cs", SourceText.From(local), filePath: Path.Combine(root, "LibA", "LocalUses.cs"));
        solution = solution.AddDocument(midDoc, "MidUses.cs", SourceText.From(midUses), filePath: Path.Combine(root, "Mid", "MidUses.cs"));
        solution = solution.AddDocument(healthyDoc, "HealthyUses.cs", SourceText.From(healthyUses), filePath: Path.Combine(root, "CallerHealthy", "HealthyUses.cs"));
        solution = solution.AddDocument(brokenDoc, "BrokenUses.cs", SourceText.From(brokenUses), filePath: Path.Combine(root, "CallerBroken", "BrokenUses.cs"));
        solution = solution.AddDocument(metaDoc, "MetaUses.cs", SourceText.From(metaUses), filePath: Path.Combine(root, "CallerMeta", "MetaUses.cs"));

        // CallerBroken's only declared edge lands on Mid — the transitive LibA
        // reference never materializes, so its MathOps.Add call cannot bind.
        solution = solution.AddProjectReference(brokenId, new ProjectReference(midId));
        solution = solution.AddProjectReference(midId, new ProjectReference(libAId));
        solution = solution.AddProjectReference(healthyId, new ProjectReference(libAId));

        foreach (var id in new[] { libAId, midId, healthyId, brokenId, metaId })
        {
            solution = solution.WithProjectCompilationOptions(
                id, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        foreach (var id in new[] { libAId, midId, healthyId, brokenId, metaId })
        {
            solution = solution.AddMetadataReference(id, mscorlib);
        }

        // CallerMeta consumes the compiled LibA image like a package consumer.
        solution = solution.AddMetadataReference(metaId, MetadataReference.CreateFromImage(libAImage));

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace callers coverage-gap fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    /// <summary>
    /// Materialization-gap fixture (issue #565): LibMod emits a NetModule, so
    /// a declared ProjectReference to it produces no metadata reference at all
    /// (Roslyn cannot reference a module) — the same observable state
    /// MSBuildWorkspace leaves when the referenced project's output dll is not
    /// on disk. CallerDecl declares the edge and calls ModOps.Sub, but the
    /// call cannot bind: the finder returns no hits there, and the coverage
    /// gate must name it with the materialization-failure reason instead of
    /// counting the declared edge as searched. ZeroRefA and ZeroRefB share the
    /// scope under entireSolution yet hold no reference of any kind to LibMod,
    /// so they collapse to a count instead of individual names.
    /// </summary>
    public static FakeSolutionLoader ImmediateWithMaterializationGap(string root = @"C:\fake") =>
        new(TimeSpan.Zero, () => CreateMaterializationGapLoaded(root));

    public static LoadedSolution CreateMaterializationGapLoaded(string root = @"C:\fake")
    {
        var workspace = new AdhocWorkspace();
        var libModId = ProjectId.CreateNewId();
        var declId = ProjectId.CreateNewId();
        var zeroAId = ProjectId.CreateNewId();
        var zeroBId = ProjectId.CreateNewId();
        var libModOps = DocumentId.CreateNewId(libModId);
        var libModUses = DocumentId.CreateNewId(libModId);
        var declDoc = DocumentId.CreateNewId(declId);
        var zeroADoc = DocumentId.CreateNewId(zeroAId);
        var zeroBDoc = DocumentId.CreateNewId(zeroBId);
        var refs = TrustedPlatformReferences();
        var solution = workspace.CurrentSolution;

        solution = solution.AddProject(ProjectInfo.Create(
            libModId, VersionStamp.Create(), "LibMod", "LibMod", LanguageNames.CSharp,
            filePath: Path.Combine(root, "LibMod", "LibMod.csproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            declId, VersionStamp.Create(), "CallerDecl", "CallerDecl", LanguageNames.CSharp,
            filePath: Path.Combine(root, "CallerDecl", "CallerDecl.csproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            zeroAId, VersionStamp.Create(), "ZeroRefA", "ZeroRefA", LanguageNames.CSharp,
            filePath: Path.Combine(root, "ZeroRefA", "ZeroRefA.csproj")));
        solution = solution.AddProject(ProjectInfo.Create(
            zeroBId, VersionStamp.Create(), "ZeroRefB", "ZeroRefB", LanguageNames.CSharp,
            filePath: Path.Combine(root, "ZeroRefB", "ZeroRefB.csproj")));

        const string ops = """
            namespace LibMod;

            public static class ModOps
            {
                public static int Sub(int a, int b) => a - b;
            }
            """;
        const string local = """
            namespace LibMod;

            public static class LocalSub
            {
                public static int Twice(int x) => ModOps.Sub(x, 1) + ModOps.Sub(x, 2);
            }
            """;
        const string declUses = """
            namespace CallerDecl;
            using LibMod;

            public static class DeclUses
            {
                public static int Remote(int x) => ModOps.Sub(x, 1);
            }
            """;
        const string zeroASource = """
            namespace ZeroRefA;

            public static class Idle
            {
                public static int Answer() => 42;
            }
            """;
        const string zeroBSource = """
            namespace ZeroRefB;

            public static class Idle
            {
                public static string Echo(string s) => s;
            }
            """;

        solution = solution.AddDocument(libModOps, "ModOps.cs", SourceText.From(ops), filePath: Path.Combine(root, "LibMod", "ModOps.cs"));
        solution = solution.AddDocument(libModUses, "LocalSub.cs", SourceText.From(local), filePath: Path.Combine(root, "LibMod", "LocalSub.cs"));
        solution = solution.AddDocument(declDoc, "DeclUses.cs", SourceText.From(declUses), filePath: Path.Combine(root, "CallerDecl", "DeclUses.cs"));
        solution = solution.AddDocument(zeroADoc, "Idle.cs", SourceText.From(zeroASource), filePath: Path.Combine(root, "ZeroRefA", "Idle.cs"));
        solution = solution.AddDocument(zeroBDoc, "Idle.cs", SourceText.From(zeroBSource), filePath: Path.Combine(root, "ZeroRefB", "Idle.cs"));

        // CallerDecl's declared edge to LibMod never materializes: a NetModule
        // target yields no metadata reference (Roslyn's GetMetadataReferenceAsync
        // returns null for OutputKind.NetModule), so the compilation holds no
        // assembly symbol named 'LibMod' and DeclUses's call cannot bind.
        solution = solution.AddProjectReference(declId, new ProjectReference(libModId));

        solution = solution.WithProjectCompilationOptions(
            libModId, new CSharpCompilationOptions(OutputKind.NetModule));
        foreach (var id in new[] { declId, zeroAId, zeroBId })
        {
            solution = solution.WithProjectCompilationOptions(
                id, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        foreach (var id in new[] { libModId, declId, zeroAId, zeroBId })
        {
            foreach (var metadata in refs)
            {
                solution = solution.AddMetadataReference(id, metadata);
            }
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace materialization-gap fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    /// <summary>
    /// Declared-edge gap fixture (issue #570), multi-TFM: LibNet8 and LibNet9
    /// are two project units of one project file sharing a linked document,
    /// LibNetStd is a third unit of the same file that does not link the
    /// declaring document. DepMissing and DepOrphan declare a
    /// <c>&lt;ProjectReference&gt;</c> to Lib.csproj only in their project
    /// files — the loaded graph holds no edge (or an edge whose synthesized
    /// id resolves to no project), matching what MSBuildWorkspace leaves
    /// when it cannot resolve the target. DepNet9's declared edge
    /// materialized normally (calls bind, the engine searches it), DepAlt's
    /// materialized to the non-origination LibNetStd unit, and ZeroRef never
    /// referenced anything. <paramref name="root"/> must be a real directory:
    /// the coverage check reads the declared items from the project files,
    /// so the fixture writes them to disk.
    /// </summary>
    public static LoadedSolution CreateDeclaredEdgeGapLoaded(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        Directory.CreateDirectory(Path.Combine(root, "DepMissing"));
        Directory.CreateDirectory(Path.Combine(root, "DepOrphan"));
        Directory.CreateDirectory(Path.Combine(root, "DepNet9"));
        Directory.CreateDirectory(Path.Combine(root, "DepAlt"));
        Directory.CreateDirectory(Path.Combine(root, "ZeroRef"));

        File.WriteAllText(Path.Combine(root, "Lib", "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(Path.Combine(root, "ZeroRef", "ZeroRef.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        const string declaresLib = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(Path.Combine(root, "DepMissing", "DepMissing.csproj"), declaresLib);
        File.WriteAllText(Path.Combine(root, "DepOrphan", "DepOrphan.csproj"), declaresLib);
        File.WriteAllText(Path.Combine(root, "DepNet9", "DepNet9.csproj"), declaresLib);
        File.WriteAllText(Path.Combine(root, "DepAlt", "DepAlt.csproj"), declaresLib);

        var workspace = new AdhocWorkspace();
        var libNet8Id = ProjectId.CreateNewId();
        var libNet9Id = ProjectId.CreateNewId();
        var libNetStdId = ProjectId.CreateNewId();
        var depMissingId = ProjectId.CreateNewId();
        var depOrphanId = ProjectId.CreateNewId();
        var depNet9Id = ProjectId.CreateNewId();
        var depAltId = ProjectId.CreateNewId();
        var zeroRefId = ProjectId.CreateNewId();
        var refs = TrustedPlatformReferences();
        var solution = workspace.CurrentSolution;

        ProjectId AddUnit(ProjectId id, string name)
        {
            solution = solution.AddProject(ProjectInfo.Create(
                id,
                VersionStamp.Create(),
                name,
                "Lib",
                LanguageNames.CSharp,
                filePath: Path.Combine(root, "Lib", "Lib.csproj")));
            return id;
        }

        AddUnit(libNet8Id, "LibNet8");
        AddUnit(libNet9Id, "LibNet9");
        AddUnit(libNetStdId, "LibNetStd");
        foreach (var (id, name) in new[]
        {
            (depMissingId, "DepMissing"),
            (depOrphanId, "DepOrphan"),
            (depNet9Id, "DepNet9"),
            (depAltId, "DepAlt"),
            (zeroRefId, "ZeroRef"),
        })
        {
            solution = solution.AddProject(ProjectInfo.Create(
                id,
                VersionStamp.Create(),
                name,
                name,
                LanguageNames.CSharp,
                filePath: Path.Combine(root, name, name + ".csproj")));
        }

        const string ops = """
            namespace Lib;

            public static class LibOps
            {
                public static int Jump(int x) => x + 1;
            }
            """;
        var linkedOpsPath = Path.Combine(root, "Lib", "Ops.cs");
        solution = solution.AddDocument(
            DocumentId.CreateNewId(libNet8Id), "Ops.cs", SourceText.From(ops), filePath: linkedOpsPath);
        solution = solution.AddDocument(
            DocumentId.CreateNewId(libNet9Id), "Ops.cs", SourceText.From(ops), filePath: linkedOpsPath);
        solution = solution.AddDocument(
            DocumentId.CreateNewId(libNetStdId),
            "OpsNetStandard.cs",
            SourceText.From(ops),
            filePath: Path.Combine(root, "Lib", "OpsNetStandard.cs"));

        const string uses = """
            namespace Dep;

            using Lib;

            public static class Uses
            {
                public static int Go(int x) => LibOps.Jump(x);
            }
            """;
        foreach (var (id, file) in new[]
        {
            (depMissingId, "DepMissingUses.cs"),
            (depOrphanId, "DepOrphanUses.cs"),
            (depNet9Id, "DepNet9Uses.cs"),
            (depAltId, "DepAltUses.cs"),
        })
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(id), file, SourceText.From(uses),
                filePath: Path.Combine(root, id == depMissingId ? "DepMissing"
                    : id == depOrphanId ? "DepOrphan"
                    : id == depNet9Id ? "DepNet9"
                    : "DepAlt", file));
        }

        solution = solution.AddDocument(
            DocumentId.CreateNewId(zeroRefId),
            "Idle.cs",
            SourceText.From("namespace ZeroRef; public static class Idle { public static int Answer() => 42; }"),
            filePath: Path.Combine(root, "ZeroRef", "Idle.cs"));

        // DepMissing: declared in the project file only — no graph edge at
        // all. DepOrphan: an edge whose synthesized id resolves to no loaded
        // project. DepNet9 / DepAlt: materialized edges (to the net9 twin and
        // to the non-origination netstandard unit respectively).
        solution = solution.AddProjectReference(depOrphanId, new ProjectReference(ProjectId.CreateNewId()));
        solution = solution.AddProjectReference(depNet9Id, new ProjectReference(libNet9Id));
        solution = solution.AddProjectReference(depAltId, new ProjectReference(libNetStdId));

        foreach (var id in new[]
        {
            libNet8Id, libNet9Id, libNetStdId,
            depMissingId, depOrphanId, depNet9Id, depAltId, zeroRefId,
        })
        {
            solution = solution.WithProjectCompilationOptions(
                id, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            foreach (var metadata in refs)
            {
                solution = solution.AddMetadataReference(id, metadata);
            }
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace declared-edge-gap fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    /// <summary>
    /// Searched-degraded fixture (issue #572): every Dep* project holds a
    /// materialized edge to Lib so the engine gate counts it searched, but
    /// <paramref name="root"/> is a real directory whose project files drive
    /// the post-search fidelity check. DepUnrestored declares a
    /// <c>&lt;PackageReference&gt;</c> yet has no
    /// <c>obj/project.assets.json</c> — the unrestored shape that strips
    /// every package reference without a trace — and its compilation holds
    /// no PortableExecutableReference at all, so its calls cannot bind.
    /// DepNoFramework declares a plain <c>&lt;Reference&gt;</c> (no package
    /// item, so the assets-file signal does not apply) and likewise holds
    /// zero PortableExecutableReferences. DepRestored is the control — same
    /// declared package reference plus a written assets file and real
    /// framework references — and DepPlain declares only a
    /// <c>&lt;ProjectReference&gt;</c>, which the fidelity check skips.
    /// </summary>
    public static LoadedSolution CreateSearchedDegradedGapLoaded(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "Lib"));
        Directory.CreateDirectory(Path.Combine(root, "DepUnrestored"));
        Directory.CreateDirectory(Path.Combine(root, "DepNoFramework"));
        Directory.CreateDirectory(Path.Combine(root, "DepRestored", "obj"));
        Directory.CreateDirectory(Path.Combine(root, "DepPlain"));

        File.WriteAllText(Path.Combine(root, "Lib", "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(Path.Combine(root, "DepUnrestored", "DepUnrestored.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "DepNoFramework", "DepNoFramework.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Reference Include="SomeVendor.dll" />
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "DepRestored", "DepRestored.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, "DepRestored", "obj", "project.assets.json"), "{}");
        File.WriteAllText(Path.Combine(root, "DepPlain", "DepPlain.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);

        var workspace = new AdhocWorkspace();
        var libId = ProjectId.CreateNewId();
        var depUnrestoredId = ProjectId.CreateNewId();
        var depNoFrameworkId = ProjectId.CreateNewId();
        var depRestoredId = ProjectId.CreateNewId();
        var depPlainId = ProjectId.CreateNewId();
        var refs = TrustedPlatformReferences();
        var solution = workspace.CurrentSolution;

        solution = solution.AddProject(ProjectInfo.Create(
            libId, VersionStamp.Create(), "Lib", "Lib", LanguageNames.CSharp,
            filePath: Path.Combine(root, "Lib", "Lib.csproj")));
        foreach (var (id, name) in new[]
        {
            (depUnrestoredId, "DepUnrestored"),
            (depNoFrameworkId, "DepNoFramework"),
            (depRestoredId, "DepRestored"),
            (depPlainId, "DepPlain"),
        })
        {
            solution = solution.AddProject(ProjectInfo.Create(
                id, VersionStamp.Create(), name, name, LanguageNames.CSharp,
                filePath: Path.Combine(root, name, name + ".csproj")));
        }

        const string ops = """
            namespace Lib;

            public static class LibOps
            {
                public static int Jump(int x) => x + 1;
            }
            """;
        const string localUses = """
            namespace Lib;

            public static class LocalUses
            {
                public static int Twice(int x) => LibOps.Jump(x) + LibOps.Jump(x);
            }
            """;
        const string depUses = """
            namespace Dep;

            using Lib;

            public static class Uses
            {
                public static int Go(int x) => LibOps.Jump(x);
            }
            """;

        solution = solution.AddDocument(
            DocumentId.CreateNewId(libId), "Ops.cs", SourceText.From(ops),
            filePath: Path.Combine(root, "Lib", "Ops.cs"));
        solution = solution.AddDocument(
            DocumentId.CreateNewId(libId), "LocalUses.cs", SourceText.From(localUses),
            filePath: Path.Combine(root, "Lib", "LocalUses.cs"));
        foreach (var (id, name) in new[]
        {
            (depUnrestoredId, "DepUnrestored"),
            (depNoFrameworkId, "DepNoFramework"),
            (depRestoredId, "DepRestored"),
            (depPlainId, "DepPlain"),
        })
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(id), name + "Uses.cs", SourceText.From(depUses),
                filePath: Path.Combine(root, name, name + "Uses.cs"));
        }

        // Every dependent's edge to Lib materializes as a CompilationReference,
        // so the engine gate counts all four searched — the fidelity check is
        // what separates DepUnrestored/DepNoFramework (degraded) from
        // DepRestored/DepPlain (whole).
        foreach (var id in new[] { depUnrestoredId, depNoFrameworkId, depRestoredId, depPlainId })
        {
            solution = solution.AddProjectReference(id, new ProjectReference(libId));
        }

        foreach (var id in new[]
        {
            libId, depUnrestoredId, depNoFrameworkId, depRestoredId, depPlainId,
        })
        {
            solution = solution.WithProjectCompilationOptions(
                id, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        // Lib, DepRestored, and DepPlain get real framework references;
        // DepUnrestored and DepNoFramework intentionally get none — zero
        // PortableExecutableReferences is the extreme unrestored shape.
        foreach (var id in new[] { libId, depRestoredId, depPlainId })
        {
            foreach (var metadata in refs)
            {
                solution = solution.AddMetadataReference(id, metadata);
            }
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace searched-degraded fixture.");
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
            "System.Runtime.InteropServices.dll",
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
    }
}

