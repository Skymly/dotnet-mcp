using CSharpSyntax = Microsoft.CodeAnalysis.CSharp.Syntax;
using VbSyntax = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>Match the FCS declaration ID within the F# producer output actually bound by a Roslyn consumer.</summary>
public static class FSharpConsumerReferences
{
    public sealed record SearchResult(IReadOnlyList<ReferenceLocationItem> Hits, ReferenceProjectCoverage Coverage);

    public static IReadOnlyList<FSharpDependentProject> Scope(
        IWorkspaceSession session, FSharpProjectSnapshot defining, bool entireSolution)
    {
        var producer = ProjectById(session, defining.ProjectId);
        var paths = OutputPaths(producer);
        var dependents = defining.DependentProjects.Select(d => d.ProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return session.Solution.Projects
            .Where(p => p.Language is LanguageNames.CSharp or LanguageNames.VisualBasic &&
                session.FSharpSnapshot.FindProject(p.Id.Id.ToString("D")) is null)
            .Where(p => entireSolution || dependents.Contains(p.Id.Id.ToString("D")) ||
                p.MetadataReferences.OfType<PortableExecutableReference>().Any(pe => paths.Contains(pe.FilePath)))
            .Select(p => new FSharpDependentProject(p.Id.Id.ToString("D"), p.Name, RoslynLanguageAdapter.LanguageToken(p.Language)))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.ProjectId, StringComparer.Ordinal).ToArray();
    }

    public static async Task<SearchResult> SearchAsync(
        IWorkspaceSession session, FSharpProjectSnapshot defining, FSharpDependentProject consumer,
        string? declarationId, CancellationToken ct)
    {
        try
        {
            return await SearchCompilationAsync(session, defining, consumer, declarationId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            ct.ThrowIfCancellationRequested();
            return Unavailable(consumer, $"The consumer could not complete semantic binding ({ex.GetType().Name}).");
        }
    }

    private static SearchResult Unavailable(FSharpDependentProject consumer, string reason) =>
        new([], new(consumer.ProjectId, consumer.Name, consumer.Language, "unavailable", reason));

    private static async Task<SearchResult> SearchCompilationAsync(
        IWorkspaceSession session, FSharpProjectSnapshot defining, FSharpDependentProject consumer,
        string? declarationId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(declarationId))
        {
            return Unavailable(consumer, "The F# symbol has no verified public CLR declaration ID.");
        }

        var project = ProjectById(session, consumer.ProjectId);
        var producer = ProjectById(session, defining.ProjectId);
        if (project is null || producer is null || !project.SupportsCompilation)
        {
            return Unavailable(consumer, "The producer or consumer is missing from the loaded compilation graph.");
        }

        var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
        if (compilation is null)
        {
            return Unavailable(consumer, "The consumer compilation is unavailable.");
        }

        var paths = OutputPaths(producer);
        var assemblies = compilation.References.OfType<PortableExecutableReference>()
            .Where(pe => paths.Contains(pe.FilePath) && !session.Solution.Projects.Any(other =>
                other.Id != producer.Id && OutputPaths(other).Contains(pe.FilePath)))
            .Select(pe => compilation.GetAssemblyOrModuleSymbol(pe)).OfType<IAssemblySymbol>()
            .ToArray();
        if (assemblies.Length != 1)
        {
            return Unavailable(consumer, "Cannot identify a unique bound reference to the defining project's output. Build its active configuration and reopen.");
        }

        var targets = DocumentationCommentId.GetSymbolsForDeclarationId(declarationId, compilation)
            .Where(symbol => SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, assemblies[0]))
            .ToArray();
        if (targets.Length != 1)
        {
            return Unavailable(consumer, "The FCS declaration ID did not resolve uniquely in the bound producer output.");
        }

        if (targets[0] is IMethodSymbol { MethodKind: not MethodKind.Ordinary } or IPropertySymbol { IsIndexer: true })
        {
            return Unavailable(consumer, "This CLR member can be referenced without an explicit identifier.");
        }

        // Work within each consumer compilation. Solution-wide SymbolFinder tries to
        // compile the F# producer via Roslyn, which cannot supply that compilation.
        var hits = new List<ReferenceLocationItem>();
        foreach (var document in project.Documents)
        {
            ct.ThrowIfCancellationRequested();
            var tree = await document.GetSyntaxTreeAsync(ct).ConfigureAwait(false);
            if (tree is null || document.FilePath is null || !compilation.ContainsSyntaxTree(tree))
            {
                return Unavailable(consumer, "The consumer has a document without a compiler syntax tree.");
            }
            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(ct).ConfigureAwait(false);
            foreach (var node in root.DescendantNodes())
            {
                var name = node switch
                {
                    CSharpSyntax.SimpleNameSyntax cs => cs.Identifier,
                    VbSyntax.SimpleNameSyntax vb => vb.Identifier,
                    _ => default
                };
                if (name.RawKind == 0)
                {
                    continue;
                }
                var symbol = model.GetSymbolInfo(node, ct).Symbol;
                var definition = symbol is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : symbol;
                if (!SymbolEqualityComparer.Default.Equals(definition?.OriginalDefinition, targets[0].OriginalDefinition))
                {
                    continue;
                }
                var pos = tree.GetLineSpan(name.Span, ct);
                hits.Add(new(DeclarationAvailability.InSource, SymbolOrigin.Handwritten, document.FilePath,
                    name.SpanStart, name.Span.Length, pos.StartLinePosition.Line + 1, pos.StartLinePosition.Character + 1,
                    pos.EndLinePosition.Line + 1, pos.EndLinePosition.Character + 1, consumer.ProjectId, ReferenceLocationKind.Reference));
            }
        }
        var ordered = hits.Distinct().OrderBy(hit => hit.FilePath, StringComparer.Ordinal).ThenBy(hit => hit.Start).ToArray();
        var errors = compilation.GetDiagnostics(ct).Any(d => d.Severity == DiagnosticSeverity.Error);
        var status = errors ? "unavailable" : ordered.Length > 0 ? "confirmed" : "noHits";
        return new(ordered, new(consumer.ProjectId, consumer.Name, consumer.Language, status,
            errors ? "The consumer has compiler errors. Any returned hits are confirmed; additional uses may be unresolved." : null));
    }

    private static Project? ProjectById(IWorkspaceSession session, string id) =>
        Guid.TryParse(id, out var guid) ? session.Solution.GetProject(ProjectId.CreateFromSerialized(guid)) : null;

    private static HashSet<string?> OutputPaths(Project? producer) =>
        new[] { producer?.OutputFilePath, producer?.OutputRefFilePath }.Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
}
