namespace DotNetMcp.Core;

public sealed record SymbolSummary(
    string Kind,
    string DisplayName,
    string? ContainingSymbol,
    string Accessibility,
    string ProjectId,
    string Language);

public sealed record SymbolResolveSuccess(string Handle, SymbolSummary Summary);

public sealed record SymbolLocation(
    string DeclarationAvailability,
    string? Origin,
    string? FilePath,
    int? Start,
    int? Length);

public sealed record SymbolDefinitionSuccess(IReadOnlyList<SymbolLocation> Locations);

public sealed record MemberListItem(string Handle, SymbolSummary Summary);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    bool Truncated,
    string? NextCursor,
    string Message);

public abstract record SymbolQueryError(string Code, string Message, string SuggestedAction);

public sealed record InvalidSymbolHandleError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.InvalidSymbolHandle, Message, SuggestedAction);

public sealed record SymbolNotFoundError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.SymbolNotFound, Message, SuggestedAction);

public sealed record SymbolAmbiguousError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.SymbolAmbiguous, Message, SuggestedAction);

public sealed record StaleCursorError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.StaleCursor, Message, SuggestedAction);

public sealed record DefinitionNotFoundError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.DefinitionNotFound, Message, SuggestedAction);

public static class SymbolQueryErrorCodes
{
    public const string InvalidSymbolHandle = "InvalidSymbolHandle";
    public const string SymbolNotFound = "SymbolNotFound";
    public const string SymbolAmbiguous = "SymbolAmbiguous";
    public const string StaleCursor = "StaleCursor";
    public const string DefinitionNotFound = "DefinitionNotFound";
}

public static class DeclarationAvailability
{
    public const string InSource = "InSource";
    public const string InMetadata = "InMetadata";
    public const string None = "None";
}

public static class SymbolOrigin
{
    public const string Handwritten = "Handwritten";
    public const string SourceGenerated = "SourceGenerated";
}
