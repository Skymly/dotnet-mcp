namespace DotNetMcp.Core;

public sealed record RenameDocumentSlice(string Path, string OldText, string NewText);

public sealed record RenamePreviewDraft(
    string OldHandle,
    string NewName,
    IReadOnlyList<RenameDocumentSlice> Documents,
    IReadOnlyList<string> InvalidatedHandles);
