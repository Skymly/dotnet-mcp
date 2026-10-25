using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Core;

public sealed partial class RoslynLanguageAdapter
{
    /// <summary>
    /// Optional ADR-0006 XAML usage index. Settable rather than a constructor
    /// dependency because the only implementation (XamlUsageIndex) itself needs
    /// this adapter — property injection avoids the DI cycle and keeps
    /// "unwired means no XAML triggers" the default for direct construction.
    /// </summary>
    public IXamlUsageSource? XamlUsageSource { get; set; }

    /// <summary>
    /// Generation triggers for a symbol already known to come from
    /// <paramref name="generator"/> (spike S9 / issue #524). Resolution order:
    /// universal partial pairing (Exact), the generator-identity rule table,
    /// XAML index evidence, then an honest empty result with a message.
    /// internal for tests: convention-rule edge cases (miss/ambiguous/unknown)
    /// are exercised directly rather than through fabricated generators.
    /// </summary>
    internal async Task<(IReadOnlyList<GenerationTrigger> Triggers, string? Message)> ResolveTriggersAsync(
        IWorkspaceSession session,
        Project project,
        ISymbol symbol,
        GeneratorIdentity generator,
        CancellationToken cancellationToken)
    {
        var pair = TryPartialPairTrigger(session, project, symbol);
        if (pair is not null)
        {
            return ([pair], null);
        }

        var rule = await TryRuleTableTriggersAsync(
                session, project, symbol, generator, cancellationToken)
            .ConfigureAwait(false);
        if (rule.Triggers.Count > 0)
        {
            return (rule.Triggers, null);
        }

        if (rule.Ambiguous)
        {
            return ([], rule.Message);
        }

        var xaml = await TryXamlTriggersAsync(session, project, symbol, cancellationToken)
            .ConfigureAwait(false);
        if (xaml.Count > 0)
        {
            return (xaml, null);
        }

        return ([], rule.Message ??
            (rule.Known ? KnownRuleMissMessage(generator, symbol) : NoTriggerRuleMessage(generator)));
    }

    private static string NoTriggerRuleMessage(GeneratorIdentity generator) =>
        $"No trigger rule known for generator '{generator.AssemblyName}::{generator.TypeFullName}'; " +
        "the generator source is the next place to look.";

    private static string KnownRuleMissMessage(GeneratorIdentity generator, ISymbol symbol) =>
        $"Generator '{generator.AssemblyName}::{generator.TypeFullName}' produced no unique trigger for " +
        $"'{symbol.ToDisplayString(SymbolDisplayFormats.SignatureQualified)}'; " +
        "the generator source is the next place to look.";

    /// <summary>
    /// The handwritten counterpart of a generated partial part
    /// (Roslyn partial-pairing APIs — methods, properties, events,
    /// constructors; C# and VB alike). Returns null when there is no
    /// counterpart or the counterpart is also generated.
    /// </summary>
    private GenerationTrigger? TryPartialPairTrigger(
        IWorkspaceSession session,
        Project project,
        ISymbol symbol)
    {
        var (counterpart, role) = symbol switch
        {
            IMethodSymbol method =>
                method.PartialDefinitionPart is { } methodDef
                    ? ((ISymbol)methodDef, "partial definition")
                    : method.PartialImplementationPart is { } methodImpl
                        ? ((ISymbol)methodImpl, "partial implementation")
                        : ((ISymbol?)null, (string?)null),
            IPropertySymbol property =>
                property.PartialDefinitionPart is { } propertyDef
                    ? ((ISymbol)propertyDef, "partial definition")
                    : property.PartialImplementationPart is { } propertyImpl
                        ? ((ISymbol)propertyImpl, "partial implementation")
                        : ((ISymbol?)null, (string?)null),
            IEventSymbol @event =>
                @event.PartialDefinitionPart is { } eventDef
                    ? ((ISymbol)eventDef, "partial definition")
                    : @event.PartialImplementationPart is { } eventImpl
                        ? ((ISymbol)eventImpl, "partial implementation")
                        : ((ISymbol?)null, (string?)null),
            _ => (null, null),
        };

        if (counterpart is null)
        {
            return null;
        }

        var location = counterpart.Locations.FirstOrDefault(static l => l.IsInSource);
        if (location?.SourceTree is not { } tree)
        {
            return null;
        }

        // A partial pair where both parts are generated gives the caller nothing
        // handwritten to edit — fall through to the rule table instead.
        if (session.Solution.GetDocument(tree) is SourceGeneratedDocument)
        {
            return null;
        }

        return ToCodeTrigger(
            GenerationTriggerKind.PartialDefinition,
            GenerationTriggerConfidence.Exact,
            project,
            counterpart,
            location.SourceSpan,
            tree,
            role);
    }

    /// <summary>
    /// Identity-keyed convention rules (spike S9 rule table). The key is
    /// (AssemblyName, TypeFullName) — generator version is display metadata.
    /// </summary>
    private Task<RuleTableResult> TryRuleTableTriggersAsync(
        IWorkspaceSession session,
        Project project,
        ISymbol symbol,
        GeneratorIdentity generator,
        CancellationToken cancellationToken) =>
        (generator.AssemblyName, generator.TypeFullName) switch
        {
            ("CommunityToolkit.Mvvm.SourceGenerators",
                "CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator") =>
                Task.FromResult(ResolveObservablePropertyTriggers(session, project, symbol, cancellationToken)),
            ("CommunityToolkit.Mvvm.SourceGenerators",
                "CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator") =>
                Task.FromResult(ResolveRelayCommandTriggers(session, project, symbol, cancellationToken)),
            ("System.Text.Json.SourceGeneration",
                "System.Text.Json.SourceGeneration.JsonSourceGenerator") =>
                Task.FromResult(ResolveJsonSerializableTriggers(project, symbol, cancellationToken)),
            _ => Task.FromResult(RuleTableResult.Unknown),
        };

    /// <summary>
    /// [ObservableProperty]: generated property <c>Name</c> or generated
    /// <c>On{P}Changing</c>/<c>On{P}Changed</c> callback definition maps back to
    /// the unique handwritten field/property carrying the attribute
    /// (S9 candidate set {camel, _camel, m_camel, m_Pascal}).
    /// </summary>
    private RuleTableResult ResolveObservablePropertyTriggers(
        IWorkspaceSession session,
        Project project,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        string? propertyName = symbol switch
        {
            IPropertySymbol property => property.Name,
            IMethodSymbol method when TryCallbackPropertyName(method.Name, out var name) => name,
            _ => null,
        };
        if (propertyName is null || symbol.ContainingType is null)
        {
            return RuleTableResult.Miss;
        }

        var camel = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
        var fieldNames = new[] { camel, "_" + camel, "m_" + camel, "m_" + propertyName };
        var carriers = symbol.ContainingType.GetMembers()
            .Where(member => member switch
            {
                IFieldSymbol field => fieldNames.Contains(field.Name, StringComparer.Ordinal),
                // [ObservableProperty] on a partial property: the property itself is the carrier.
                IPropertySymbol property => property.Name == propertyName,
                _ => false,
            })
            .Where(member => HasAttributeNamed(member, "ObservablePropertyAttribute") &&
                IsHandwritten(session, member))
            .ToList();

        if (carriers.Count == 0)
        {
            return RuleTableResult.Miss;
        }

        if (carriers.Count > 1)
        {
            return RuleTableResult.Ambiguity(
                $"Trigger candidates for '{SymbolKey(symbol)}' are ambiguous: " +
                $"{string.Join(", ", carriers.Select(SymbolKey))}.");
        }

        var trigger = ToAttributeTrigger(
            project, carriers[0], "ObservablePropertyAttribute", "ObservableProperty",
            GenerationTriggerConfidence.Convention, cancellationToken);
        return trigger is null ? RuleTableResult.Miss : RuleTableResult.Hit(trigger);
    }

    /// <summary>
    /// [RelayCommand]: generated <c>XxxCommand</c> property or <c>xxxCommand</c>
    /// backing field maps back to the unique handwritten method carrying the
    /// attribute (S9 candidate set {name, On+name, name+Async, On+name+Async}).
    /// </summary>
    private RuleTableResult ResolveRelayCommandTriggers(
        IWorkspaceSession session,
        Project project,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        const string suffix = "Command";
        if (symbol.ContainingType is null ||
            !symbol.Name.EndsWith(suffix, StringComparison.Ordinal) ||
            symbol.Name.Length == suffix.Length)
        {
            return RuleTableResult.Miss;
        }

        var stem = symbol.Name[..^suffix.Length];
        if (char.IsLower(stem[0]))
        {
            stem = char.ToUpperInvariant(stem[0]) + stem[1..];
        }

        var methodNames = new[] { stem, "On" + stem, stem + "Async", "On" + stem + "Async" };
        var carriers = symbol.ContainingType.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(method => method.MethodKind == MethodKind.Ordinary &&
                methodNames.Contains(method.Name, StringComparer.Ordinal) &&
                HasAttributeNamed(method, "RelayCommandAttribute") &&
                IsHandwritten(session, method))
            .ToList();

        if (carriers.Count == 0)
        {
            return RuleTableResult.Miss;
        }

        if (carriers.Count > 1)
        {
            return RuleTableResult.Ambiguity(
                $"Trigger candidates for '{SymbolKey(symbol)}' are ambiguous: " +
                $"{string.Join(", ", carriers.Select(SymbolKey))}.");
        }

        var trigger = ToAttributeTrigger(
            project, carriers[0], "RelayCommandAttribute", "RelayCommand",
            GenerationTriggerConfidence.Convention, cancellationToken);
        return trigger is null ? RuleTableResult.Miss : RuleTableResult.Hit(trigger);
    }

    /// <summary>
    /// [JsonSerializable] on the handwritten partial context class is the
    /// trigger for every generated member of that type (S9: the attribute
    /// syntax node itself via ApplicationSyntaxReference — Exact).
    /// </summary>
    private RuleTableResult ResolveJsonSerializableTriggers(
        Project project,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        if (symbol.ContainingType is not { } type)
        {
            return RuleTableResult.Miss;
        }

        var triggers = type.GetAttributes()
            .Where(attribute => IsAttributeNamed(attribute, "JsonSerializableAttribute"))
            .Select(attribute => ToAttributeTrigger(
                project, type, "JsonSerializableAttribute", "JsonSerializable",
                GenerationTriggerConfidence.Exact, cancellationToken))
            .OfType<GenerationTrigger>()
            .ToList();

        return triggers.Count == 0 ? RuleTableResult.Miss : RuleTableResult.Hit(triggers);
    }

    /// <summary>
    /// Evidence-gated XAML triggers (issue #524): a generated field named by an
    /// <c>x:Name</c> usage, or a generated <c>InitializeComponent</c> whose
    /// code-behind document root carries <c>x:Class</c>. Works for any
    /// generator identity — the index match is the evidence. Unwired
    /// (no IXamlUsageSource) means no XAML triggers.
    /// </summary>
    private async Task<IReadOnlyList<GenerationTrigger>> TryXamlTriggersAsync(
        IWorkspaceSession session,
        Project project,
        ISymbol symbol,
        CancellationToken cancellationToken)
    {
        if (XamlUsageSource is null)
        {
            return [];
        }

        var projectId = project.Id.Id.ToString("D");
        var usages = await XamlUsageSource
            .GetUsagesAsync(session, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (usages.Count == 0)
        {
            return [];
        }

        if (symbol is IFieldSymbol field)
        {
            var key = SymbolKey(field);
            return usages
                .Where(usage => usage.Kind == XamlUsageKind.XName &&
                    usage.ProjectId == projectId &&
                    usage.SymbolKey == key)
                .Select(usage => ToXamlTrigger(usage, $"x:Name=\"{field.Name}\""))
                .ToList();
        }

        if (symbol is IMethodSymbol { Name: "InitializeComponent" } &&
            symbol.ContainingType is { } type)
        {
            var typeKey = SymbolKey(type);
            var classPoints = usages
                .Where(usage => usage.Kind == XamlUsageKind.XClass &&
                    usage.ProjectId == projectId &&
                    usage.SymbolKey == typeKey)
                .ToList();

            var triggers = new List<GenerationTrigger>();
            foreach (var classPoint in classPoints)
            {
                // The document root element: the first element-type usage in the
                // same document, else the x:Class attribute value itself.
                var anchor = usages
                    .Where(usage => usage.Kind == XamlUsageKind.ElementType &&
                        usage.ProjectId == projectId &&
                        string.Equals(usage.FilePath, classPoint.FilePath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(usage => usage.Start)
                    .Cast<XamlUsagePoint?>()
                    .FirstOrDefault() ?? classPoint;
                triggers.Add(ToXamlTrigger(anchor, Path.GetFileName(anchor.FilePath)));
            }

            return triggers;
        }

        return [];
    }

    private GenerationTrigger ToCodeTrigger(
        string kind,
        string confidence,
        Project project,
        ISymbol declaredSymbol,
        TextSpan span,
        SyntaxTree tree,
        string? detail)
    {
        var (line, column, endLine, endColumn) = SourcePositions.FromLineSpan(
            Location.Create(tree, span).GetLineSpan());
        return new GenerationTrigger(
            kind,
            confidence,
            FormatHandle(project, declaredSymbol),
            tree.FilePath,
            span.Start,
            span.Length,
            line,
            column,
            endLine,
            endColumn,
            detail);
    }

    /// <summary>
    /// Attribute trigger on the declaration carrying
    /// <paramref name="attributeMetadataName"/>: exact attribute syntax when
    /// resolvable, else the declaration identifier span. The handle is the
    /// carrier's handle — never the unresolvable attribute symbol.
    /// </summary>
    private GenerationTrigger? ToAttributeTrigger(
        Project project,
        ISymbol carrier,
        string attributeMetadataName,
        string detail,
        string confidence,
        CancellationToken cancellationToken)
    {
        var attribute = carrier.GetAttributes()
            .FirstOrDefault(candidate => IsAttributeNamed(candidate, attributeMetadataName));
        if (attribute?.ApplicationSyntaxReference is { } syntaxRef)
        {
            var node = syntaxRef.GetSyntax(cancellationToken);
            return ToCodeTrigger(
                GenerationTriggerKind.Attribute, confidence, project, carrier,
                node.Span, node.SyntaxTree, detail);
        }

        var location = carrier.Locations.FirstOrDefault(static l => l.IsInSource);
        if (location?.SourceTree is not { } tree)
        {
            return null;
        }

        return ToCodeTrigger(
            GenerationTriggerKind.Attribute, confidence, project, carrier,
            location.SourceSpan, tree, detail);
    }

    private static GenerationTrigger ToXamlTrigger(XamlUsagePoint usage, string detail) =>
        new(
            GenerationTriggerKind.XamlElement,
            usage.Confidence == XamlUsageConfidence.Exact
                ? GenerationTriggerConfidence.Exact
                : GenerationTriggerConfidence.Convention,
            Handle: null,
            usage.FilePath,
            usage.Start,
            usage.Length,
            usage.Line,
            usage.Column,
            usage.EndLine,
            usage.EndColumn,
            detail);

    /// <summary>
    /// On{Property}Changing / On{Property}Changed generated callback definitions:
    /// the property name sits between the "On" prefix and the "Changing"/"Changed"
    /// suffix. Anything else (including OnPropertyChanged itself) is not a
    /// per-property callback and does not match.
    /// </summary>
    private static bool TryCallbackPropertyName(string methodName, out string propertyName)
    {
        propertyName = string.Empty;
        if (!methodName.StartsWith("On", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var suffix in new[] { "Changing", "Changed" })
        {
            if (methodName.Length > "On".Length + suffix.Length &&
                methodName.EndsWith(suffix, StringComparison.Ordinal))
            {
                propertyName = methodName["On".Length..^suffix.Length];
                return propertyName.Length > 0;
            }
        }

        return false;
    }

    private static bool IsAttributeNamed(AttributeData attribute, string attributeMetadataName) =>
        string.Equals(attribute.AttributeClass?.Name, attributeMetadataName, StringComparison.Ordinal);

    private static bool HasAttributeNamed(ISymbol symbol, string attributeMetadataName) =>
        symbol.GetAttributes().Any(attribute => IsAttributeNamed(attribute, attributeMetadataName));

    /// <summary>
    /// The member has at least one declaration in a handwritten (not
    /// source-generated) document of this workspace.
    /// </summary>
    private static bool IsHandwritten(IWorkspaceSession session, ISymbol symbol) =>
        symbol.DeclaringSyntaxReferences.Any(reference =>
            session.Solution.GetDocument(reference.SyntaxTree) is not SourceGeneratedDocument);

    /// <summary>Rule-table outcome: hits, an ambiguity message, a plain miss, or no rule.</summary>
    private readonly record struct RuleTableResult(
        IReadOnlyList<GenerationTrigger> Triggers,
        string? Message,
        bool Ambiguous,
        bool Known)
    {
        public static RuleTableResult Hit(GenerationTrigger trigger) =>
            new([trigger], null, Ambiguous: false, Known: true);

        public static RuleTableResult Hit(IReadOnlyList<GenerationTrigger> triggers) =>
            new(triggers, null, Ambiguous: false, Known: true);

        public static RuleTableResult Ambiguity(string message) =>
            new([], message, Ambiguous: true, Known: true);

        public static RuleTableResult Miss { get; } =
            new([], null, Ambiguous: false, Known: true);

        public static RuleTableResult Unknown { get; } =
            new([], null, Ambiguous: false, Known: false);
    }
}
