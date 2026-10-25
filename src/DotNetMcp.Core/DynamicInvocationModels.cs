namespace DotNetMcp.Core;

public sealed record DynamicInvocationItem(
    string Kind,
    string? FilePath,
    int? Start,
    int? Length,
    int? Line,
    int? Column,
    int? EndLine,
    int? EndColumn,
    string ProjectId,
    string? ReceiverStaticType,
    IReadOnlyList<string?> ArgumentStaticTypes);
