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
            FindRefsScopeKind.DependencyClosure => CollectClosureDocuments(solution, project),
            FindRefsScopeKind.EntireSolution => solution.Projects
                .SelectMany(p => p.Documents)
                .ToImmutableHashSet(),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
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
