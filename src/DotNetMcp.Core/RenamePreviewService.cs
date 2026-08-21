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
    private readonly IFSharpSymbolQuery? _fsharp;

    public RenamePreviewService(SymbolQueryService symbols, IFSharpSymbolQuery? fsharp = null)
    {
        _symbols = symbols;
        _fsharp = fsharp;
    }

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
        if (string.Equals(parsed.Language, SymbolQueryService.FSharpLanguage, StringComparison.Ordinal))
        {
            if (_fsharp is null)
            {
                return (null, new RenameLanguageNotSupportedError(
                    "Rename preview for 'fsharp' handles is not available.",
                    "F# rename requires the FCS stack; call symbol_resolve for a handwritten fsharp symbol."));
            }

            return await _fsharp
                .BuildRenamePreviewAsync(session, handle, newName, cancellationToken)
                .ConfigureAwait(false);
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

        var (slices, generated) = await HandwrittenDocumentDiff
            .FromSolutionsAsync(session.Solution, renamed, cancellationToken)
            .ConfigureAwait(false);
        if (generated && slices.Count == 0)
        {
            return (null, new GeneratedSymbolRenameRefusedError(
                "This rename would only change generated documents.",
                "Change the generator input (handwritten partial / attribute) and call symbol_preview_rename on that symbol."));
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
