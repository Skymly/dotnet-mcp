namespace DotNetMcp.Core;

public sealed record RenameDocumentSlice(string Path, string OldText, string NewText);

/// <summary>
/// A name-matched XAML usage reported but not edited (ADR-0006): the usage
/// point's span text equals the old simple name, yet its Confidence is
/// <see cref="XamlUsageConfidence.Unverified"/>, so the rename preview carries
/// it as evidence without writing it. Positions use the unified 1-based
/// quad (<see cref="XamlUsagePoint"/> semantics).
/// </summary>
public sealed record UnverifiedXamlMention(
    string Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Text);

/// <summary>
/// Values of <see cref="UnverifiedMention.Kind"/> (issue #562).
/// </summary>
public static class UnverifiedMentionKind
{
    /// <summary>A string literal holds the symbol's qualified/metadata name.</summary>
    public const string MetadataName = "metadataName";

    /// <summary>An attribute usage's short or full name matches but is not bound to the renamed type.</summary>
    public const string UnboundAttribute = "unboundAttribute";

    /// <summary>A qualified-name hit in comments, doc comments, or additional documents; opt-in.</summary>
    public const string Documentation = "documentation";
}

/// <summary>
/// A name-matched hit outside the rename write set (issue #562), reported as
/// evidence and never edited — same contract as
/// <see cref="UnverifiedXamlMention"/> for source-side text: string literals
/// carrying the symbol's qualified name, attribute usages whose short or full
/// name is not bound to the renamed type, and (only when the caller asks for
/// them) qualified-name hits in comments and additional documents. Positions
/// use the unified 1-based quad; <see cref="Text"/> is the matched span text.
/// </summary>
public sealed record UnverifiedMention(
    string Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Text,
    string Kind,
    string Reason);

public sealed record RenamePreviewDraft(
    string OldHandle,
    string NewName,
    IReadOnlyList<RenameDocumentSlice> Documents,
    IReadOnlyList<string> InvalidatedHandles,
    IReadOnlyList<UnverifiedXamlMention>? UnverifiedXamlMentions = null,
    IReadOnlyList<UnverifiedMention>? UnverifiedMentions = null);
