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
    public const string ProjectNotFound = "ProjectNotFound";
    public const string CompilationUnavailable = "CompilationUnavailable";
    public const string SoftBudgetExceeded = "SoftBudgetExceeded";
    public const string GeneratorNotFound = "GeneratorNotFound";
    public const string MissingXamlClass = "MissingXamlClass";
    public const string XamlDocumentNotFound = "XamlDocumentNotFound";
    public const string UnsupportedXamlDocument = "UnsupportedXamlDocument";
    public const string UnknownXmlnsPrefix = "UnknownXmlnsPrefix";
    public const string MissingXamlName = "MissingXamlName";
    public const string NameGeneratorNotRun = "NameGeneratorNotRun";
}

public sealed record XamlXmlnsMappingDto
{
    public required string Prefix { get; init; }
    public required string XmlNamespace { get; init; }
    public string? ClrNamespace { get; init; }
    public string? AssemblyName { get; init; }
    public required string Source { get; init; }
}

public sealed record XamlListXmlnsResultDto
{
    public required IReadOnlyList<XamlXmlnsMappingDto> Items { get; init; }
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

public sealed record ReferenceLocationItemDto
{
    public required string DeclarationAvailability { get; init; }
    public string? Origin { get; init; }
    public string? FilePath { get; init; }
    public int? Start { get; init; }
    public int? Length { get; init; }
    public required string ProjectId { get; init; }
    public required string Kind { get; init; }
}

public sealed record SymbolFindReferencesResultDto
{
    public required IReadOnlyList<ReferenceLocationItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public required string Message { get; init; }
}

public sealed record CallerLocationItemDto
{
    public required string DeclarationAvailability { get; init; }
    public string? Origin { get; init; }
    public string? FilePath { get; init; }
    public int? Start { get; init; }
    public int? Length { get; init; }
    public required string ProjectId { get; init; }
    public required string CallerHandle { get; init; }
    public required SymbolSummaryDto CallerSummary { get; init; }
}

public sealed record SymbolFindCallersResultDto
{
    public required IReadOnlyList<CallerLocationItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public required string Message { get; init; }
}

public sealed record ImplementationItemDto
{
    public required string Handle { get; init; }
    public required SymbolSummaryDto Summary { get; init; }
    public required IReadOnlyList<SymbolLocationDto> Locations { get; init; }
}

public sealed record SymbolFindImplementationsResultDto
{
    public required IReadOnlyList<ImplementationItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public required string Message { get; init; }
}

public sealed record HierarchyItemDto
{
    public required string Kind { get; init; }
    public required string Handle { get; init; }
    public required SymbolSummaryDto Summary { get; init; }
}

public sealed record SymbolTypeHierarchyResultDto
{
    public required IReadOnlyList<HierarchyItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public required string Message { get; init; }
}

public sealed record DiagnosticItemDto
{
    public required string Id { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public string? FilePath { get; init; }
    public int? StartLine { get; init; }
    public int? StartCharacter { get; init; }
    public int? EndLine { get; init; }
    public int? EndCharacter { get; init; }
    public required string ProjectId { get; init; }
}

public sealed record ProjectDiagnosticsResultDto
{
    public required IReadOnlyList<DiagnosticItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public required string Message { get; init; }
}

public sealed record GeneratorIdentityDto
{
    public required string AssemblyName { get; init; }
    public required string TypeFullName { get; init; }
    public required string Version { get; init; }
}

public sealed record ProjectListGeneratorsResultDto
{
    public required IReadOnlyList<GeneratorIdentityDto> Generators { get; init; }
    public long Epoch { get; init; }
}

public sealed record GeneratedSourceItemDto
{
    public required string HintName { get; init; }
    public required string Content { get; init; }
}

public sealed record ProjectListGeneratedSourcesResultDto
{
    public required IReadOnlyList<GeneratedSourceItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public required string Message { get; init; }
    public long Epoch { get; init; }
}

public sealed record GeneratorDiagnosticItemDto
{
    public required string Id { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
}

public sealed record ProjectListGeneratorDiagnosticsResultDto
{
    public required GeneratorIdentityDto Generator { get; init; }
    public required IReadOnlyList<GeneratorDiagnosticItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public required string Message { get; init; }
    public long Epoch { get; init; }
}

public sealed record SymbolAttributionDto
{
    public required string DeclarationAvailability { get; init; }
    public required string OriginKind { get; init; }
    public GeneratorIdentityDto? Generator { get; init; }
}

public sealed record SymbolAttributionResultDto
{
    public required string DeclarationAvailability { get; init; }
    public required string OriginKind { get; init; }
    public GeneratorIdentityDto? Generator { get; init; }
    public required IReadOnlyDictionary<string, SymbolAttributionDto> Members { get; init; }
}

public sealed record DriftItemDto
{
    public required string Path { get; init; }
    public required string Kind { get; init; }
    public bool Repaired { get; init; }
}

public sealed record WorkspaceCheckDriftResultDto
{
    public required long Epoch { get; init; }
    public required IReadOnlyList<DriftItemDto> Drifted { get; init; }
    public required string SuggestedAction { get; init; }
}

/// <summary> Backward-compatible alias used by older tests / call sites. </summary>
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
