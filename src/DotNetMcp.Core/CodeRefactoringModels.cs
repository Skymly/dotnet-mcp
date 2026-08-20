namespace DotNetMcp.Core;

public sealed record CodeRefactoringItem(
    int RefactoringIndex,
    string Title,
    string? EquivalenceKey);

public sealed record CodeRefactoringListSuccess(IReadOnlyList<CodeRefactoringItem> Items);

public sealed record CodeRefactoringPreviewDraft(
    string Title,
    string? EquivalenceKey,
    string Handle,
    IReadOnlyList<RenameDocumentSlice> Documents,
    IReadOnlyList<string> InvalidatedHandles);

public sealed record RefactoringLanguageNotSupportedError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.RefactoringLanguageNotSupported, Message, SuggestedAction);

public sealed record RefactoringIndexOutOfRangeError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.RefactoringIndexOutOfRange, Message, SuggestedAction);

public sealed record GeneratedSymbolRefactoringRefusedError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.GeneratedSymbolRefactoringRefused, Message, SuggestedAction);

public sealed record GeneratedDocumentRefactoringRefusedError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.GeneratedDocumentRefactoringRefused, Message, SuggestedAction);

public sealed record RefactoringApplyFailedError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.RefactoringApplyFailed, Message, SuggestedAction);
