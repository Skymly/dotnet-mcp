namespace DotNetMcp.Core;

public sealed record SymbolSummary(
    string Kind,
    string DisplayName,
    string? ContainingSymbol,
    string Accessibility,
    string ProjectId,
    string Language);

public sealed record SymbolResolveSuccess(string Handle, SymbolSummary Summary);

public abstract record SymbolQueryError(string Code, string Message, string SuggestedAction);

public sealed record InvalidSymbolHandleError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.InvalidSymbolHandle, Message, SuggestedAction);

public sealed record SymbolNotFoundError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.SymbolNotFound, Message, SuggestedAction);

public sealed record SymbolAmbiguousError(string Message, string SuggestedAction)
    : SymbolQueryError(SymbolQueryErrorCodes.SymbolAmbiguous, Message, SuggestedAction);

public static class SymbolQueryErrorCodes
{
    public const string InvalidSymbolHandle = "InvalidSymbolHandle";
    public const string SymbolNotFound = "SymbolNotFound";
    public const string SymbolAmbiguous = "SymbolAmbiguous";
}
