using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using DotNetMcp.Core;

namespace DotNetMcp.Tests;

public class FindRefsScopesTests
{
    [Fact]
    public void ProjectsInClosure_is_defining_project_plus_dependents_not_outgoing_references()
    {
        var workspace = new AdhocWorkspace();
        var coreId = ProjectId.CreateNewId();
        var libId = ProjectId.CreateNewId();
        var appId = ProjectId.CreateNewId();
        var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(coreId, VersionStamp.Create(), "Core", "Core", LanguageNames.CSharp))
            .AddProject(ProjectInfo.Create(libId, VersionStamp.Create(), "Lib", "Lib", LanguageNames.CSharp))
            .AddProject(ProjectInfo.Create(appId, VersionStamp.Create(), "App", "App", LanguageNames.CSharp));

        solution = solution.AddDocument(DocumentId.CreateNewId(coreId), "Core.cs", SourceText.From("namespace Core; public class C {}"));
        solution = solution.AddDocument(DocumentId.CreateNewId(libId), "Lib.cs", SourceText.From("namespace Lib; public class Foo {}"));
        solution = solution.AddDocument(DocumentId.CreateNewId(appId), "App.cs", SourceText.From("namespace App; public class Uses { public static object X => new Lib.Foo(); }"));
        solution = solution.AddProjectReference(libId, new ProjectReference(coreId));
        solution = solution.AddProjectReference(appId, new ProjectReference(libId));
        solution = solution.AddMetadataReference(coreId, mscorlib);
        solution = solution.AddMetadataReference(libId, mscorlib);
        solution = solution.AddMetadataReference(appId, mscorlib);
        Assert.True(workspace.TryApplyChanges(solution));
        solution = workspace.CurrentSolution;

        var lib = solution.GetProject(libId)!;
        var names = FindRefsScopes.ProjectsInClosure(solution, lib).Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "App", "Lib" }, names);
        Assert.DoesNotContain("Core", names);
    }
}
