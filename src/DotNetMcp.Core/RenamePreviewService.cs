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

    private readonly LanguageAdapters _languages;

    public RenamePreviewService(LanguageAdapters languages)
    {
        _languages = languages;
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

        if (!_languages.TryGetForHandle(handle, out var adapter, out var error))
        {
            return (null, error);
        }

        return await adapter
            .BuildRenamePreviewAsync(session, handle, newName, cancellationToken)
            .ConfigureAwait(false);
    }
}
