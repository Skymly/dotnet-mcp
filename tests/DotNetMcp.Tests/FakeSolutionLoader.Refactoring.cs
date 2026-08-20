using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace DotNetMcp.Tests;

public sealed partial class FakeSolutionLoader
{
    public static FakeSolutionLoader ImmediateWithEncapsulateFieldOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateEncapsulateFieldLoadedOnDisk(projectDir));

    public static FakeSolutionLoader ImmediateWithVbEncapsulateFieldOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateVbEncapsulateFieldLoadedOnDisk(projectDir));

    public static FakeSolutionLoader ImmediateWithProjectFixAllOnDisk(string root) =>
        new(TimeSpan.Zero, () => CreateProjectFixAllLoadedOnDisk(root));

    public static FakeSolutionLoader ImmediateWithVbProjectFixAllOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateVbProjectFixAllLoadedOnDisk(projectDir));

    public static LoadedSolution CreateEncapsulateFieldLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "RefactorApp.csproj");
        var widgetPath = Path.Combine(projectDir, "Widget.cs");
        var callerPath = Path.Combine(projectDir, "Caller.cs");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(widgetPath, """
            namespace RefactorApp;

            public sealed class Widget
            {
                public int count;
            }
            """);
        File.WriteAllText(callerPath, """
            namespace RefactorApp;

            public static class Caller
            {
                public static int Read(Widget widget) => widget.count;
            }
            """);

        return CreateOnDiskProject(
            projectDir,
            projectFilePath,
            "RefactorApp",
            LanguageNames.CSharp,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            error: "Failed to apply AdhocWorkspace encapsulate-field fixture.",
            documents: [("Widget.cs", widgetPath), ("Caller.cs", callerPath)]);
    }

    public static LoadedSolution CreateVbEncapsulateFieldLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "VbRefactorApp.vbproj");
        var widgetPath = Path.Combine(projectDir, "Widget.vb");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(widgetPath, """
            Public Class Widget
                Public count As Integer
            End Class
            """);

        return CreateOnDiskProject(
            projectDir,
            projectFilePath,
            "VbRefactorApp",
            LanguageNames.VisualBasic,
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            ("Widget.vb", widgetPath),
            "Failed to apply AdhocWorkspace VB encapsulate-field fixture.");
    }

    public static LoadedSolution CreateProjectFixAllLoadedOnDisk(string root)
    {
        var aDir = Path.Combine(root, "A");
        var bDir = Path.Combine(root, "B");
        Directory.CreateDirectory(aDir);
        Directory.CreateDirectory(bDir);
        var aProj = Path.Combine(aDir, "FixAllA.csproj");
        var bProj = Path.Combine(bDir, "FixAllB.csproj");
        var onePath = Path.Combine(aDir, "One.cs");
        var twoPath = Path.Combine(aDir, "Two.cs");
        var otherPath = Path.Combine(bDir, "Other.cs");
        File.WriteAllText(aProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(bProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(onePath, """
            namespace FixAllA;

            public sealed class One
            {
                public int A() => new List<int>().Count;
            }
            """);
        File.WriteAllText(twoPath, """
            namespace FixAllA;

            public sealed class Two
            {
                public int B() => new List<string>().Count;
            }
            """);
        File.WriteAllText(otherPath, """
            namespace FixAllB;

            public sealed class Other
            {
                public int C() => new List<long>().Count;
            }
            """);

        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        solution = AddOnDiskCsharpProject(
            solution, "FixAllA", aProj, ("One.cs", onePath), ("Two.cs", twoPath));
        solution = AddOnDiskCsharpProject(
            solution, "FixAllB", bProj, ("Other.cs", otherPath));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace project fix-all fixture.");
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    public static LoadedSolution CreateVbProjectFixAllLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "VbFixAll.vbproj");
        var onePath = Path.Combine(projectDir, "One.vb");
        var twoPath = Path.Combine(projectDir, "Two.vb");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(onePath, """
            Public Class One
                Public Function A() As Integer
                    Return New List(Of Integer)().Count
                End Function
            End Class
            """);
        File.WriteAllText(twoPath, """
            Public Class Two
                Public Function B() As Integer
                    Return New List(Of String)().Count
                End Function
            End Class
            """);

        return CreateOnDiskProject(
            projectDir,
            projectFilePath,
            "VbFixAll",
            LanguageNames.VisualBasic,
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            error: "Failed to apply AdhocWorkspace VB project fix-all fixture.",
            documents: [("One.vb", onePath), ("Two.vb", twoPath)]);
    }

    private static Solution AddOnDiskCsharpProject(
        Solution solution,
        string name,
        string projectFilePath,
        params (string Name, string Path)[] documents)
    {
        var projectId = ProjectId.CreateNewId();
        solution = solution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name,
            name,
            LanguageNames.CSharp,
            filePath: projectFilePath));
        foreach (var (docName, path) in documents)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId),
                docName,
                SourceText.From(File.ReadAllText(path)),
                filePath: path);
        }

        solution = solution.WithProjectCompilationOptions(
            projectId,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        foreach (var metadata in TrustedPlatformReferences().Concat(CollectionsReference()))
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        return solution;
    }
}
