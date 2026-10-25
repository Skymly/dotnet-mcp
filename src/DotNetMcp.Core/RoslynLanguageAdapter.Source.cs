using Microsoft.CodeAnalysis;
using CSharp = Microsoft.CodeAnalysis.CSharp.Syntax;
using Vb = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace DotNetMcp.Core;

/// <summary>
/// symbol_source for C#/VB: verbatim declaration text via DeclaringSyntaxReferences,
/// expanded to the XML doc comment (leading structured trivia) or the first
/// attribute list, through the end of the declaration node. Field and
/// event-field symbols take the whole declaration statement so multi-declarator
/// fields stay in one slice; properties keep their accessors; type symbols take
/// the whole type declaration. Generated trees reuse the GetDefinition origin
/// path (GeneratorQueryService.MatchSyntaxTreeAsync), never FilePath heuristics.
/// </summary>
public sealed partial class RoslynLanguageAdapter
{
    public async Task<(SymbolSourceSuccess? Success, SymbolQueryError? Error)> GetSourceAsync(
        IWorkspaceSession session,
        string handle,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        var (project, symbol, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (symbol!.DeclaringSyntaxReferences.Length == 0)
        {
            return (null, SourceUnavailable(symbol));
        }

        var handwritten = new List<SymbolSourceSlice>();
        var generated = new List<SymbolSourceSlice>();
        var seen = new HashSet<(SyntaxTree Tree, int Start)>();
        var texts = new Dictionary<SyntaxTree, string>();
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            var node = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            var declaration = DeclarationNodeFor(node);
            var tree = declaration.SyntaxTree ?? reference.SyntaxTree;
            if (tree is null)
            {
                continue;
            }

            var start = DocCommentStart(declaration);
            if (!seen.Add((tree, start)))
            {
                continue;
            }

            var (originLabel, originError) = await ResolveOriginAsync(session, project!, tree, cancellationToken)
                .ConfigureAwait(false);
            if (originError is not null)
            {
                return (null, originError);
            }

            if (!texts.TryGetValue(tree, out var text))
            {
                text = (await tree.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                texts[tree] = text;
            }

            // Same parse as ToAttribution: Origin carries the full
            // SourceGenerator(Assembly::Type@Version) label, Generator the identity.
            GeneratorIdentity? identity = null;
            if (originLabel is not null &&
                TryParseSourceGeneratorOrigin(originLabel, out var parsed))
            {
                identity = parsed;
            }

            var isGenerated = identity is not null;
            var slice = new SymbolSourceSlice(
                originLabel ?? SymbolOrigin.Handwritten,
                identity,
                tree.FilePath,
                start,
                declaration.Span.End,
                text!);
            (isGenerated ? generated : handwritten).Add(slice);
        }

        var slices = handwritten
            .OrderBy(s => s.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(s => s.Start)
            .Concat(generated
                .OrderBy(s => s.FilePath ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(s => s.Start))
            .ToArray();

        if (slices.Length == 0)
        {
            return (null, SourceUnavailable(symbol));
        }

        return (SymbolSourceText.Slice(slices, maxChars, isTypeSymbol: symbol is INamedTypeSymbol), null);
    }

    private static SourceUnavailableError SourceUnavailable(ISymbol symbol) =>
        new(
            $"Symbol '{symbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified)}' has no source declarations.",
            "symbol_source only returns text for source-defined symbols; metadata-only symbols have no declaration text.");

    /// <summary>
    /// Normalize a declaring-syntax node to the full declaration node: field /
    /// event-field declarators ascend to the whole declaration statement, C#
    /// accessors to their property/indexer/event, positional-record parameters
    /// to the record, and VB header statements to their member/type block.
    /// </summary>
    private static SyntaxNode DeclarationNodeFor(SyntaxNode node)
    {
        switch (node)
        {
            case CSharp.VariableDeclaratorSyntax:
                {
                    var field = node.Ancestors()
                        .OfType<CSharp.BaseFieldDeclarationSyntax>()
                        .FirstOrDefault();
                    return field ?? node;
                }
            case CSharp.AccessorDeclarationSyntax:
                {
                    var property = node.Ancestors()
                        .OfType<CSharp.BasePropertyDeclarationSyntax>()
                        .FirstOrDefault();
                    return property ?? node;
                }
            case CSharp.ParameterSyntax parameter
                when parameter.Parent is CSharp.ParameterListSyntax
                    && parameter.Parent.Parent is CSharp.RecordDeclarationSyntax:
                return parameter.Parent.Parent;
        }

        // VB field declarators keep the whole field statement (a, b As Integer).
        if (node is Vb.VariableDeclaratorSyntax or Vb.ModifiedIdentifierSyntax)
        {
            var field = node.Ancestors()
                .OfType<Vb.FieldDeclarationSyntax>()
                .FirstOrDefault();
            if (field is not null)
            {
                return field;
            }
        }

        // VB type-header statements (Class/Interface/Structure/Module/Enum) take the whole type block.
        if ((node is Vb.TypeStatementSyntax || node is Vb.EnumStatementSyntax) &&
            (node.Parent is Vb.TypeBlockSyntax || node.Parent is Vb.EnumBlockSyntax))
        {
            return node.Parent;
        }

        // VB member-header statements (Sub/Function/New/Property/Operator/accessor/custom event)
        // ascend to their member block so the body and End line come along.
        // Statements sitting directly in a type block (interface members,
        // MustOverride headers) have no member block and stay as-is.
        while (node.Parent is Vb.MethodBlockSyntax or Vb.ConstructorBlockSyntax or
               Vb.OperatorBlockSyntax or Vb.PropertyBlockSyntax or
               Vb.EventBlockSyntax or Vb.AccessorBlockSyntax)
        {
            node = node.Parent;
        }

        return node;
    }

    /// <summary>
    /// The declaration start: the earliest structured documentation-comment
    /// trivia in the node's leading trivia (still attached — only whitespace
    /// may separate it from the node), else the node start which already covers
    /// the first attribute list.
    /// </summary>
    private static int DocCommentStart(SyntaxNode node)
    {
        var start = node.SpanStart;
        var trivia = node.GetLeadingTrivia();
        for (var i = trivia.Count - 1; i >= 0; i--)
        {
            var current = trivia[i];
            if (string.IsNullOrWhiteSpace(current.ToString()))
            {
                continue;
            }

            // '''/''' and /** */ doc comments are structured trivia; FullSpan
            // covers the ''' or /// markers as well as the XML body. Both
            // languages carry their own DocumentationCommentTriviaSyntax node.
            if (current.HasStructure &&
                current.GetStructure() is CSharp.DocumentationCommentTriviaSyntax
                    or Vb.DocumentationCommentTriviaSyntax)
            {
                start = current.FullSpan.Start;
                continue;
            }

            break;
        }

        return start;
    }
}
