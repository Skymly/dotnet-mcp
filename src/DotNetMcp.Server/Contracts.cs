using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetMcp.Server;

public static class JsonOptions
{
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}

public static class PolicyErrorCodes
{
    public const string PathOutsideTrustedRoots = "PathOutsideTrustedRoots";
    public const string WorkspaceNotReady = "WorkspaceNotReady";
    public const string InvalidWorkspacePath = "InvalidWorkspacePath";
    public const string InvalidSymbolHandle = "InvalidSymbolHandle";
    public const string SymbolNotFound = "SymbolNotFound";
    public const string SymbolAmbiguous = "SymbolAmbiguous";
    public const string StaleCursor = "StaleCursor";
    public const string DefinitionNotFound = "DefinitionNotFound";
}

public sealed record PolicyErrorDto
{
    public required string Error { get; init; }
    public required string Message { get; init; }
    public required string SuggestedAction { get; init; }
}

/// <summary>
/// Shared open/status payload (ADR-0003 §1 / Spike S3). No jobId — single active workspace.
/// </summary>
public sealed record WorkspaceStatusDto
{
    public required string Phase { get; init; }
    public int CompletedUnits { get; init; }
    public int TotalUnits { get; init; }
    public long ElapsedMs { get; init; }
    public long EstimatedRemainingMs { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public required string SuggestedAction { get; init; }
}

public sealed record ProjectSummaryDto
{
    public required string ProjectId { get; init; }
    public required string Name { get; init; }
    public string? TargetFramework { get; init; }
    public string? FilePath { get; init; }
}

public sealed record WorkspaceListProjectsResultDto
{
    public required IReadOnlyList<ProjectSummaryDto> Projects { get; init; }
}

public sealed record SymbolSummaryDto
{
    public required string Kind { get; init; }
    public required string DisplayName { get; init; }
    public string? ContainingSymbol { get; init; }
    public required string Accessibility { get; init; }
    public required string ProjectId { get; init; }
    public required string Language { get; init; }
}

public sealed record SymbolResolveResultDto
{
    public required string Handle { get; init; }
    public required SymbolSummaryDto Summary { get; init; }
}

public sealed record SymbolLocationDto
{
    public required string DeclarationAvailability { get; init; }
    public string? Origin { get; init; }
    public string? FilePath { get; init; }
    public int? Start { get; init; }
    public int? Length { get; init; }
}

public sealed record SymbolDefinitionResultDto
{
    public required IReadOnlyList<SymbolLocationDto> Locations { get; init; }
}

public sealed record MemberListItemDto
{
    public required string Handle { get; init; }
    public required SymbolSummaryDto Summary { get; init; }
}

public sealed record SymbolMembersResultDto
{
    public required IReadOnlyList<MemberListItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public required string Message { get; init; }
}

/// <summary>Backward-compatible alias used by older tests / call sites. </summary>
public sealed record WorkspaceOpenResultDto
{
    public required string Phase { get; init; }
    public string? Message { get; init; }
    public string? SuggestedAction { get; init; }
    public int CompletedUnits { get; init; }
    public int TotalUnits { get; init; }
    public long ElapsedMs { get; init; }
    public long EstimatedRemainingMs { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
    public string? Error { get; init; }

    public static WorkspaceOpenResultDto FromStatus(WorkspaceStatusDto status) => new()
    {
        Phase = status.Phase,
        Message = status.Message,
        SuggestedAction = status.SuggestedAction,
        CompletedUnits = status.CompletedUnits,
        TotalUnits = status.TotalUnits,
        ElapsedMs = status.ElapsedMs,
        EstimatedRemainingMs = status.EstimatedRemainingMs,
        Warnings = status.Warnings,
        Error = status.Error
    };
}
