using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;

namespace DotNetMcp.Core;

public sealed record RenameDocumentSlice(string Path, string OldText, string NewText);

public sealed record RenamePreviewDraft(
    string OldHandle,
    string NewName,
    IReadOnlyList<RenameDocumentSlice> Documents,
    IReadOnlyList<string> InvalidatedHandles);

public sealed class RenamePreviewService
{
    public static readonly SymbolRenameOptions DefaultOptions = new(
        RenameOverloads: false,
        RenameInStrings: false,
        RenameInComments: false,
        RenameFile: false);

    private readonly SymbolQueryService _symbols;

    public RenamePreviewService(SymbolQueryService symbols) => _symbols = symbols;

    public async Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildAsync(
        IWorkspaceSession session,
        string handle,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(['.', ' ', '\t']) >= 0)
        {
            return (null, new InvalidRenameNameError(
                "New name must be a single identifier.",
                "Pass a C# identifier (no qualification) as newName."));
        }

        if (!SymbolHandle.TryParse(handle, out var parsed, out var parseError) || parsed is null)
        {
            return (null, new InvalidSymbolHandleError(
                parseError ?? "Handle format or checksum is invalid.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle; do not invent handles."));
        }

        if (string.Equals(parsed.Language, SymbolQueryService.FSharpLanguage, StringComparison.Ordinal) ||
            string.Equals(parsed.Language, SymbolQueryService.VbLanguage, StringComparison.Ordinal))
        {
            return (null, new RenameLanguageNotSupportedError(
                $"Rename preview for '{parsed.Language}' handles is not available in this release.",
                parsed.Language == SymbolQueryService.VbLanguage
                    ? "Use a csharp SymbolHandle. VB rename ships in a later 2.0 slice."
                    : "F# rename is out of scope; call symbol_resolve for a handwritten C# symbol."));
        }

        var (project, symbol, resolveError) = await _symbols
            .ResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (resolveError is not null)
        {
            return (null, resolveError);
        }

        var (attribution, attrError) = await _symbols
            .GetAttributionAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (attrError is not null)
        {
            return (null, attrError);
        }

        if (attribution!.Attribution.OriginKind == SymbolOrigin.SourceGenerator)
        {
            return (null, new GeneratedSymbolRenameRefusedError(
                "SourceGenerator declarations cannot be renamed.",
                "Change the generator input (handwritten partial / attribute) and call symbol_preview_rename on that symbol."));
        }

        if (string.Equals(symbol!.Name, newName, StringComparison.Ordinal))
        {
            return (null, new InvalidRenameNameError(
                $"New name '{newName}' is identical to the current symbol name.",
                "Pass a different identifier as newName."));
        }

        var renamed = await Renamer.RenameSymbolAsync(
            session.Solution,
            symbol,
            DefaultOptions,
            newName,
            cancellationToken).ConfigureAwait(false);

        var slices = new List<RenameDocumentSlice>();
        foreach (var projectChange in renamed.GetChanges(session.Solution).GetProjectChanges())
        {
            foreach (var docId in projectChange.GetChangedDocuments())
            {
                var oldDoc = session.Solution.GetDocument(docId);
                var newDoc = renamed.GetDocument(docId);
                if (oldDoc is null || newDoc is null)
                {
                    continue;
                }

                var oldText = (await oldDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var newText = (await newDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                if (oldText == newText)
                {
                    continue;
                }

                var path = oldDoc.FilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                slices.Add(new RenameDocumentSlice(path, oldText, newText));
            }
        }

        var invalidated = new List<string> { handle };
        if (symbol is INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers().Where(static m => !m.IsImplicitlyDeclared))
            {
                if (member.Kind is SymbolKind.NamedType or SymbolKind.Namespace)
                {
                    continue;
                }

                var memberHandle = SymbolHandle.Create(
                    parsed.Language,
                    parsed.ProjectId,
                    member.ToDisplayString(SymbolDisplayFormats.SignatureQualified)).Format();
                if (!invalidated.Contains(memberHandle, StringComparer.Ordinal))
                {
                    invalidated.Add(memberHandle);
                }
            }
        }

        return (new RenamePreviewDraft(handle, newName, slices, invalidated), null);
    }
}
