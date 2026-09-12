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
    public static ImmutableHashSet<Document> DocumentsForScope(
        Solution solution,
        Project project,
        FindRefsScopeKind scope)
    {
        return scope switch
        {
            FindRefsScopeKind.DependencyClosure => ProjectsInClosure(solution, project)
                .SelectMany(p => p.Documents)
                .ToImmutableHashSet(),
            FindRefsScopeKind.EntireSolution => solution.Projects
                .SelectMany(p => p.Documents)
                .ToImmutableHashSet(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
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
