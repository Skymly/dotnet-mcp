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

public sealed record RenamePreviewDraft(
    string OldHandle,
    string NewName,
    IReadOnlyList<RenameDocumentSlice> Documents,
    IReadOnlyList<string> InvalidatedHandles,
    IReadOnlyList<UnverifiedXamlMention>? UnverifiedXamlMentions = null);
