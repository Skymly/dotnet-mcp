using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

public class FindRefsScopesTests
{
    [Fact]
    public async Task documents_for_scope_includes_source_generated_documents()
    {
        var loaded = FakeSolutionLoader.CreateGeneratorsLoaded();
        using var session = new WorkspaceSession(loaded, epoch: 1);
        var project = Assert.Single(loaded.Solution.Projects);
        _ = await session.GetCompilationAsync(project.Id);

        var documents = await FindRefsScopes.DocumentsForScopeAsync(
            loaded.Solution,
            project,
            FindRefsScopeKind.EntireSolution);

        Assert.Contains(documents, d => d is SourceGeneratedDocument);
        Assert.Contains(documents, d => d.Name == "Host.cs");
    }
}
