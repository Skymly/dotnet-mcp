namespace DotNetMcp.Core;

public sealed record GeneratorIdentity(
    string AssemblyName,
    string TypeFullName,
    string Version);

public sealed record GeneratedSourceItem(string HintName, string Content);

public sealed record GeneratorRunSources(
    GeneratorIdentity Identity,
    IReadOnlyList<GeneratedSourceItem> Sources);

public sealed record GeneratedSourceMatch(
    GeneratorIdentity Identity,
    string HintName,
    string Content,
    Microsoft.CodeAnalysis.SyntaxTree SyntaxTree);

public sealed record DriverRunSnapshot(
    IReadOnlyList<GeneratorRunSources> ByGenerator,
    IReadOnlyList<GeneratedSourceMatch> FlatSources);

public sealed record SymbolAttribution(
    string DeclarationAvailability,
    string OriginKind,
    GeneratorIdentity? Generator);

public sealed record SymbolAttributionSuccess(
    SymbolAttribution Attribution,
    IReadOnlyDictionary<string, SymbolAttribution> Members);
