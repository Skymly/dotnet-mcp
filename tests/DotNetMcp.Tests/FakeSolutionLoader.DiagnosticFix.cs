using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace DotNetMcp.Tests;

public sealed partial class FakeSolutionLoader
{
    public static FakeSolutionLoader ImmediateWithMissingUsingOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateMissingUsingLoadedOnDisk(projectDir));

    public static FakeSolutionLoader ImmediateWithVbMissingImportOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateVbMissingImportLoadedOnDisk(projectDir));

    public static FakeSolutionLoader ImmediateWithFixAllOnDisk(string projectDir) =>
        new(TimeSpan.Zero, () => CreateFixAllLoadedOnDisk(projectDir));

    public static LoadedSolution CreateMissingUsingLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "FixApp.csproj");
        var brokenPath = Path.Combine(projectDir, "Broken.cs");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(brokenPath, """
            namespace FixApp;

            public sealed class Broken
            {
                public int Count()
                {
                    var items = new List<int>();
                    return items.Count;
                }
            }
            """);

        return CreateOnDiskProject(
            projectDir,
            projectFilePath,
            "FixApp",
            LanguageNames.CSharp,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            ("Broken.cs", brokenPath),
            "Failed to apply AdhocWorkspace missing-using fixture.");
    }

    public static LoadedSolution CreateVbMissingImportLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "VbFixApp.vbproj");
        var brokenPath = Path.Combine(projectDir, "Broken.vb");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(brokenPath, """
            Public Class Broken
                Public Function Count() As Integer
                    Dim items As New List(Of Integer)()
                    Return items.Count
                End Function
            End Class
            """);

        return CreateOnDiskProject(
            projectDir,
            projectFilePath,
            "VbFixApp",
            LanguageNames.VisualBasic,
            new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            ("Broken.vb", brokenPath),
            "Failed to apply AdhocWorkspace VB missing-import fixture.");
    }

    public static LoadedSolution CreateFixAllLoadedOnDisk(string projectDir)
    {
        Directory.CreateDirectory(projectDir);
        var projectFilePath = Path.Combine(projectDir, "FixAllApp.csproj");
        var onePath = Path.Combine(projectDir, "One.cs");
        var twoPath = Path.Combine(projectDir, "Two.cs");
        File.WriteAllText(projectFilePath, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        File.WriteAllText(onePath, """
            namespace FixAllApp;

            public sealed class One
            {
                public int A() => new List<int>().Count;
                public int B() => new List<string>().Count;
            }
            """);
        File.WriteAllText(twoPath, """
            namespace FixAllApp;

            public sealed class Two
            {
                public int C() => new List<long>().Count;
            }
            """);

        return CreateOnDiskProject(
            projectDir,
            projectFilePath,
            "FixAllApp",
            LanguageNames.CSharp,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            error: "Failed to apply AdhocWorkspace fix-all fixture.",
            documents: [("One.cs", onePath), ("Two.cs", twoPath)]);
    }

    private static LoadedSolution CreateOnDiskProject(
        string projectDir,
        string projectFilePath,
        string name,
        string language,
        CompilationOptions options,
        (string Name, string Path) document,
        string error)
        => CreateOnDiskProject(projectDir, projectFilePath, name, language, options, error, [document]);

    private static LoadedSolution CreateOnDiskProject(
        string projectDir,
        string projectFilePath,
        string name,
        string language,
        CompilationOptions options,
        string error,
        params (string Name, string Path)[] documents)
    {
        _ = projectDir;
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name,
            name,
            language,
            filePath: projectFilePath));
        foreach (var (docName, path) in documents)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId),
                docName,
                SourceText.From(File.ReadAllText(path)),
                filePath: path);
        }

        solution = solution.WithProjectCompilationOptions(projectId, options);
        foreach (var metadata in TrustedPlatformReferences().Concat(CollectionsReference()))
        {
            solution = solution.AddMetadataReference(projectId, metadata);
        }

        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException(error);
        }

        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    private static IEnumerable<MetadataReference> CollectionsReference()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var hit = tpa.FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), "System.Collections.dll", StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            yield return MetadataReference.CreateFromFile(hit);
        }
    }
}
