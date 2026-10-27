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
    public const string TrustedRootsConfigurationFailed = "TrustedRootsConfigurationFailed";
    public const string LoadedGraphOutsideTrustedRoots = "LoadedGraphOutsideTrustedRoots";
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
    public const string GeneratorLanguageNotSupported = "GeneratorLanguageNotSupported";
    public const string DynamicInvocationLanguageNotSupported = "DynamicInvocationLanguageNotSupported";
    public const string MissingXamlClass = "MissingXamlClass";
    public const string XamlDocumentNotFound = "XamlDocumentNotFound";
    public const string XamlDocumentAmbiguous = "XamlDocumentAmbiguous";
    public const string UnsupportedXamlDocument = "UnsupportedXamlDocument";
    public const string UnknownXmlnsPrefix = "UnknownXmlnsPrefix";
    public const string MissingXamlName = "MissingXamlName";
    public const string NameGeneratorNotRun = "NameGeneratorNotRun";
    public const string BindingPropertyNotFound = "BindingPropertyNotFound";
    public const string BindingTypeMismatch = "BindingTypeMismatch";
    public const string MissingDataType = "MissingDataType";
    public const string XamlParseError = "XamlParseError";
    public const string GeneratedSymbolRenameRefused = "GeneratedSymbolRenameRefused";
    public const string RenameLanguageNotSupported = "RenameLanguageNotSupported";
    public const string InvalidRenameName = "InvalidRenameName";
    public const string FSharpCrossProjectRenameUnsupported = "FSharpCrossProjectRenameUnsupported";
    public const string PreviewNotFound = "PreviewNotFound";
    public const string PreviewExpired = "PreviewExpired";
    public const string PreviewEpochMismatch = "PreviewEpochMismatch";
    public const string PreviewKindMismatch = "PreviewKindMismatch";
    public const string PreviewPathOutsideTrustedRoots = "PreviewPathOutsideTrustedRoots";
    public const string PreviewTargetMissing = "PreviewTargetMissing";
    public const string PreviewTextMismatch = "PreviewTextMismatch";
    public const string SourceEncodingRefused = "SourceEncodingRefused";
    public const string RenameApplyFailed = "RenameApplyFailed";
    public const string WorkspaceEditApplyFailed = "WorkspaceEditApplyFailed";
    public const string DiagnosticNotFound = "DiagnosticNotFound";
    public const string DiagnosticAmbiguous = "DiagnosticAmbiguous";
    public const string FixLanguageNotSupported = "FixLanguageNotSupported";
    public const string FixIndexOutOfRange = "FixIndexOutOfRange";
    public const string FixListEpochMismatch = "FixListEpochMismatch";
    public const string GeneratedDocumentFixRefused = "GeneratedDocumentFixRefused";
    public const string FixApplyFailed = "FixApplyFailed";
    public const string FixAllUnavailable = "FixAllUnavailable";
    public const string FixAllBudgetExceeded = "FixAllBudgetExceeded";
    public const string RefactoringLanguageNotSupported = "RefactoringLanguageNotSupported";
    public const string RefactoringIndexOutOfRange = "RefactoringIndexOutOfRange";
    public const string RefactoringListEpochMismatch = "RefactoringListEpochMismatch";
    public const string GeneratedSymbolRefactoringRefused = "GeneratedSymbolRefactoringRefused";
    public const string GeneratedDocumentRefactoringRefused = "GeneratedDocumentRefactoringRefused";
    public const string RefactoringApplyFailed = "RefactoringApplyFailed";
    public const string DocumentAddOrRemoveRefused = "DocumentAddOrRemoveRefused";
    public const string DocumentNotInWorkspace = "DocumentNotInWorkspace";
    public const string UnsupportedDocumentForPosition = "UnsupportedDocumentForPosition";
    public const string InvalidPosition = "InvalidPosition";
    public const string NoSymbolAtPosition = "NoSymbolAtPosition";
    public const string SymbolNotAddressable = "SymbolNotAddressable";
    public const string SourceUnavailable = "SourceUnavailable";
    public const string SymbolQueryFailed = "SymbolQueryFailed";
}

public sealed record XamlBindingSegmentDto
{
    public required string Name { get; init; }
    public required string Handle { get; init; }
    public required SymbolSummaryDto Summary { get; init; }
}

public sealed record XamlResolveBindingResultDto
{
    public required IReadOnlyList<XamlBindingSegmentDto> Items { get; init; }
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
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public required string SuggestedAction { get; init; }

    /// <summary>ok, lost, or off. Additive; absent on older clients is treated as unknown.</summary>
    public string Watcher { get; init; } = "off";
}

public sealed record ProjectSummaryDto
{
    public required string ProjectId { get; init; }
    public required string Name { get; init; }
    public required string Language { get; init; }
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
    public string InteropKind { get; init; } = "None";
}

public sealed record SymbolResolveResultDto
{
    public required string Handle { get; init; }
    public required SymbolSummaryDto Summary { get; init; }
}

public sealed record MatchedIdentifierDto
{
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required int EndLine { get; init; }
    public required int EndColumn { get; init; }
    public required string Text { get; init; }
}

public sealed record SymbolResolveAtResultDto
{
    public required string Handle { get; init; }
    public required SymbolSummaryDto Summary { get; init; }
    public required MatchedIdentifierDto Matched { get; init; }
    public required string ResolvedFrom { get; init; }
    public string? CandidateReason { get; init; }
}

public sealed record SymbolLocationDto
{
    public required string DeclarationAvailability { get; init; }
    public string? Origin { get; init; }
    public string? FilePath { get; init; }
    public int? Start { get; init; }
    public int? Length { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
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
    public string? Message { get; init; }
}

public sealed record ReferenceLocationItemDto
{
    public required string DeclarationAvailability { get; init; }
    public string? Origin { get; init; }
    public string? FilePath { get; init; }
    public int? Start { get; init; }
    public int? Length { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public required string ProjectId { get; init; }
    public required string Kind { get; init; }

    /// <summary>XAML usage kind for Exact XAML hits (ADR-0006); omitted/null on source hits.</summary>
    public string? XamlUsage { get; init; }
}

public sealed record SymbolFindReferencesResultDto
{
    public required IReadOnlyList<ReferenceLocationItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public string? Message { get; init; }
}

public sealed record CallerLocationItemDto
{
    public required string DeclarationAvailability { get; init; }
    public string? Origin { get; init; }
    public string? FilePath { get; init; }
    public int? Start { get; init; }
    public int? Length { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public required string ProjectId { get; init; }
    public required string CallerHandle { get; init; }
    public required SymbolSummaryDto CallerSummary { get; init; }
}

public sealed record SymbolFindCallersResultDto
{
    public required IReadOnlyList<CallerLocationItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public string? Message { get; init; }
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
    public string? Message { get; init; }
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
    public string? Message { get; init; }
}

public sealed record DiagnosticItemDto
{
    public required string Id { get; init; }
    public required string Severity { get; init; }
    public required string Message { get; init; }
    public string? FilePath { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public required string ProjectId { get; init; }
    public string? Error { get; init; }
    public string? SuggestedAction { get; init; }
}

public sealed record ProjectDiagnosticsResultDto
{
    public required IReadOnlyList<DiagnosticItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string>? MissingDependencyOutputs { get; init; }

    /// <summary>
    /// true when the compilation did not resolve all compile inputs — it reported
    /// CS0006/CS0012 metadata diagnostics, a declared project reference resolved
    /// to no usable metadata (issue #560), or a declared analyzer/generator
    /// reference did not materialize (issue #566) — so listed errors may be
    /// workspace artifacts rather than real code defects. Omitted when false
    /// (issue #546).
    /// </summary>
    public bool? Degraded { get; init; }

    /// <summary>
    /// Why <see cref="Degraded"/> is set: the CS0006/CS0012 count, the declared
    /// project references that produced no usable metadata, and/or the declared
    /// analyzer/generator paths that did not materialize.
    /// </summary>
    public string? DegradedReason { get; init; }
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

    /// <summary>
    /// Analyzer paths MSBuild declared for the project that no loadable assembly materialized
    /// for — an OutputItemType="Analyzer" project reference whose output is not built, or a
    /// missing analyzer DLL (issue #551). Paths only: the generator types inside are unknown
    /// until the assembly exists and the workspace is reopened. Omitted when every declared
    /// analyzer reference resolved.
    /// </summary>
    public IReadOnlyList<string>? UnresolvedAnalyzerReferences { get; init; }
    public string? Message { get; init; }
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
    public string? Message { get; init; }
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
    public string? Message { get; init; }
    public long Epoch { get; init; }
}

/// <summary>
/// One handwritten construct that caused a generated member to exist
/// (issue #524). kind: PartialDefinition | Attribute | XamlElement;
/// confidence: Exact | Convention. Positions follow the unified model:
/// 1-based line/column in UTF-16 code units, endColumn exclusive;
/// start/length are raw offsets of the same span. handle is present for
/// code triggers and re-resolves via symbol_summary / symbol_resolve.
/// </summary>
public sealed record GenerationTriggerDto
{
    public required string Kind { get; init; }
    public required string Confidence { get; init; }
    public string? Handle { get; init; }
    public string? Path { get; init; }
    public int? Start { get; init; }
    public int? Length { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public string? Detail { get; init; }
}

public sealed record SymbolAttributionDto
{
    public required string DeclarationAvailability { get; init; }
    public required string OriginKind { get; init; }
    public GeneratorIdentityDto? Generator { get; init; }
    public required IReadOnlyList<GenerationTriggerDto> Triggers { get; init; }
    public string? TriggerMessage { get; init; }
}

public sealed record SymbolAttributionResultDto
{
    public required string DeclarationAvailability { get; init; }
    public required string OriginKind { get; init; }
    public GeneratorIdentityDto? Generator { get; init; }
    public required IReadOnlyList<GenerationTriggerDto> Triggers { get; init; }
    public string? TriggerMessage { get; init; }
    public required IReadOnlyDictionary<string, SymbolAttributionDto> Members { get; init; }
}

public sealed record SymbolSourceDeclarationDto
{
    public required string Origin { get; init; }
    public GeneratorIdentityDto? Generator { get; init; }
    public string? FilePath { get; init; }
    public int Line { get; init; }
    public int Column { get; init; }
    public int EndLine { get; init; }
    public int EndColumn { get; init; }
    public required string Text { get; init; }
    public bool Truncated { get; init; }
}

public sealed record SymbolSourceResultDto
{
    public required IReadOnlyList<SymbolSourceDeclarationDto> Declarations { get; init; }
    public bool Truncated { get; init; }
    public string? Message { get; init; }
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

/// <summary>
/// Production payload for workspace_open. Mapped from WorkspaceStatusDto,
/// including ErrorCode when the open fails before the background load.
/// </summary>
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
    public string? ErrorCode { get; init; }

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
        Error = status.Error,
        ErrorCode = status.ErrorCode
    };
}


public sealed record DynamicInvocationItemDto
{
    public required string Kind { get; init; }
    public string? FilePath { get; init; }
    public int? Start { get; init; }
    public int? Length { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
    public required string ProjectId { get; init; }
    public string? ReceiverStaticType { get; init; }
    public IReadOnlyList<string?> ArgumentStaticTypes { get; init; } = [];
}

public sealed record ProjectListDynamicInvocationsResultDto
{
    public required IReadOnlyList<DynamicInvocationItemDto> Items { get; init; }
    public bool Truncated { get; init; }
    public string? NextCursor { get; init; }
    public string? Message { get; init; }
}


public sealed record RenameDocumentSliceDto
{
    public required string Path { get; init; }
    public required string OldText { get; init; }
    public required string NewText { get; init; }
}

public sealed record UnverifiedXamlMentionDto
{
    public required string Path { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required int EndLine { get; init; }
    public required int EndColumn { get; init; }
    public required string Text { get; init; }
}

public sealed record SymbolPreviewRenameResultDto
{
    public required string PreviewId { get; init; }
    public required long Epoch { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string OldHandle { get; init; }
    public required string NewName { get; init; }
    public required IReadOnlyList<RenameDocumentSliceDto> Documents { get; init; }
    public required IReadOnlyList<string> InvalidatedHandles { get; init; }
    public IReadOnlyList<UnverifiedXamlMentionDto>? UnverifiedXamlMentions { get; init; }
}


public sealed record SymbolApplyRenameResultDto
{
    public required string PreviewId { get; init; }
    public required long Epoch { get; init; }
    public required IReadOnlyList<string> WrittenPaths { get; init; }
    public required IReadOnlyList<string> InvalidatedHandles { get; init; }
}

public sealed record DiagnosticFixItemDto
{
    public required int FixIndex { get; init; }
    public required string Title { get; init; }
    public string? EquivalenceKey { get; init; }
}

public sealed record DiagnosticsListFixesResultDto
{
    public required bool IncludesProjectAnalyzers { get; init; }
    public required long Epoch { get; init; }
    public required IReadOnlyList<DiagnosticFixItemDto> Items { get; init; }

    /// <summary>
    /// true when the diagnostic's project did not resolve all compile inputs
    /// (issues #560/#566): fixes on cascade errors from missing metadata or
    /// generator output may not apply cleanly — verify with dotnet build
    /// first. Omitted when not degraded.
    /// </summary>
    public bool? Degraded { get; init; }

    /// <summary>Why <see cref="Degraded"/> is set.</summary>
    public string? DegradedReason { get; init; }
}

public sealed record DiagnosticsPreviewFixResultDto
{
    public required string PreviewId { get; init; }
    public required long Epoch { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string Title { get; init; }
    public string? EquivalenceKey { get; init; }
    public required string Scope { get; init; }
    public required IReadOnlyList<RenameDocumentSliceDto> Documents { get; init; }
    public required IReadOnlyList<string> InvalidatedHandles { get; init; }

    /// <summary>
    /// true when the diagnostic's project did not resolve all compile inputs
    /// (issues #560/#566): a preview built on a cascade error can be wrong —
    /// verify with dotnet build first. Omitted when not degraded.
    /// </summary>
    public bool? Degraded { get; init; }

    /// <summary>Why <see cref="Degraded"/> is set.</summary>
    public string? DegradedReason { get; init; }
}

public sealed record DiagnosticsApplyFixResultDto
{
    public required string PreviewId { get; init; }
    public required long Epoch { get; init; }
    public required IReadOnlyList<string> WrittenPaths { get; init; }
    public required IReadOnlyList<string> InvalidatedHandles { get; init; }
}

public sealed record CodeRefactoringItemDto
{
    public required int RefactoringIndex { get; init; }
    public required string Title { get; init; }
    public string? EquivalenceKey { get; init; }
}

public sealed record SymbolListRefactoringsResultDto
{
    public required bool IncludesProjectAnalyzers { get; init; }
    public required long Epoch { get; init; }
    public required IReadOnlyList<CodeRefactoringItemDto> Items { get; init; }
}

public sealed record SymbolPreviewRefactoringResultDto
{
    public required string PreviewId { get; init; }
    public required long Epoch { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string Title { get; init; }
    public string? EquivalenceKey { get; init; }
    public required string Handle { get; init; }
    public required IReadOnlyList<RenameDocumentSliceDto> Documents { get; init; }
    public required IReadOnlyList<string> InvalidatedHandles { get; init; }
}

public sealed record SymbolApplyRefactoringResultDto
{
    public required string PreviewId { get; init; }
    public required long Epoch { get; init; }
    public required IReadOnlyList<string> WrittenPaths { get; init; }
    public required IReadOnlyList<string> InvalidatedHandles { get; init; }
}

