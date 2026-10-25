namespace DotNetMcp.Core;

/// <summary>
/// Which XAML construct produced a usage point (ADR-0006).
/// </summary>
public enum XamlUsageKind
{
    /// <summary><c>x:Class</c> value → the code-behind type.</summary>
    XClass,

    /// <summary>Element start/end tag name, or a property-element owner prefix (<c>Grid</c> in <c>&lt;Grid.Row&gt;</c>).</summary>
    ElementType,

    /// <summary>Attribute name resolving to a property/event on the element's CLR type (never attached properties).</summary>
    PropertyAttribute,

    /// <summary><c>x:DataType</c> value → the design-time type.</summary>
    XDataType,

    /// <summary>Type part of <c>{x:Type}</c> / <c>{x:Static}</c> markup.</summary>
    MarkupTypeRef,

    /// <summary>One segment of a <c>{Binding}</c> / <c>{CompiledBinding}</c> path (positional or <c>Path=</c>).</summary>
    BindingSegment,

    /// <summary><c>x:Name</c> value → the name-generator field on the <c>x:Class</c> type.</summary>
    XName,

    /// <summary>Event attribute value → the code-behind handler method.</summary>
    EventHandler,
}

/// <summary>
/// How strongly the index trusts a usage point's target (ADR-0006).
/// </summary>
public enum XamlUsageConfidence
{
    /// <summary>Resolved through <c>x:DataType</c>, <c>x:Class</c>, xmlns, or the static-DataContext rules.</summary>
    Exact,

    /// <summary>Name-matched only; the type context was unavailable (no DataType, ElementName / RelativeSource / $parent).</summary>
    Unverified,
}

/// <summary>
/// One symbol usage inside a registered XAML document (ADR-0006), in the
/// unified position model: 1-based line/column in UTF-16 code units,
/// <see cref="EndColumn"/> exclusive; <see cref="Start"/>/<see cref="Length"/>
/// are raw text offsets of the same span.
/// </summary>
public sealed record XamlUsagePoint(
    string FilePath,
    int Start,
    int Length,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string ProjectId,
    XamlUsageKind Kind,
    XamlUsageConfidence Confidence,
    string SymbolKey,
    string Handle,
    string Snippet);

/// <summary>
/// Core seam (ADR-0006): the XAML usage-point index as a Core-owned abstraction.
/// DotNetMcp.Xaml implements it; DotNetMcp.Server wires it. When unwired, Core
/// behaves exactly as before — no Core module takes a required dependency here.
/// </summary>
public interface IXamlUsageSource
{
    /// <summary>
    /// All usage points in the XAML AdditionalDocuments of one project.
    /// Cached per (Epoch, ProjectId) when the session carries
    /// <see cref="IWorkspaceSessionCaches"/>; sessions without caches still
    /// work — the index just rebuilds.
    /// </summary>
    Task<IReadOnlyList<XamlUsagePoint>> GetUsagesAsync(
        IWorkspaceSession session,
        string projectId,
        CancellationToken ct = default);

    /// <summary>
    /// The usage point covering a 1-based (line, column) position in a XAML
    /// document, or null when the position is not inside any usage span.
    /// </summary>
    Task<XamlUsagePoint?> FindAtPositionAsync(
        IWorkspaceSession session,
        string path,
        int line,
        int column,
        string? projectId = null,
        CancellationToken ct = default);

    /// <summary>True for paths shaped like registered XAML documents (.axaml, .xaml).</summary>
    bool IsXamlDocumentPath(string path);
}
