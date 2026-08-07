using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace S2.Core;

public enum FindRefsScopeKind
{
    CurrentProject,
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
            FindRefsScopeKind.CurrentProject => project.Documents.ToImmutableHashSet(),
            FindRefsScopeKind.DependencyClosure => CollectClosureDocuments(solution, project),
            FindRefsScopeKind.EntireSolution => solution.Projects
                .SelectMany(p => p.Documents)
                .ToImmutableHashSet(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
    }

    public static async Task<IEnumerable<ReferencedSymbol>> FindReferencesAsync(
        ISymbol symbol,
        Solution solution,
        Project project,
        FindRefsScopeKind scope,
        CancellationToken ct = default)
    {
        if (scope == FindRefsScopeKind.EntireSolution)
        {
            return await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
        }

        var documents = DocumentsForScope(solution, project, scope);
        return await SymbolFinder.FindReferencesAsync(symbol, solution, documents, ct);
    }

    private static ImmutableHashSet<Document> CollectClosureDocuments(Solution solution, Project project)
    {
        var visited = new HashSet<ProjectId>();
        var stack = new Stack<ProjectId>();
        stack.Push(project.Id);
        var docs = ImmutableHashSet.CreateBuilder<Document>();

        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!visited.Add(id))
            {
                continue;
            }

            var p = solution.GetProject(id);
            if (p is null)
            {
                continue;
            }

            foreach (var d in p.Documents)
            {
                docs.Add(d);
            }

            foreach (var reference in p.ProjectReferences)
            {
                stack.Push(reference.ProjectId);
            }
        }

        return docs.ToImmutable();
    }
}
