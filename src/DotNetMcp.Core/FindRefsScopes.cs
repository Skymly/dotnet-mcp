using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace DotNetMcp.Core;

public enum FindRefsScopeKind
{
    DependencyClosure,
    EntireSolution,
}

public static class FindRefsScopes
{
    public static async Task<ImmutableHashSet<Document>> DocumentsForScopeAsync(
        Solution solution,
        Project project,
        FindRefsScopeKind scope,
        CancellationToken cancellationToken = default)
    {
        var projects = scope switch
        {
            FindRefsScopeKind.DependencyClosure => ProjectsInClosure(solution, project),
            FindRefsScopeKind.EntireSolution => solution.Projects.ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };

        var documents = new List<Document>();
        foreach (var candidate in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documents.AddRange(candidate.Documents);
            documents.AddRange(await candidate.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false));
        }

        return documents.ToImmutableHashSet();
    }

    /// <summary>
    /// Defining project plus projects that transitively depend on it (consumers).
    /// Callers and references live in dependents, not in outgoing ProjectReferences.
    /// </summary>
    public static IReadOnlyList<Project> ProjectsInClosure(Solution solution, Project project)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(project);

        var graph = solution.GetProjectDependencyGraph();
        var dependentIds = graph.GetProjectsThatTransitivelyDependOnThisProject(project.Id);
        var projects = new List<Project> { project };
        foreach (var id in dependentIds)
        {
            var dependent = solution.GetProject(id);
            if (dependent is not null)
            {
                projects.Add(dependent);
            }
        }

        return projects;
    }

    public static async Task<IEnumerable<ReferencedSymbol>> FindReferencesInDocumentsAsync(
        ISymbol symbol,
        Solution solution,
        IImmutableSet<Document> documents,
        CancellationToken ct = default)
    {
        return await SymbolFinder.FindReferencesAsync(symbol, solution, documents, ct)
            .ConfigureAwait(false);
    }
}
