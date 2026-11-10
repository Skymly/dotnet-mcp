using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Core;

public sealed partial class RoslynLanguageAdapter
{
    /// <summary>
    /// Source-side unverified mentions for a rename preview (issue #562), the
    /// same report-only contract as <see cref="UnverifiedXamlMention"/>:
    /// name-matched hits that plausibly reference the renamed symbol but are
    /// never written. Three classes are collected:
    /// <list type="bullet">
    /// <item><see cref="UnverifiedMentionKind.MetadataName"/> — a string literal
    /// holding the symbol's qualified name (a <c>GetTypeByMetadataName</c> /
    /// <c>ForAttributeWithMetadataName</c> argument, or an assembly-qualified
    /// name), reported for every preview.</item>
    /// <item><see cref="UnverifiedMentionKind.UnboundAttribute"/> — an attribute
    /// usage whose short or full name matches the renamed type's old or new
    /// spelling yet does not bind to it (unbound, ambiguous, or bound to another
    /// type). Bound usages are already in the write set.</item>
    /// <item><see cref="UnverifiedMentionKind.Documentation"/> — a bounded
    /// qualified-name hit in comments, doc comments, disabled text, additional
    /// documents, or a non-exact string; only collected when
    /// <paramref name="includeDocMentions"/> is set.</item>
    /// </list>
    /// The scan covers the whole solution — the same scope as the rename —
    /// over handwritten C# / VB documents only; generated documents are
    /// skipped and registered XAML documents keep their own channel.
    /// </summary>
    private async Task<IReadOnlyList<UnverifiedMention>?> CollectUnverifiedMentionsAsync(
        IWorkspaceSession session,
        ISymbol symbol,
        string newName,
        bool includeDocMentions,
        IXamlUsageSource? xamlSource,
        CancellationToken cancellationToken)
    {
        var qualified = QualifiedNameCandidates(symbol);
        var spellings = AttributeSpellings(symbol, newName);
        if (qualified.Count == 0 && spellings.Count == 0)
        {
            return null;
        }

        var generatedIds = await GeneratedDocumentIdsAsync(session.Solution, cancellationToken)
            .ConfigureAwait(false);
        // The display key is compilation-independent: a symbol resolved in the
        // defining project still matches its retargeted twin in referencing
        // projects' SemanticModels, and OriginalDefinition collapses
        // constructed generics.
        var symbolKey = SymbolKey(symbol.OriginalDefinition);
        var mentions = new List<UnverifiedMention>();
        foreach (var project in session.Solution.Projects)
        {
            if (!OwnsProject(project))
            {
                continue;
            }

            foreach (var document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (document is SourceGeneratedDocument ||
                    generatedIds.Contains(document.Id) ||
                    string.IsNullOrWhiteSpace(document.FilePath))
                {
                    continue;
                }

                await ScanSourceDocumentAsync(
                        document, symbol, symbolKey, qualified, spellings, includeDocMentions, mentions, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!includeDocMentions)
            {
                continue;
            }

            foreach (var document in project.AdditionalDocuments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Unwired sessions still skip XAML-shaped paths: the XAML channel
                // owns them when a source is registered, and a bare .axaml/.xaml
                // additional document is not a documentation mention.
                if (string.IsNullOrWhiteSpace(document.FilePath) ||
                    (xamlSource?.IsXamlDocumentPath(document.FilePath) ?? IsXamlPathExtension(document.FilePath)))
                {
                    continue;
                }

                await ScanAdditionalDocumentAsync(document, qualified, mentions, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        mentions.Sort(static (a, b) =>
        {
            var byPath = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return mentions.Count == 0 ? null : mentions;
    }

    /// <summary>
    /// The qualified-name strings a source-side hit must match: the dotted
    /// containing path (<c>Ns.Outer.Inner</c> for types, <c>Ns.Type.Member</c>
    /// for members) plus, for named types, the reflection metadata form
    /// (<c>Ns.Outer+Inner`1</c>).
    /// </summary>
    private static IReadOnlyList<string> QualifiedNameCandidates(ISymbol symbol)
    {
        var candidates = new List<string>(2);
        var dotted = DottedName(symbol);
        if (dotted.Length > 0)
        {
            candidates.Add(dotted);
        }

        if (symbol is INamedTypeSymbol named)
        {
            var metadata = MetadataName(named);
            if (!string.Equals(metadata, dotted, StringComparison.Ordinal))
            {
                candidates.Add(metadata);
            }
        }

        return candidates;
    }

    private static string DottedName(ISymbol symbol)
    {
        var builder = new StringBuilder(symbol.Name);
        for (var container = symbol.ContainingSymbol; container is not null; container = container.ContainingSymbol)
        {
            if (container is INamespaceSymbol { IsGlobalNamespace: true } || string.IsNullOrEmpty(container.Name))
            {
                break;
            }

            builder.Insert(0, '.').Insert(0, container.Name);
        }

        return builder.ToString();
    }

    private static string MetadataName(INamedTypeSymbol type)
    {
        var segments = new List<string>();
        for (var current = type; current is not null; current = current.ContainingType)
        {
            segments.Insert(0, current.MetadataName);
        }

        var prefix = type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "."
            : string.Empty;
        return prefix + string.Join("+", segments);
    }

    /// <summary>
    /// The attribute-name spellings worth matching, old spellings first: the
    /// type's full name, its short form when it ends with <c>Attribute</c>,
    /// then the same pair for <paramref name="newName"/>. Empty unless the
    /// symbol is a named type.
    /// </summary>
    private static IReadOnlyList<AttributeSpelling> AttributeSpellings(ISymbol symbol, string newName)
    {
        if (symbol is not INamedTypeSymbol)
        {
            return [];
        }

        var spellings = new List<AttributeSpelling>(4) { new(symbol.Name, NewSpelling: false) };
        if (ShortAttributeName(symbol.Name) is { } oldShort &&
            !string.Equals(oldShort, newName, StringComparison.Ordinal))
        {
            spellings.Add(new AttributeSpelling(oldShort, NewSpelling: false));
        }

        if (!string.Equals(newName, symbol.Name, StringComparison.Ordinal))
        {
            spellings.Add(new AttributeSpelling(newName, NewSpelling: true));
        }

        if (ShortAttributeName(newName) is { } newShort &&
            !string.Equals(newShort, symbol.Name, StringComparison.Ordinal))
        {
            spellings.Add(new AttributeSpelling(newShort, NewSpelling: true));
        }

        return spellings.Distinct().ToArray();
    }

    private static string? ShortAttributeName(string name) =>
        name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length
            ? name[..^"Attribute".Length]
            : null;

    private static async Task ScanSourceDocumentAsync(
        Document document,
        ISymbol symbol,
        string symbolKey,
        IReadOnlyList<string> qualified,
        IReadOnlyList<AttributeSpelling> spellings,
        bool includeDocMentions,
        List<UnverifiedMention> mentions,
        CancellationToken cancellationToken)
    {
        var vb = document.Project.Language == LanguageNames.VisualBasic;
        var spellingComparison = vb ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var source = text.ToString();
        var hits = QualifiedNameHits(source, qualified);
        // Bounded spelling hits only: a bare IndexOf makes every preview pay for
        // a syntax root + walk on documents that merely contain the text inside
        // a longer word. VB identifiers are case-insensitive.
        var attributeHit = spellings.Count > 0 &&
            spellings.Any(s => ContainsBounded(source, s.Text, spellingComparison));
        if (hits.Count == 0 && !attributeHit)
        {
            return;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        SemanticModel? model = null;

        foreach (var (start, length) in hits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var needle = source.Substring(start, length);
            if (root.FindTrivia(start).RawKind != 0)
            {
                if (!includeDocMentions)
                {
                    continue;
                }

                // A cref already bound to the renamed symbol is a real reference:
                // it lands in the write set, not in the mention report.
                model ??= await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (model is not null && InsideBoundCref(root, start, symbolKey, model, vb))
                {
                    continue;
                }

                AddMention(
                    mentions,
                    document.FilePath!,
                    text,
                    start,
                    length,
                    needle,
                    UnverifiedMentionKind.Documentation,
                    "Comment, doc-comment, or disabled text contains the qualified name; it is not rewritten.");
                continue;
            }

            var token = root.FindToken(start);
            if (!IsStringTextToken(token, vb))
            {
                continue;
            }

            var literal = token.ValueText ?? string.Empty;
            if (string.Equals(literal, needle, StringComparison.Ordinal) ||
                literal.StartsWith(needle + ",", StringComparison.Ordinal))
            {
                AddMention(
                    mentions,
                    document.FilePath!,
                    text,
                    start,
                    length,
                    needle,
                    UnverifiedMentionKind.MetadataName,
                    "String literal holds the symbol's qualified name; a metadata-name lookup (GetTypeByMetadataName / ForAttributeWithMetadataName) or assembly-qualified name is not rewritten.");
            }
            else if (includeDocMentions && ContainsBounded(literal, needle))
            {
                AddMention(
                    mentions,
                    document.FilePath!,
                    text,
                    start,
                    length,
                    needle,
                    UnverifiedMentionKind.Documentation,
                    "String literal text contains the qualified name without being a metadata-name match; it is not rewritten.");
            }
        }

        if (attributeHit)
        {
            // Syntax first: only pay for a semantic model when an attribute's
            // rightmost identifier actually spells an old or new name.
            var candidates = CollectAttributeCandidates(root, spellings, vb, spellingComparison);
            if (candidates.Count > 0)
            {
                model ??= await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (model is not null)
                {
                    ScanAttributeUsages(candidates, model, symbol, symbolKey, document.FilePath!, text, mentions);
                }
            }
        }
    }

    private static async Task ScanAdditionalDocumentAsync(
        TextDocument document,
        IReadOnlyList<string> qualified,
        List<UnverifiedMention> mentions,
        CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return;
        }

        var source = text.ToString();
        foreach (var (start, length) in QualifiedNameHits(source, qualified))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddMention(
                mentions,
                document.FilePath!,
                text,
                start,
                length,
                source.Substring(start, length),
                UnverifiedMentionKind.Documentation,
                $"Additional document '{System.IO.Path.GetFileName(document.FilePath)}' contains the qualified name; it is not rewritten.");
        }
    }

    /// <summary>
    /// Bounded occurrences of the qualified-name candidates in
    /// <paramref name="source"/>, keyed start → length. A hit must not be
    /// preceded by a name character or <c>.</c> (a longer qualification) and
    /// must not be followed by a name character; a trailing <c>.</c> is allowed
    /// so a deeper qualified name still reports its matching prefix. Overlaps
    /// at one start keep the longest candidate.
    /// </summary>
    private static SortedDictionary<int, int> QualifiedNameHits(string source, IReadOnlyList<string> candidates)
    {
        var hits = new SortedDictionary<int, int>();
        foreach (var candidate in candidates)
        {
            for (var at = source.IndexOf(candidate, StringComparison.Ordinal);
                 at >= 0;
                 at = source.IndexOf(candidate, at + 1, StringComparison.Ordinal))
            {
                if (!HasNameBoundaries(source, at, candidate.Length))
                {
                    continue;
                }

                if (hits.TryGetValue(at, out var length) && length >= candidate.Length)
                {
                    continue;
                }

                hits[at] = candidate.Length;
            }
        }

        return hits;
    }

    private static bool HasNameBoundaries(string text, int start, int length)
    {
        if (start > 0 && (IsNameChar(text[start - 1]) || text[start - 1] == '.'))
        {
            return false;
        }

        var end = start + length;
        return end == text.Length || !IsNameChar(text[end]);
    }

    private static bool ContainsBounded(string text, string needle) =>
        ContainsBounded(text, needle, StringComparison.Ordinal);

    private static bool ContainsBounded(string text, string needle, StringComparison comparison)
    {
        for (var at = text.IndexOf(needle, comparison);
             at >= 0;
             at = text.IndexOf(needle, at + 1, comparison))
        {
            if (HasNameBoundaries(text, at, needle.Length))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNameChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '`';

    /// <summary>
    /// True when the trivia hit at <paramref name="start"/> sits inside a
    /// doc-comment cref that binds to the renamed symbol — a compiler-visible
    /// reference already in the write set.
    /// </summary>
    private static bool InsideBoundCref(
        SyntaxNode root,
        int start,
        string symbolKey,
        SemanticModel model,
        bool vb)
    {
        var token = root.FindToken(start, findInsideTrivia: true);
        SyntaxNode? cref = null;
        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            var isCref = vb
                ? node is Microsoft.CodeAnalysis.VisualBasic.Syntax.CrefReferenceSyntax
                : node is Microsoft.CodeAnalysis.CSharp.Syntax.CrefSyntax;
            if (isCref)
            {
                cref = node;
            }
        }

        if (cref is null)
        {
            return false;
        }

        var info = model.GetSymbolInfo(cref);
        return IsRenamedSymbol(info.Symbol, symbolKey);
    }

    /// <summary>
    /// Display-key equality against the renamed symbol's original definition:
    /// retargeted symbols in referencing projects' compilations and
    /// constructed generics still match, so bound references are not
    /// double-reported as mentions.
    /// </summary>
    private static bool IsRenamedSymbol(ISymbol? candidate, string symbolKey) =>
        candidate is not null &&
        string.Equals(SymbolKey(candidate.OriginalDefinition), symbolKey, StringComparison.Ordinal);

    private static bool IsXamlPathExtension(string path) =>
        path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Syntax-only pass: attribute nodes whose rightmost name identifier
    /// matches an old or new spelling. A semantic model is only worth its
    /// cost when this list is non-empty.
    /// </summary>
    private static List<AttributeCandidate> CollectAttributeCandidates(
        SyntaxNode root,
        IReadOnlyList<AttributeSpelling> spellings,
        bool vb,
        StringComparison comparison)
    {
        var candidates = new List<AttributeCandidate>();
        foreach (var node in root.DescendantNodes())
        {
            var isAttribute = vb
                ? node.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.Attribute)
                : node.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.Attribute);
            if (!isAttribute)
            {
                continue;
            }

            var identifier = vb
                ? RightmostNameIdentifier((Microsoft.CodeAnalysis.VisualBasic.Syntax.AttributeSyntax)node)
                : RightmostNameIdentifier((Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax)node);
            if (identifier.RawKind == 0)
            {
                continue;
            }

            var spelling = spellings.FirstOrDefault(s =>
                string.Equals(identifier.Text, s.Text, comparison));
            if (spelling.Text is null)
            {
                continue;
            }

            candidates.Add(new AttributeCandidate(node, identifier, spelling));
        }

        return candidates;
    }

    private static void ScanAttributeUsages(
        List<AttributeCandidate> candidates,
        SemanticModel model,
        ISymbol symbol,
        string symbolKey,
        string path,
        SourceText text,
        List<UnverifiedMention> mentions)
    {
        foreach (var (node, identifier, spelling) in candidates)
        {
            var info = model.GetSymbolInfo(node);
            var bound = BoundType(info.Symbol);
            if (IsRenamedSymbol(bound, symbolKey))
            {
                // Bound to the renamed type: a compiler-visible reference that
                // is already rewritten by the rename.
                continue;
            }

            var binding = info.CandidateSymbols.Any(candidate =>
                    IsRenamedSymbol(BoundType(candidate), symbolKey))
                ? "resolves ambiguously, the renamed type among the candidates"
                : bound is not null
                    ? $"binds to '{bound.ToDisplayString()}'"
                    : "is unbound";
            var reason = spelling.NewSpelling
                ? $"Attribute name '{identifier.Text}' spells the renamed type's new name and {binding}; it may silently bind after apply and is not rewritten."
                : $"Attribute name '{identifier.Text}' {SpellingKind(identifier.Text, symbol.Name)}but {binding}; it is not rewritten.";
            AddMention(
                mentions,
                path,
                text,
                identifier.Span.Start,
                identifier.Span.Length,
                identifier.Text,
                UnverifiedMentionKind.UnboundAttribute,
                reason);
        }
    }

    private static string SpellingKind(string spelling, string oldName) =>
        string.Equals(spelling, oldName, StringComparison.OrdinalIgnoreCase)
            ? "matches the renamed type's name "
            : $"spells the renamed type as a short name '{spelling}' ";

    private static INamedTypeSymbol? BoundType(ISymbol? symbol) =>
        symbol switch
        {
            INamedTypeSymbol type => type,
            IMethodSymbol method => method.ContainingType,
            _ => null
        };

    private static SyntaxToken RightmostNameIdentifier(
        Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax attribute) =>
        RightmostNameIdentifier(attribute.Name);

    private static SyntaxToken RightmostNameIdentifier(
        Microsoft.CodeAnalysis.CSharp.Syntax.NameSyntax? name) => name switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.SimpleNameSyntax simple => simple.Identifier,
            Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qualified => RightmostNameIdentifier(qualified.Right),
            Microsoft.CodeAnalysis.CSharp.Syntax.AliasQualifiedNameSyntax alias => RightmostNameIdentifier(alias.Name),
            _ => default
        };

    private static SyntaxToken RightmostNameIdentifier(
        Microsoft.CodeAnalysis.VisualBasic.Syntax.AttributeSyntax attribute) =>
        RightmostNameIdentifier(attribute.Name);

    private static SyntaxToken RightmostNameIdentifier(
        Microsoft.CodeAnalysis.VisualBasic.Syntax.TypeSyntax? name) => name switch
        {
            Microsoft.CodeAnalysis.VisualBasic.Syntax.SimpleNameSyntax simple => simple.Identifier,
            Microsoft.CodeAnalysis.VisualBasic.Syntax.QualifiedNameSyntax qualified => RightmostNameIdentifier(qualified.Right),
            _ => default
        };

    private static bool IsStringTextToken(SyntaxToken token, bool vb) => vb
        ? token.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.StringLiteralToken) ||
          token.IsKind(Microsoft.CodeAnalysis.VisualBasic.SyntaxKind.InterpolatedStringTextToken)
        : token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralToken) ||
          token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.Utf8StringLiteralToken) ||
          token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.InterpolatedStringTextToken) ||
          token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SingleLineRawStringLiteralToken) ||
          token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.MultiLineRawStringLiteralToken) ||
          token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.Utf8SingleLineRawStringLiteralToken) ||
          token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.Utf8MultiLineRawStringLiteralToken);

    private static void AddMention(
        List<UnverifiedMention> mentions,
        string path,
        SourceText text,
        int start,
        int length,
        string matched,
        string kind,
        string reason)
    {
        var span = text.Lines.GetLinePositionSpan(new TextSpan(start, length));
        mentions.Add(new UnverifiedMention(
            path,
            span.Start.Line + 1,
            span.Start.Character + 1,
            span.End.Line + 1,
            span.End.Character + 1,
            matched,
            kind,
            reason));
    }

    private readonly record struct AttributeSpelling(string Text, bool NewSpelling);

    private readonly record struct AttributeCandidate(
        SyntaxNode Node,
        SyntaxToken Identifier,
        AttributeSpelling Spelling);
}
