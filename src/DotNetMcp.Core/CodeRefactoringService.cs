using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Core;

/// <summary>
/// First-party / parameterless CodeRefactoring host (Spike S8). No Visual Studio MEF catalog.
/// </summary>
public sealed class CodeRefactoringService
{
    private readonly SymbolQueryService _symbols;
    private readonly LanguageAdapters _languages;

    public CodeRefactoringService(SymbolQueryService symbols, LanguageAdapters? languages = null)
    {
        _symbols = symbols;
        _languages = languages ?? symbols.Languages;
    }

    public async Task<(CodeRefactoringListSuccess? Success, SymbolQueryError? Error)> ListAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default)
    {
        var (document, span, error) = await ResolveAnchorAsync(session, handle, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var actions = await CollectActionsAsync(document!, span, cancellationToken).ConfigureAwait(false);
        var items = actions
            .Select((action, index) => new CodeRefactoringItem(index, action.Title, action.EquivalenceKey))
            .ToArray();
        return (new CodeRefactoringListSuccess(items), null);
    }

    public async Task<(CodeRefactoringPreviewDraft? Draft, SymbolQueryError? Error)> BuildPreviewAsync(
        IWorkspaceSession session,
        string handle,
        int refactoringIndex,
        CancellationToken cancellationToken = default)
    {
        var (document, span, error) = await ResolveAnchorAsync(session, handle, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var actions = await CollectActionsAsync(document!, span, cancellationToken).ConfigureAwait(false);
        if (refactoringIndex < 0 || refactoringIndex >= actions.Count)
        {
            return (null, new RefactoringIndexOutOfRangeError(
                $"refactoringIndex {refactoringIndex} is out of range for {actions.Count} available refactoring(s).",
                "Call symbol_list_refactorings and pass a refactoringIndex from that list."));
        }

        var chosen = actions[refactoringIndex];
        var changed = await CodeActionDocuments.ApplyActionAsync(chosen, cancellationToken).ConfigureAwait(false);
        if (changed is null)
        {
            return (null, new RefactoringApplyFailedError(
                $"Code Refactoring '{chosen.Title}' did not produce a solution change.",
                "Pick another refactoringIndex from symbol_list_refactorings, or change the code without this tool."));
        }

        var (slices, generated) = await HandwrittenDocumentDiff.FromSolutionsAsync(session.Solution, changed, cancellationToken).ConfigureAwait(false);
        if (generated)
        {
            return (null, new GeneratedDocumentRefactoringRefusedError(
                "This Code Refactoring would change a generated document.",
                "Change the generator input (handwritten source / attribute) instead of applying a refactoring to generated output."));
        }

        if (slices.Count == 0)
        {
            return (null, new RefactoringApplyFailedError(
                $"Code Refactoring '{chosen.Title}' produced no handwritten document changes.",
                "Pick another refactoringIndex from symbol_list_refactorings."));
        }

        return (new CodeRefactoringPreviewDraft(
            chosen.Title,
            chosen.EquivalenceKey,
            handle,
            slices,
            InvalidatedHandles: [handle]), null);
    }

    private async Task<(Document? Document, TextSpan Span, SymbolQueryError? Error)> ResolveAnchorAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken)
    {
        if (!SymbolHandle.TryParse(handle, out var parsed, out var parseError) || parsed is null)
        {
            return (null, default, new InvalidSymbolHandleError(
                parseError ?? "Handle format or checksum is invalid.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle; do not invent handles."));
        }

        if (_languages.TryGet(parsed.Language, out var adapter) && !adapter.SupportsCodeRefactoring)
        {
            return (null, default, new RefactoringLanguageNotSupportedError(
                "Code Refactoring is not available for this language.",
                "Call symbol_list_refactorings on a handwritten csharp or vb SymbolHandle."));
        }

        var (project, symbol, resolveError) = await _symbols
            .ResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (resolveError is not null)
        {
            return (null, default, resolveError);
        }

        var (attribution, attrError) = await _symbols
            .GetAttributionAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (attrError is not null)
        {
            return (null, default, attrError);
        }

        if (attribution!.Attribution.OriginKind == SymbolOrigin.SourceGenerator)
        {
            return (null, default, new GeneratedSymbolRefactoringRefusedError(
                "SourceGenerator declarations cannot be refactored.",
                "Change the generator input (handwritten partial / attribute) and call symbol_list_refactorings on that symbol."));
        }

        var location = symbol!.Locations.FirstOrDefault(static l => l.IsInSource && l.SourceTree is not null);
        if (location is null)
        {
            return (null, default, new SymbolNotFoundError(
                "Symbol has no in-source identifier span.",
                "Call symbol_list_refactorings on a handwritten source symbol."));
        }

        var document = project!.GetDocument(location.SourceTree!)
                       ?? project.Documents.FirstOrDefault(d =>
                           PathsEqual(d.FilePath, location.GetLineSpan().Path));
        if (document is null)
        {
            return (null, default, new GeneratedDocumentRefactoringRefusedError(
                "This symbol is declared in a generated document.",
                "Change the generator input instead of applying a Code Refactoring to generated output."));
        }

        return (document, location.SourceSpan, null);
    }

    private static async Task<IReadOnlyList<CodeAction>> CollectActionsAsync(
        Document document,
        TextSpan span,
        CancellationToken cancellationToken)
    {
        var providers = CodeActionDocuments.GetProviders<CodeRefactoringProvider>(document.Project.Language);
        var actions = new List<CodeAction>();
        foreach (var provider in providers)
        {
            try
            {
                var context = new CodeRefactoringContext(
                    document,
                    span,
                    action => actions.AddRange(CodeActionDocuments.Flatten(action)),
                    cancellationToken);
                await provider.ComputeRefactoringsAsync(context).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Skip providers that require MEF / VS services at compute time.
            }
        }

        return actions
            .OrderBy(static a => a.Title, StringComparer.Ordinal)
            .ThenBy(static a => a.EquivalenceKey ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
