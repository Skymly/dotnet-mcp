namespace DotNetMcp.Core;

public sealed record DynamicInvocationItem(
    string Kind,
    string? FilePath,
    int? Start,
    int? Length,
    string ProjectId,
    string? ReceiverStaticType,
    IReadOnlyList<string?> ArgumentStaticTypes);
