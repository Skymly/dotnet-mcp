using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Core;

public sealed partial class RoslynLanguageAdapter
{
    /// <summary>
    /// XAML half of a rename preview (ADR-0005 Amendment 1, ADR-0006): every
    /// <see cref="XamlUsageConfidence.Exact"/> usage point whose
    /// <see cref="XamlUsagePoint.SymbolKey"/> matches the renamed symbol
    /// becomes a text edit inside its registered AdditionalDocument;
    /// <see cref="XamlUsageConfidence.Unverified"/> name matches are reported,
    /// never written. Rename is solution-wide, so usages are collected from
    /// every project in the session's solution — not the find_references
    /// dependency-closure scope. A session without a wired
    /// <see cref="IXamlUsageSource"/> collects nothing and the preview is
    /// byte-identical to before.
    /// </summary>
    private async Task<XamlRenameEdits> CollectXamlRenameAsync(
        IWorkspaceSession session,
        ISymbol symbol,
        string newName,
        IXamlUsageSource? xamlSource,
        CancellationToken cancellationToken)
    {
        if (xamlSource is null)
        {
            return XamlRenameEdits.Empty;
        }

        var normalized = NormalizePositionSymbol(symbol);
        var targetKey = SymbolKey(normalized);
        var oldName = normalized.Name;

        // Shared or linked XAML documents are indexed once per project that
        // sees them; collapse to one usage like CollectXamlReferenceHitsAsync.
        var seen = new HashSet<(string PathKey, int Start, int Length, XamlUsageKind Kind)>();
        var matches = new List<XamlUsagePoint>();
        foreach (var project in session.Solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var usages = await xamlSource
                .GetUsagesAsync(session, project.Id.Id.ToString("D"), cancellationToken)
                .ConfigureAwait(false);
            foreach (var usage in usages)
            {
                if (!string.Equals(usage.SymbolKey, targetKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (seen.Add((PositionPaths.Key(usage.FilePath), usage.Start, usage.Length, usage.Kind)))
                {
                    matches.Add(usage);
                }
            }
        }

        if (matches.Count == 0)
        {
            return XamlRenameEdits.Empty;
        }

        var texts = new Dictionary<string, (string Path, SourceText Text)>(StringComparer.Ordinal);
        var editsByKey = new Dictionary<string, List<(int Start, int Length)>>(StringComparer.Ordinal);
        var mentions = new List<UnverifiedXamlMention>();
        var mentionSeen = new HashSet<(string PathKey, int Start, int Length)>();
        foreach (var usage in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = PositionPaths.Key(usage.FilePath);
            if (!texts.TryGetValue(key, out var entry))
            {
                var document = FindAdditionalDocument(session, usage.ProjectId, usage.FilePath);
                if (document is null)
                {
                    // Stale index entry: the usage points at a document the
                    // session no longer carries. Never edit an unread file.
                    continue;
                }

                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                entry = (document.FilePath!, text);
                texts[key] = entry;
            }

            if (usage.Start < 0 || usage.Length < 0 || usage.Length > entry.Text.Length - usage.Start)
            {
                continue;
            }

            var spanText = entry.Text.ToString(new TextSpan(usage.Start, usage.Length));
            if (usage.Confidence == XamlUsageConfidence.Exact)
            {
                if (TryXamlEdit(usage, spanText, oldName, out var edit))
                {
                    if (!editsByKey.TryGetValue(key, out var list))
                    {
                        list = [];
                        editsByKey[key] = list;
                    }

                    list.Add(edit);
                }

                // An Exact usage whose span does not end with / equal the old
                // simple name is skipped rather than guessed at.
                continue;
            }

            // Unverified: name-match evidence only. The span must literally be
            // the old simple name, and the point is reported, never written.
            if (string.Equals(spanText, oldName, StringComparison.Ordinal) &&
                mentionSeen.Add((key, usage.Start, usage.Length)))
            {
                mentions.Add(new UnverifiedXamlMention(
                    entry.Path,
                    usage.Line,
                    usage.Column,
                    usage.EndLine,
                    usage.EndColumn,
                    spanText));
            }
        }

        var slices = new List<RenameDocumentSlice>();
        foreach (var (key, edits) in editsByKey)
        {
            var (path, text) = texts[key];
            var oldText = text.ToString();
            var newText = ApplySpanReplacements(oldText, edits, newName);
            if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                slices.Add(new RenameDocumentSlice(path, oldText, newText));
            }
        }

        slices.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        mentions.Sort(static (a, b) =>
        {
            var byPath = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });
        return new XamlRenameEdits(slices, mentions.Count == 0 ? null : mentions);
    }

    /// <summary>
    /// The x:Name escape from the SourceGenerator refusal: a generated field is
    /// renameable only when an <see cref="XamlUsageKind.XName"/> usage in its
    /// defining project points at it — the XAML value is the generator input.
    /// </summary>
    private static async Task<bool> HasMatchingXNameUsageAsync(
        IWorkspaceSession session,
        Project project,
        ISymbol field,
        IXamlUsageSource xamlSource,
        CancellationToken cancellationToken)
    {
        var targetKey = SymbolKey(NormalizePositionSymbol(field));
        var usages = await xamlSource
            .GetUsagesAsync(session, project.Id.Id.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        return usages.Any(usage =>
            usage.Kind == XamlUsageKind.XName &&
            usage.Confidence == XamlUsageConfidence.Exact &&
            string.Equals(usage.SymbolKey, targetKey, StringComparison.Ordinal));
    }

    /// <summary>
    /// Preview for a generated x:Name field: rewrite the x:Name value (the
    /// generator re-emits the field on its next run) plus every handwritten
    /// source reference to the field. The generated declaration and generated
    /// references are not edited — they regenerate. Handwritten references are
    /// collected with Roslyn FindReferences over the whole solution, matching
    /// the solution-wide scope of a source rename.
    /// </summary>
    private async Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildXNameFieldRenameDraftAsync(
        IWorkspaceSession session,
        string handle,
        ISymbol field,
        string newName,
        IXamlUsageSource xamlSource,
        CancellationToken cancellationToken)
    {
        if (string.Equals(field.Name, newName, StringComparison.Ordinal))
        {
            return (null, new InvalidRenameNameError(
                $"New name '{newName}' is identical to the current symbol name.",
                "Pass a different identifier as newName."));
        }

        var xaml = await CollectXamlRenameAsync(session, field, newName, xamlSource, cancellationToken)
            .ConfigureAwait(false);
        var sourceSlices = await CollectHandwrittenReferenceSlicesAsync(
                session, field, newName, cancellationToken)
            .ConfigureAwait(false);

        var documents = sourceSlices.Concat(xaml.Slices).ToArray();
        if (documents.Length == 0)
        {
            return (null, new GeneratedSymbolRenameRefusedError(
                "This rename would only change generated documents.",
                "Change the generator input (handwritten partial / attribute) and call symbol_preview_rename on that symbol."));
        }

        return (new RenamePreviewDraft(handle, newName, documents, [handle], xaml.Mentions), null);
    }

    /// <summary>
    /// Reference spans of one symbol across the solution's handwritten
    /// documents, turned into rename slices. Generated documents (both real
    /// <see cref="SourceGeneratedDocument"/> instances and the project's
    /// generated ids) and candidate locations are excluded; a reference whose
    /// span is not literally the old simple name is skipped rather than
    /// guessed at.
    /// </summary>
    private static async Task<IReadOnlyList<RenameDocumentSlice>> CollectHandwrittenReferenceSlicesAsync(
        IWorkspaceSession session,
        ISymbol symbol,
        string newName,
        CancellationToken cancellationToken)
    {
        var oldName = symbol.Name;
        var generatedIds = await GeneratedDocumentIdsAsync(session.Solution, cancellationToken)
            .ConfigureAwait(false);
        var referenced = await SymbolFinder
            .FindReferencesAsync(symbol, session.Solution, cancellationToken)
            .ConfigureAwait(false);

        var edits = new Dictionary<Document, List<(int Start, int Length)>>();
        foreach (var referencedSymbol in referenced)
        {
            if (!SymbolEqualityComparer.Default.Equals(referencedSymbol.Definition, symbol))
            {
                continue;
            }

            foreach (var location in referencedSymbol.Locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (location.IsCandidateLocation)
                {
                    continue;
                }

                var document = location.Document;
                if (document is null ||
                    document is SourceGeneratedDocument ||
                    generatedIds.Contains(document.Id) ||
                    string.IsNullOrWhiteSpace(document.FilePath))
                {
                    continue;
                }

                var span = location.Location.SourceSpan;
                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (span.Start < 0 || span.End > text.Length ||
                    !string.Equals(text.ToString(span), oldName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!edits.TryGetValue(document, out var list))
                {
                    list = [];
                    edits[document] = list;
                }

                list.Add((span.Start, span.Length));
            }
        }

        var slices = new List<RenameDocumentSlice>();
        foreach (var (document, spans) in edits)
        {
            var oldText = (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            var newText = ApplySpanReplacements(oldText, spans, newName);
            if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                slices.Add(new RenameDocumentSlice(document.FilePath!, oldText, newText));
            }
        }

        slices.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return slices;
    }

    private static async Task<HashSet<DocumentId>> GeneratedDocumentIdsAsync(
        Solution solution,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<DocumentId>();
        foreach (var project in solution.Projects)
        {
            foreach (var generated in await project
                         .GetSourceGeneratedDocumentsAsync(cancellationToken)
                         .ConfigureAwait(false))
            {
                ids.Add(generated.Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Locate the registered AdditionalDocument a usage point was read from:
    /// the usage's owning project first, then any project carrying the same
    /// path (linked documents surface under more than one project).
    /// </summary>
    private static TextDocument? FindAdditionalDocument(
        IWorkspaceSession session,
        string projectId,
        string path)
    {
        var projects = session.Solution.Projects;
        var owner = projects.FirstOrDefault(p =>
            string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase));
        var found = FindInProject(owner);
        if (found is not null)
        {
            return found;
        }

        foreach (var project in projects)
        {
            if (ReferenceEquals(project, owner))
            {
                continue;
            }

            found = FindInProject(project);
            if (found is not null)
            {
                return found;
            }
        }

        return null;

        TextDocument? FindInProject(Project? project)
        {
            if (project is null)
            {
                return null;
            }

            foreach (var document in project.AdditionalDocuments)
            {
                if (PositionPaths.Equal(document.FilePath, path))
                {
                    return document;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Per-kind edit rule (ADR-0006): the replacement covers only the symbol's
    /// simple-name occurrence inside the usage span.
    /// </summary>
    private static bool TryXamlEdit(
        XamlUsagePoint usage,
        string spanText,
        string oldName,
        out (int Start, int Length) edit)
    {
        edit = default;
        switch (usage.Kind)
        {
            // The span IS the identifier/value text — replace it wholesale.
            // A padded attribute value keeps its padding: replace the name
            // occurrence inside the span instead.
            case XamlUsageKind.BindingSegment:
            case XamlUsageKind.PropertyAttribute:
            case XamlUsageKind.EventHandler:
            case XamlUsageKind.XName:
                var at = IdentifierOccurrence(spanText, oldName);
                if (at < 0)
                {
                    return false;
                }

                edit = (usage.Start + at, oldName.Length);
                return true;

            // The span may carry an xmlns prefix or CLR qualification
            // (`vm:Foo`, `ns.Sub.Foo`, `SampleApp.MainWindow`): only a tail
            // occurrence of the old simple name is safe to replace. A span
            // that does not end with the name is skipped, not guessed at.
            case XamlUsageKind.ElementType:
            case XamlUsageKind.XClass:
            case XamlUsageKind.XDataType:
            case XamlUsageKind.MarkupTypeRef:
                if (!spanText.EndsWith(oldName, StringComparison.Ordinal))
                {
                    return false;
                }

                edit = (usage.Start + usage.Length - oldName.Length, oldName.Length);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Offset of the old name inside a whole-identifier span: 0 when the span
    /// is exactly the name, the occurrence offset when only surrounding
    /// whitespace pads it, else -1.
    /// </summary>
    private static int IdentifierOccurrence(string spanText, string oldName)
    {
        if (string.Equals(spanText, oldName, StringComparison.Ordinal))
        {
            return 0;
        }

        return string.Equals(spanText.Trim(), oldName, StringComparison.Ordinal)
            ? spanText.IndexOf(oldName, StringComparison.Ordinal)
            : -1;
    }

    /// <summary>
    /// Apply (start, length) → newName replacements to <paramref name="text"/>.
    /// Edits are deduplicated, applied right-to-left, and any edit overlapping
    /// an already-applied one is skipped rather than corrupting the document.
    /// </summary>
    private static string ApplySpanReplacements(
        string text,
        IReadOnlyList<(int Start, int Length)> edits,
        string newName)
    {
        var builder = new StringBuilder(text);
        var nextStart = text.Length;
        foreach (var (start, length) in edits.Distinct().OrderByDescending(static e => e.Start))
        {
            if (start < 0 || length < 0 || start + length > nextStart)
            {
                continue;
            }

            builder.Remove(start, length);
            builder.Insert(start, newName);
            nextStart = start;
        }

        return builder.ToString();
    }

    private sealed record XamlRenameEdits(
        IReadOnlyList<RenameDocumentSlice> Slices,
        IReadOnlyList<UnverifiedXamlMention>? Mentions)
    {
        public static readonly XamlRenameEdits Empty = new([], null);
    }
}
