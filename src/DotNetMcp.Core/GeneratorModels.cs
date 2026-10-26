namespace DotNetMcp.Core;

public sealed record GeneratorIdentity(
    string AssemblyName,
    string TypeFullName,
    string Version);

/// <summary>
/// project_list_generators payload (issue #551): the generators Roslyn actually loaded, plus the
/// analyzer reference paths MSBuild declared but no loadable assembly materialized for — an
/// OutputItemType="Analyzer" project reference whose output is not built, or an analyzer DLL
/// missing on disk. The declared path is all that is known there; generator type identities are
/// never fabricated for an assembly that was not loaded.
/// </summary>
public sealed record GeneratorListResult(
    IReadOnlyList<GeneratorIdentity> Generators,
    IReadOnlyList<string> UnresolvedAnalyzerReferences);

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

/// <summary>
/// What produced a generated symbol — the kind of handwritten construct the
/// caller should edit (spike S9, issue #524).
/// </summary>
public static class GenerationTriggerKind
{
    /// <summary>A symbol-level partial pair: the counterpart declaration bound by the compiler.</summary>
    public const string PartialDefinition = "PartialDefinition";

    /// <summary>A declaration carrying the attribute that triggered the generator.</summary>
    public const string Attribute = "Attribute";

    /// <summary>A XAML document element (<c>x:Name</c> or the document root).</summary>
    public const string XamlElement = "XamlElement";
}

/// <summary>
/// How strongly a <see cref="GenerationTrigger"/> is verified: compiler-bound
/// symbol relationships and attribute syntax nodes are Exact; name-inverse
/// lookups with attribute disambiguation are Convention.
/// </summary>
public static class GenerationTriggerConfidence
{
    public const string Exact = "Exact";
    public const string Convention = "Convention";
}

/// <summary>
/// One handwritten location that caused a generated member to exist, in the
/// unified position model: 1-based line/column in UTF-16 code units,
/// <see cref="EndColumn"/> exclusive; <see cref="Start"/>/<see cref="Length"/>
/// are raw text offsets of the same span. <see cref="Handle"/> is present for
/// code triggers and re-resolves via symbol_summary / symbol_resolve.
/// </summary>
public sealed record GenerationTrigger(
    string Kind,
    string Confidence,
    string? Handle,
    string? Path,
    int? Start,
    int? Length,
    int? Line,
    int? Column,
    int? EndLine,
    int? EndColumn,
    string? Detail);

public sealed record SymbolAttribution(
    string DeclarationAvailability,
    string OriginKind,
    GeneratorIdentity? Generator,
    IReadOnlyList<GenerationTrigger> Triggers,
    string? TriggerMessage)
{
    /// <summary>Shorthand for an attribution with no generation triggers.</summary>
    public static SymbolAttribution WithoutTriggers(
        string declarationAvailability,
        string originKind,
        GeneratorIdentity? generator) =>
        new(declarationAvailability, originKind, generator, [], null);
}

public sealed record SymbolAttributionSuccess(
    SymbolAttribution Attribution,
    IReadOnlyDictionary<string, SymbolAttribution> Members);
