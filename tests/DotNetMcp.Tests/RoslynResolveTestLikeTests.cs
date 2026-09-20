using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class RoslynResolveTestLikeTests
{
    [Theory]
    [InlineData("Contest", false)]
    [InlineData("Latest", false)]
    [InlineData("Attest", false)]
    [InlineData("Foo.Tests", true)]
    [InlineData("Foo.Test", true)]
    [InlineData("Foo.Benches", true)]
    [InlineData("Tests", true)]
    public void IsTestLikeProject_uses_name_segments(string name, bool expected) =>
        Assert.Equal(expected, RoslynLanguageAdapter.IsTestLikeProject(name));

    [Fact]
    public async Task resolve_without_projectId_does_not_skip_Contest_on_cold_or_warm_path()
    {
        using var workspace = CreateContestWorkspace();
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var lru = new CompilationLru(50);
        using var session = new WorkspaceSession(loaded, epoch: 1, compilationLru: lru);
        var adapter = new RoslynLanguageAdapter(new GeneratorQueryService());

        var (cold, coldError) = await adapter.ResolveByNameAsync(session, "Marker");
        Assert.Null(coldError);
        Assert.NotNull(cold);
        Assert.Equal("Contest", ProjectName(workspace.CurrentSolution, cold!.Handle));

        await session.GetCompilationAsync(workspace.CurrentSolution.Projects.Single(p => p.Name == "Contest").Id);
        var (warm, warmError) = await adapter.ResolveByNameAsync(session, "Marker");
        Assert.Null(warmError);
        Assert.NotNull(warm);
        Assert.Equal(cold.Handle, warm!.Handle);
        Assert.Equal("Contest", ProjectName(workspace.CurrentSolution, warm.Handle));
    }

    [Fact]
    public async Task resolve_with_explicit_projectId_still_hits_Foo_Tests()
    {
        using var workspace = CreateContestWorkspace();
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        using var session = new WorkspaceSession(loaded, epoch: 1);
        var adapter = new RoslynLanguageAdapter(new GeneratorQueryService());
        var tests = workspace.CurrentSolution.Projects.Single(p => p.Name == "Foo.Tests");

        var (success, error) = await adapter.ResolveByNameAsync(
            session, "TestOnly", tests.Id.Id.ToString("D"));
        Assert.Null(error);
        Assert.NotNull(success);
        Assert.Equal("TestOnly", success!.Summary.DisplayName);
    }

    [Fact]
    public async Task resolve_soft_budget_without_hits_is_soft_budget_exceeded()
    {
        using var workspace = CreateContestWorkspace();
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        using var session = new WorkspaceSession(loaded, epoch: 1);
        var adapter = new RoslynLanguageAdapter(
            new GeneratorQueryService(),
            new SoftBudgetOptions { SingleProjectCompile = TimeSpan.Zero });

        var (_, error) = await adapter.ResolveByNameAsync(session, "DoesNotExist");
        Assert.IsType<SoftBudgetExceededError>(error);
    }

    [Fact]
    public async Task resolve_soft_budget_with_warm_hit_is_not_unique_success()
    {
        using var workspace = CreateTwoMainWorkspace();
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var lru = new CompilationLru(50);
        using var session = new WorkspaceSession(loaded, epoch: 1, compilationLru: lru);
        var libA = workspace.CurrentSolution.Projects.Single(p => p.Name == "LibA");
        await session.GetCompilationAsync(libA.Id);

        var adapter = new RoslynLanguageAdapter(
            new GeneratorQueryService(),
            new SoftBudgetOptions { SingleProjectCompile = TimeSpan.Zero });

        var (success, error) = await adapter.ResolveByNameAsync(session, "Marker");

        Assert.Null(success);
        Assert.IsType<SoftBudgetExceededError>(error);
        Assert.Contains("projectId", error!.SuggestedAction, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SymbolNotFound", error.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task resolve_same_name_in_two_main_projects_is_ambiguous_cold_and_warm()
    {
        using var workspace = CreateTwoMainWorkspace();
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var lru = new CompilationLru(50);
        using var session = new WorkspaceSession(loaded, epoch: 1, compilationLru: lru);
        var adapter = new RoslynLanguageAdapter(new GeneratorQueryService());

        var (_, coldError) = await adapter.ResolveByNameAsync(session, "Marker");
        Assert.IsType<SymbolAmbiguousError>(coldError);

        var first = workspace.CurrentSolution.Projects.First();
        await session.GetCompilationAsync(first.Id);
        var (_, warmError) = await adapter.ResolveByNameAsync(session, "Marker");
        Assert.IsType<SymbolAmbiguousError>(warmError);
    }

    private static string ProjectName(Solution solution, string handle)
    {
        Assert.True(SymbolHandle.TryParse(handle, out var parsed, out _));
        return Assert.Single(solution.Projects, p =>
            string.Equals(p.Id.Id.ToString("D"), parsed!.ProjectId, StringComparison.OrdinalIgnoreCase)).Name;
    }

    private static AdhocWorkspace CreateContestWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var contestId = ProjectId.CreateNewId();
        var testsId = ProjectId.CreateNewId();
        var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(contestId, VersionStamp.Create(), "Contest", "Contest", LanguageNames.CSharp))
            .AddProject(ProjectInfo.Create(testsId, VersionStamp.Create(), "Foo.Tests", "Foo.Tests", LanguageNames.CSharp));
        solution = solution.AddDocument(DocumentId.CreateNewId(contestId), "Marker.cs", SourceText.From("public class Marker {}"));
        solution = solution.AddDocument(DocumentId.CreateNewId(testsId), "TestOnly.cs", SourceText.From("public class TestOnly {}"));
        solution = solution.AddMetadataReference(contestId, mscorlib);
        solution = solution.AddMetadataReference(testsId, mscorlib);
        solution = solution.WithProjectCompilationOptions(contestId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.WithProjectCompilationOptions(testsId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(workspace.TryApplyChanges(solution));
        return workspace;
    }

    private static AdhocWorkspace CreateTwoMainWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var alphaId = ProjectId.CreateNewId();
        var betaId = ProjectId.CreateNewId();
        var mscorlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(alphaId, VersionStamp.Create(), "LibA", "LibA", LanguageNames.CSharp))
            .AddProject(ProjectInfo.Create(betaId, VersionStamp.Create(), "LibB", "LibB", LanguageNames.CSharp));
        solution = solution.AddDocument(DocumentId.CreateNewId(alphaId), "Marker.cs", SourceText.From("public class Marker {}"));
        solution = solution.AddDocument(DocumentId.CreateNewId(betaId), "Marker.cs", SourceText.From("public class Marker {}"));
        solution = solution.AddMetadataReference(alphaId, mscorlib);
        solution = solution.AddMetadataReference(betaId, mscorlib);
        solution = solution.WithProjectCompilationOptions(alphaId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.WithProjectCompilationOptions(betaId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(workspace.TryApplyChanges(solution));
        return workspace;
    }
}
