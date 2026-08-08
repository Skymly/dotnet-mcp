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
