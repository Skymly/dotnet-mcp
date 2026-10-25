using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Core;

public sealed partial class RoslynLanguageAdapter
{
    /// <summary>
    /// Position entry (ADR-0001 Amendment 7). Enumerates the adapter's own
    /// document set — regular documents via GetDocumentIdsWithFilePath plus
    /// source-generated documents per project — then resolves the position in
    /// every matching document (linked files / multi-TFM share one FilePath)
    /// and dedupes hits by signature-qualified name.
    /// </summary>
    public async Task<(SymbolResolveAtSuccess? Success, SymbolQueryError? Error)> ResolveAtPositionAsync(
        IWorkspaceSession session,
        string path,
        int line,
        int column,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var solution = session.Solution;
        var documents = new List<Document>();
        var seen = new HashSet<DocumentId>();
        foreach (var documentId in solution.GetDocumentIdsWithFilePath(path))
        {
            var document = solution.GetDocument(documentId);
            if (document is null || !IsSupportedRoslynLanguage(document.Project.Language))
            {
                continue;
            }

            if (!MatchesProjectId(document.Project, projectId) || !seen.Add(document.Id))
            {
                continue;
            }

            documents.Add(document);
        }

        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSupportedRoslynLanguage(project.Language) || !MatchesProjectId(project, projectId))
            {
                continue;
            }

            var generated = await project
                .GetSourceGeneratedDocumentsAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var document in generated)
            {
                if (!PositionPaths.Equal(document.FilePath, path) || !seen.Add(document.Id))
                {
                    continue;
                }

                documents.Add(document);
            }
        }

        if (documents.Count == 0)
        {
            return (null, new DocumentNotInWorkspaceError(
                $"No document '{path}' is part of the ready workspace.",
                "Feed filePath from project_diagnostics / symbol_find_references / symbol_goto_definition verbatim, or check workspace_list_projects for the right projectId."));
        }

        var hits = new List<PositionHit>();
        SymbolQueryError? firstError = null;
        foreach (var document in documents)
        {
            var (hit, error) = await ResolveInDocumentAsync(
                    document, line, column, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                firstError ??= error;
                continue;
            }

            hits.Add(hit!);
        }

        if (hits.Count == 0)
        {
            return (null, firstError ?? new NoSymbolAtPositionError(
                $"No symbol was found at position ({line},{column}) in '{path}'.",
                "Point at an identifier (a declaration or a reference) emitted by another tool."));
        }

        var distinct = hits
            .GroupBy(hit => hit.Symbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified))
            .Select(group => group.First())
            .ToList();
        if (distinct.Count > 1)
        {
            var candidates = string.Join(
                ", ",
                distinct.Select(hit =>
                    hit.Symbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified)));
            return (null, new SymbolAmbiguousError(
                $"The position resolves to different symbols across {distinct.Count} projects: {candidates}.",
                "Pass projectId from workspace_list_projects to symbol_resolve_at to pick a single project's view."));
        }

        var winner = distinct[0];
        var success = ToSuccess(winner.Project, winner.Symbol);
        return (new SymbolResolveAtSuccess(
            success.Handle,
            success.Summary,
            winner.Matched,
            winner.ResolvedFrom,
            winner.CandidateReason), null);
    }

    private static async Task<(PositionHit? Hit, SymbolQueryError? Error)> ResolveInDocumentAsync(
        Document document,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count || column < 1)
        {
            return (null, new InvalidPositionError(
                $"Position ({line},{column}) is outside the document (lines 1..{text.Lines.Count}, columns start at 1).",
                "Pass the 1-based line/column emitted by another tool for this document."));
        }

        var textLine = text.Lines[line - 1];
        var offset = textLine.Start + Math.Min(column - 1, textLine.Span.Length);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return (null, new CompilationUnavailableError(
                $"Document '{document.FilePath}' has no syntax tree.",
                "The document is not a compilable C#/VB document."));
        }

        var token = root.FindToken(offset, findInsideTrivia: false);
        if (!IsIdentifierLike(token))
        {
            var previous = token.GetPreviousToken();
            if (IsIdentifierLike(previous) && LeftTokenStillApplies(text, previous, offset, line))
            {
                token = previous;
            }
            else
            {
                return (null, new NoSymbolAtPositionError(
                    $"No identifier or type keyword sits at position ({line},{column}) in '{document.FilePath}'.",
                    "Point at an identifier (a declaration or a reference) emitted by another tool."));
            }
        }

        var node = token.Parent;
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null || node is null)
        {
            return (null, new CompilationUnavailableError(
                $"Document '{document.FilePath}' has no semantic model.",
                "The document is not a compilable C#/VB document."));
        }

        var symbol = DeclaredSymbolAt(model, node, cancellationToken);
        var resolvedFrom = SymbolResolveFrom.Reference;
        string? candidateReason = null;
        if (symbol is not null)
        {
            resolvedFrom = SymbolResolveFrom.Declaration;
        }
        else
        {
            var info = model.GetSymbolInfo(node, cancellationToken);
            symbol = info.Symbol;
            if (symbol is null)
            {
                if (info.CandidateSymbols.Length == 1)
                {
                    symbol = info.CandidateSymbols[0];
                    candidateReason = "OverloadResolutionFailed-SingleCandidate";
                }
                else if (info.CandidateSymbols.Length > 1)
                {
                    var candidates = string.Join(
                        ", ",
                        info.CandidateSymbols.Select(candidate =>
                            candidate.ToDisplayString(SymbolDisplayFormats.SignatureQualified)));
                    return (null, new SymbolAmbiguousError(
                        $"The identifier resolves to {info.CandidateSymbols.Length} candidates (overload resolution failed): {candidates}.",
                        "Fix the expression, or resolve one overload directly with symbol_resolve."));
                }
            }
        }

        if (symbol is null)
        {
            return (null, new NoSymbolAtPositionError(
                $"No symbol binds at position ({line},{column}) in '{document.FilePath}'.",
                "Point at an identifier (a declaration or a reference) emitted by another tool."));
        }

        symbol = NormalizePositionSymbol(symbol);
        if (!IsAddressable(symbol))
        {
            var containing = NearestAddressableAncestor(symbol);
            var display = symbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified);
            return (null, new SymbolNotAddressableError(
                $"Symbol '{display}' ({symbol.Kind}) has no stable SymbolHandle — only named types and their direct members are addressable." +
                (containing is null
                    ? string.Empty
                    : $" It is declared inside member '{containing.ToDisplayString(SymbolDisplayFormats.SignatureQualified)}'."),
                containing is null
                    ? "Resolve the enclosing named type or member with symbol_resolve (by name) to obtain a SymbolHandle."
                    : $"Call symbol_resolve for '{containing.ToDisplayString(SymbolDisplayFormats.SignatureQualified)}' to obtain a SymbolHandle for the containing member."));
        }

        var lineSpan = Location.Create(node.SyntaxTree, token.Span).GetLineSpan();
        var (matchLine, matchColumn, matchEndLine, matchEndColumn) = SourcePositions.FromLineSpan(lineSpan);
        var matched = new MatchedIdentifier(
            matchLine, matchColumn, matchEndLine, matchEndColumn, token.Text);
        return (new PositionHit(document.Project, symbol, matched, resolvedFrom, candidateReason), null);
    }

    private sealed record PositionHit(
        Project Project,
        ISymbol Symbol,
        MatchedIdentifier Matched,
        string ResolvedFrom,
        string? CandidateReason);

    private static bool MatchesProjectId(Project project, string? projectId) =>
        string.IsNullOrWhiteSpace(projectId) ||
        string.Equals(project.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Identifier tokens plus keywords that carry type meaning (var is already
    /// an identifier token; predefined types like int/string/Integer sit on
    /// keyword tokens under PredefinedTypeSyntax).
    /// </summary>
    private static bool IsIdentifierLike(SyntaxToken token) =>
        token.RawKind == (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken ||
        token.RawKind == (int)Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.IdentifierToken ||
        token.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.PredefinedTypeSyntax ||
        token.Parent is Microsoft.CodeAnalysis.VisualBasic.Syntax.PredefinedTypeSyntax;

    /// <summary>
    /// The left-adjacent token only counts when it still "owns" the position:
    /// its span covers the offset, or it ends on the requested line (a blank
    /// position hits the identifier immediately to its left).
    /// </summary>
    private static bool LeftTokenStillApplies(SourceText text, SyntaxToken previous, int offset, int line)
    {
        if (previous.Span.Contains(offset))
        {
            return true;
        }

        return text.Lines.GetLinePosition(previous.Span.End).Line == line - 1;
    }

    private static ISymbol? DeclaredSymbolAt(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        var declared = model.GetDeclaredSymbol(node, cancellationToken);
        if (declared is null &&
            node.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.NameEqualsSyntax
            {
                Parent: Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax usingDirective
            })
        {
            declared = model.GetDeclaredSymbol(usingDirective, cancellationToken);
        }

        return declared;
    }

    /// <summary>
    /// Canonical normalization for the position entry (ADR-0001 Amendment 7):
    /// using aliases map to their target; reduced extension calls map to the
    /// extension method on the type; constructed generic forms map to their
    /// OriginalDefinition so `List&lt;int&gt;` gives the `List&lt;T&gt;` handle.
    /// </summary>
    private static ISymbol NormalizePositionSymbol(ISymbol symbol)
    {
        if (symbol is IAliasSymbol alias)
        {
            symbol = alias.Target;
        }

        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
        {
            symbol = reduced;
        }

        if (!SymbolEqualityComparer.Default.Equals(symbol, symbol.OriginalDefinition))
        {
            symbol = symbol.OriginalDefinition;
        }

        return symbol;
    }

    /// <summary>
    /// The addressable set (ADR-0001 §1): named types and direct members of
    /// named types — the same symbols FindBySignature can re-resolve.
    /// </summary>
    private static bool IsAddressable(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => false,
        INamedTypeSymbol named => !named.IsAnonymousType,
        ITypeParameterSymbol or IParameterSymbol or ILocalSymbol or ILabelSymbol or
            IRangeVariableSymbol or IDiscardSymbol or IAliasSymbol => false,
        _ => symbol.ContainingSymbol is INamedTypeSymbol
    };

    private static ISymbol? NearestAddressableAncestor(ISymbol symbol)
    {
        var containing = symbol.ContainingSymbol;
        while (containing is not null && !IsAddressable(containing))
        {
            containing = containing.ContainingSymbol;
        }

        return containing;
    }
}
