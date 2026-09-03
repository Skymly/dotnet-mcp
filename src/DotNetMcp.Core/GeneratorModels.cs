namespace DotNetMcp.Core;

public sealed record GeneratorIdentity(
    string AssemblyName,
    string TypeFullName,
    string Version);

public sealed record GeneratedSourceItem(string HintName, string Content);

public sealed record GeneratorDiagnosticItem(
    string Id,
    string Severity,
    string Message);

public sealed record GeneratorDiagnosticsPage(
    GeneratorIdentity Identity,
    PagedResult<GeneratorDiagnosticItem> Page);

public sealed record GeneratorRunSources(
    GeneratorIdentity Identity,
    IReadOnlyList<GeneratedSourceItem> Sources,
    IReadOnlyList<GeneratorDiagnosticItem> Diagnostics);

public sealed record GeneratedSourceMatch(
    GeneratorIdentity Identity,
    string HintName,
    string Content,
    Microsoft.CodeAnalysis.SyntaxTree SyntaxTree);

public sealed record DriverRunSnapshot(
    IReadOnlyList<GeneratorRunSources> ByGenerator,
    IReadOnlyList<GeneratedSourceMatch> FlatSources);

/// <summary>
/// Result of matching a <see cref="Microsoft.CodeAnalysis.SyntaxTree"/> to a driver run.
/// <see cref="Ambiguous"/> means content matched more than one generator identity.
/// </summary>
public readonly record struct GeneratorTreeMatch(GeneratorIdentity? Identity, bool Ambiguous);

public sealed record SymbolAttribution(
    string DeclarationAvailability,
    string OriginKind,
    GeneratorIdentity? Generator);

public sealed record SymbolAttributionSuccess(
    SymbolAttribution Attribution,
    IReadOnlyDictionary<string, SymbolAttribution> Members);
