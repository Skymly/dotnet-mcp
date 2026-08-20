using System.Collections.Concurrent;
using System.Reflection;
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
    private static readonly ConcurrentDictionary<string, IReadOnlyList<CodeRefactoringProvider>> ProvidersByLanguage = new(StringComparer.Ordinal);

    private readonly SymbolQueryService _symbols;

    public CodeRefactoringService(SymbolQueryService symbols)
    {
        _symbols = symbols;
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
        var changed = await ApplyActionAsync(chosen, cancellationToken).ConfigureAwait(false);
        if (changed is null)
        {
            return (null, new RefactoringApplyFailedError(
                $"Code Refactoring '{chosen.Title}' did not produce a solution change.",
                "Pick another refactoringIndex from symbol_list_refactorings, or change the code without this tool."));
        }

        var (slices, generated) = await DiffAsync(session.Solution, changed, cancellationToken).ConfigureAwait(false);
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

        if (string.Equals(parsed.Language, SymbolQueryService.FSharpLanguage, StringComparison.Ordinal))
        {
            return (null, default, new RefactoringLanguageNotSupportedError(
                "Code Refactoring is not available for fsharp handles.",
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
        var providers = GetProviders(document.Project.Language);
        var actions = new List<CodeAction>();
        foreach (var provider in providers)
        {
            try
            {
                var context = new CodeRefactoringContext(
                    document,
                    span,
                    action => actions.AddRange(Flatten(action)),
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

    private static IReadOnlyList<CodeRefactoringProvider> GetProviders(string language)
    {
        return ProvidersByLanguage.GetOrAdd(language, static lang =>
        {
            var assemblyName = lang switch
            {
                LanguageNames.CSharp => "Microsoft.CodeAnalysis.CSharp.Features",
                LanguageNames.VisualBasic => "Microsoft.CodeAnalysis.VisualBasic.Features",
                _ => null
            };
            if (assemblyName is null)
            {
                return [];
            }

            var list = new List<CodeRefactoringProvider>();
            foreach (var type in Assembly.Load(assemblyName).GetTypes())
            {
                if (type.IsAbstract || !typeof(CodeRefactoringProvider).IsAssignableFrom(type))
                {
                    continue;
                }

                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                try
                {
                    if (Activator.CreateInstance(type) is CodeRefactoringProvider provider)
                    {
                        list.Add(provider);
                    }
                }
                catch (Exception)
                {
                    // Some parameterless types still throw in the ctor.
                }
            }

            return list;
        });
    }

    private static IEnumerable<CodeAction> Flatten(CodeAction action)
    {
        var nested = action.NestedActions;
        return nested.Length == 0 ? [action] : nested.SelectMany(Flatten);
    }

    private static async Task<Solution?> ApplyActionAsync(CodeAction action, CancellationToken cancellationToken)
    {
        try
        {
            var operations = await action.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
            return operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
        }
        catch (Exception)
        {
            return null;
        }
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

    private static async Task<(IReadOnlyList<RenameDocumentSlice> Slices, bool TouchedGenerated)> DiffAsync(
        Solution before,
        Solution after,
        CancellationToken cancellationToken)
    {
        var slices = new List<RenameDocumentSlice>();
        var changes = after.GetChanges(before);
        if (changes.GetAddedProjects().Any() || changes.GetRemovedProjects().Any())
        {
            return (slices, true);
        }

        foreach (var projectChange in changes.GetProjectChanges())
        {
            if (projectChange.GetAddedDocuments().Any() || projectChange.GetRemovedDocuments().Any())
            {
                return (slices, true);
            }

            var oldProject = before.GetProject(projectChange.ProjectId);
            var generated = oldProject is null
                ? []
                : await oldProject.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false);
            var generatedIds = generated.Select(g => g.Id).ToHashSet();

            foreach (var docId in projectChange.GetChangedDocuments())
            {
                if (generatedIds.Contains(docId))
                {
                    return (slices, true);
                }

                var oldDoc = before.GetDocument(docId);
                var newDoc = after.GetDocument(docId);
                if (oldDoc is null || newDoc is null)
                {
                    return (slices, true);
                }

                var path = oldDoc.FilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return (slices, true);
                }

                var oldText = (await oldDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var newText = (await newDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                if (oldText == newText)
                {
                    continue;
                }

                slices.Add(new RenameDocumentSlice(path, oldText, newText));
            }
        }

        return (slices, false);
    }
}
