using System.Xml;
using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Xaml;

/// <summary>
/// XAML usage-point index (ADR-0006): one semantic pass over a project's
/// registered XAML <see cref="Project.AdditionalDocuments"/>, resolving each
/// usage through the same xmlns / x:DataType / x:Class / static-DataContext
/// rules the semantic diagnostics use. Usage points are cached per
/// (Epoch, ProjectId) through <see cref="IWorkspaceSessionCaches"/>; a session
/// without caches simply rebuilds the index.
/// </summary>
public sealed class XamlUsageIndex : IXamlUsageSource
{
    private const int SnippetMaxChars = 80;

    private readonly XamlDocumentService _documents;
    private readonly LanguageAdapters _languages;
    private readonly RoslynLanguageAdapter _roslyn;

    public XamlUsageIndex(
        XamlDocumentService documents,
        LanguageAdapters languages,
        RoslynLanguageAdapter roslyn)
    {
        _documents = documents;
        _languages = languages;
        _roslyn = roslyn;
    }

    public bool IsXamlDocumentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return string.Equals(extension, XamlDocumentService.AvaloniaDocumentExtension, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, XamlDocumentService.MauiDocumentExtension, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<XamlUsagePoint>> GetUsagesAsync(
        IWorkspaceSession session,
        string projectId,
        CancellationToken ct = default)
    {
        var project = XamlDocumentService.FindRoslynProject(session, projectId);
        if (project is null)
        {
            return Array.Empty<XamlUsagePoint>();
        }

        var key = project.Id.Id.ToString("D");
        var caches = session as IWorkspaceSessionCaches;
        if (caches?.XamlUsages.TryGet(session.Epoch, key, out var cached) == true)
        {
            return cached;
        }

        var usages = await BuildProjectUsagesAsync(session, project, ct).ConfigureAwait(false);
        caches?.XamlUsages.Set(session.Epoch, key, usages);
        return usages;
    }

    public async Task<XamlUsagePoint?> FindAtPositionAsync(
        IWorkspaceSession session,
        string path,
        int line,
        int column,
        string? projectId = null,
        CancellationToken ct = default)
    {
        if (!IsXamlDocumentPath(path) || line < 1 || column < 1)
        {
            return null;
        }

        XamlUsagePoint? best = null;
        foreach (var project in session.Solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (!XamlDocumentService.IsRoslynProject(project))
            {
                continue;
            }

            var id = project.Id.Id.ToString("D");
            if (!string.IsNullOrWhiteSpace(projectId) &&
                !string.Equals(id, projectId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var usage in await GetUsagesAsync(session, id, ct).ConfigureAwait(false))
            {
                if (Covers(usage, line, column) &&
                    PositionPaths.Equal(usage.FilePath, path) &&
                    (best is null || usage.Length < best.Length))
                {
                    best = usage;
                }
            }
        }

        return best;
    }

    private async Task<IReadOnlyList<XamlUsagePoint>> BuildProjectUsagesAsync(
        IWorkspaceSession session,
        Project project,
        CancellationToken ct)
    {
        var projectId = project.Id.Id.ToString("D");
        var usages = new List<XamlUsagePoint>();
        foreach (var document in project.AdditionalDocuments)
        {
            ct.ThrowIfCancellationRequested();
            var path = document.FilePath;
            if (string.IsNullOrWhiteSpace(path) || !IsXamlDocumentPath(path))
            {
                continue;
            }

            var text = await document.GetTextAsync(ct).ConfigureAwait(false);
            var (root, _) = XamlDocumentService.ParseDocumentRoot(path, text.ToString(), projectId);
            if (root is null)
            {
                continue;
            }

            await ExtractDocumentUsagesAsync(session, project, root, text, usages, ct).ConfigureAwait(false);
        }

        usages.Sort(static (a, b) =>
        {
            var byPath = string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
            return byPath != 0 ? byPath : a.Start.CompareTo(b.Start);
        });
        return usages;
    }

    private async Task ExtractDocumentUsagesAsync(
        IWorkspaceSession session,
        Project project,
        XamlDocumentService.XamlDocumentRoot root,
        SourceText text,
        List<XamlUsagePoint> usages,
        CancellationToken ct)
    {
        var (xmlns, _, _) = await _documents.MapDeclaredXmlnsAsync(
                session, root, root.XmlnsDeclarations, stopCollection: null, ct)
            .ConfigureAwait(false);

        var ctx = new ExtractContext
        {
            Session = session,
            Project = project,
            ProjectId = project.Id.Id.ToString("D"),
            Root = root,
            Text = text,
            Xmlns = xmlns ?? Array.Empty<XamlXmlnsMapping>(),
            Usages = usages,
        };

        if (!string.IsNullOrWhiteSpace(root.ClassName))
        {
            var (resolved, _) = await _documents
                .ResolveNameInDocumentProjectAsync(session, root, root.ClassName, ct)
                .ConfigureAwait(false);
            if (resolved is not null)
            {
                var (_, symbol, _) = await _roslyn
                    .ResolveHandleSymbolAsync(session, resolved.Handle, ct)
                    .ConfigureAwait(false);
                ctx.ClassType = symbol as INamedTypeSymbol;
            }
        }

        if (ctx.ClassType is not null)
        {
            var (typeName, _) = await _documents
                .TryResolveStaticDataContextTypeAsync(session, root, ct)
                .ConfigureAwait(false);
            ctx.StaticDataType = typeName;
        }

        try
        {
            using var reader = XamlDocumentService.CreateReader(root.Text);
            var lineInfo = (IXmlLineInfo)reader;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    await EmitElementNameAsync(ctx, reader, lineInfo, ct).ConfigureAwait(false);
                    if (ctx.DataTypeStack.Count > 0)
                    {
                        ctx.CurrentDataType = ctx.DataTypeStack.Pop();
                    }

                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                var dataType = reader.GetAttribute("DataType", XamlXmlns.Xaml);
                var effectiveDataType = !string.IsNullOrWhiteSpace(dataType)
                    ? dataType.Trim()
                    : ctx.CurrentDataType;
                if (!reader.IsEmptyElement)
                {
                    ctx.DataTypeStack.Push(ctx.CurrentDataType);
                    ctx.CurrentDataType = effectiveDataType;
                }

                var elementType = await EmitElementNameAsync(ctx, reader, lineInfo, ct).ConfigureAwait(false);
                if (reader.HasAttributes && reader.MoveToFirstAttribute())
                {
                    do
                    {
                        await EmitAttributeUsagesAsync(ctx, reader, lineInfo, elementType, effectiveDataType, ct)
                            .ConfigureAwait(false);
                    } while (reader.MoveToNextAttribute());
                    reader.MoveToElement();
                }
            }
        }
        catch (XmlException)
        {
            // ParseDocumentRoot already rejected malformed XML.
        }
    }

    /// <summary>
    /// Emit the ElementType usage for an element or end-tag name (or the owner
    /// part of a property element like <c>&lt;Grid.Row&gt;</c>). Returns the
    /// resolved element type so attribute lookups can reuse it.
    /// </summary>
    private async Task<INamedTypeSymbol?> EmitElementNameAsync(
        ExtractContext ctx,
        XmlReader reader,
        IXmlLineInfo lineInfo,
        CancellationToken ct)
    {
        var start = NameOffset(ctx.Text, lineInfo);
        if (start < 0)
        {
            return null;
        }

        var prefix = reader.Prefix;
        var local = reader.LocalName;
        if (XamlDocumentService.IsPropertyElementName(local))
        {
            var ownerLocal = local.Split('.', 2)[0];
            var ownerType = await ResolveElementTypeCachedAsync(ctx, prefix, ownerLocal, ct).ConfigureAwait(false);
            if (ownerType is not null)
            {
                var ownerLength = prefix.Length == 0
                    ? ownerLocal.Length
                    : prefix.Length + 1 + ownerLocal.Length;
                Emit(ctx, start, ownerLength, XamlUsageKind.ElementType,
                    XamlUsageConfidence.Exact, ownerType, ctx.Project);
            }

            return null;
        }

        var elementType = await ResolveElementTypeCachedAsync(ctx, prefix, local, ct).ConfigureAwait(false);
        if (elementType is not null)
        {
            Emit(ctx, start, reader.Name.Length, XamlUsageKind.ElementType,
                XamlUsageConfidence.Exact, elementType, ctx.Project);
        }

        return elementType;
    }

    private async Task<INamedTypeSymbol?> ResolveElementTypeCachedAsync(
        ExtractContext ctx,
        string prefix,
        string local,
        CancellationToken ct)
    {
        if (ctx.ElementTypes.TryGetValue((prefix, local), out var cached))
        {
            return cached;
        }

        var type = await _documents
            .ResolveElementTypeAsync(ctx.Session, prefix, local, ctx.Xmlns, ctx.ProjectId, ct)
            .ConfigureAwait(false);
        ctx.ElementTypes[(prefix, local)] = type;
        return type;
    }

    private async Task EmitAttributeUsagesAsync(
        ExtractContext ctx,
        XmlReader reader,
        IXmlLineInfo lineInfo,
        INamedTypeSymbol? elementType,
        string? effectiveDataType,
        CancellationToken ct)
    {
        var name = reader.Name;
        if (name.StartsWith("xmlns", StringComparison.Ordinal))
        {
            return;
        }

        var namespaceUri = reader.NamespaceURI;
        var local = reader.LocalName;
        var nameOffset = NameOffset(ctx.Text, lineInfo);
        if (nameOffset < 0)
        {
            return;
        }

        if (XamlDocumentService.IsXamlNamespace(namespaceUri))
        {
            if (!TryGetAttributeValueSpan(ctx.Text, nameOffset, name, out var xValueSpan))
            {
                return;
            }

            var rawValue = ctx.Text.ToString(xValueSpan);
            switch (local)
            {
                case "Class" when ctx.ClassType is not null:
                    if (TryFindToken(rawValue, reader.Value.Trim(), out var classToken))
                    {
                        Emit(ctx, xValueSpan.Start + classToken.Offset, classToken.Value.Length,
                            XamlUsageKind.XClass, XamlUsageConfidence.Exact, ctx.ClassType, ctx.Project);
                    }

                    break;
                case "DataType":
                    if (TryExtractTypeToken(rawValue, out var dataToken))
                    {
                        var (type, _) = await ResolveTypeTokenAsync(ctx, dataToken.Value, ct)
                            .ConfigureAwait(false);
                        if (type is not null)
                        {
                            Emit(ctx, xValueSpan.Start + dataToken.Offset, dataToken.Value.Length,
                                XamlUsageKind.XDataType, XamlUsageConfidence.Exact, type, ctx.Project);
                        }
                    }

                    break;
                case "Name" when ctx.ClassType is not null:
                    var nameValue = reader.Value.Trim();
                    if (nameValue.Length > 0 && TryFindToken(rawValue, nameValue, out var nameToken))
                    {
                        var (lookup, _) = _roslyn.LookupTypeMember(
                            ctx.Project, ctx.ClassType, nameValue, publicOnly: false);
                        if (lookup?.Member is IFieldSymbol field)
                        {
                            Emit(ctx, xValueSpan.Start + nameToken.Offset, nameToken.Value.Length,
                                XamlUsageKind.XName, XamlUsageConfidence.Exact, field, ctx.Project);
                        }
                    }

                    break;
            }

            return;
        }

        if (string.Equals(reader.Prefix, "xml", StringComparison.Ordinal) || namespaceUri.Length > 0)
        {
            return;
        }

        var attached = XamlDocumentService.IsAttachedPropertyName(local, name);
        if (!attached && elementType is not null)
        {
            var (lookup, _) = _roslyn.LookupTypeMember(ctx.Project, elementType, local, publicOnly: true);
            var member = lookup?.Member ?? (ISymbol?)FindElementEvent(elementType, local);
            if (member is not null)
            {
                Emit(ctx, nameOffset, name.Length, XamlUsageKind.PropertyAttribute,
                    XamlUsageConfidence.Exact, member, ctx.Project);
                if (member is IEventSymbol && ctx.ClassType is not null &&
                    TryGetAttributeValueSpan(ctx.Text, nameOffset, name, out var handlerSpan))
                {
                    var handler = FindHandlerMethod(ctx.ClassType, ctx.Text.ToString(handlerSpan).Trim());
                    if (handler is not null)
                    {
                        Emit(ctx, handlerSpan.Start, handlerSpan.Length, XamlUsageKind.EventHandler,
                            XamlUsageConfidence.Exact, handler, ctx.Project);
                    }
                }
            }
        }

        if (TryGetAttributeValueSpan(ctx.Text, nameOffset, name, out var valueSpan))
        {
            await EmitValueMarkupsAsync(ctx, valueSpan, effectiveDataType, ct).ConfigureAwait(false);
        }
    }

    private async Task EmitValueMarkupsAsync(
        ExtractContext ctx,
        TextSpan valueSpan,
        string? effectiveDataType,
        CancellationToken ct)
    {
        var value = ctx.Text.ToString(valueSpan);
        if (value.Length == 0 || value[0] != '{')
        {
            return;
        }

        if (TryExtractBinding(value, out var binding) && binding is not null)
        {
            await EmitBindingAsync(ctx, valueSpan, binding, effectiveDataType, ct).ConfigureAwait(false);
            return;
        }

        if (TryExtractMarkupToken(value, "x:Static", out var staticToken, out var staticOffset))
        {
            var typePart = StaticTypePart(staticToken);
            if (typePart.Length > 0)
            {
                var (type, _) = await ResolveTypeTokenAsync(ctx, typePart, ct).ConfigureAwait(false);
                if (type is not null)
                {
                    Emit(ctx, valueSpan.Start + staticOffset, typePart.Length,
                        XamlUsageKind.MarkupTypeRef, XamlUsageConfidence.Exact, type, ctx.Project);
                }
            }

            return;
        }

        if (TryExtractMarkupToken(value, "x:Type", out var typeToken, out var typeOffset))
        {
            var (type, _) = await ResolveTypeTokenAsync(ctx, typeToken, ct).ConfigureAwait(false);
            if (type is not null)
            {
                Emit(ctx, valueSpan.Start + typeOffset, typeToken.Length,
                    XamlUsageKind.MarkupTypeRef, XamlUsageConfidence.Exact, type, ctx.Project);
            }
        }
    }

    private async Task EmitBindingAsync(
        ExtractContext ctx,
        TextSpan valueSpan,
        ExtractedBinding binding,
        string? effectiveDataType,
        CancellationToken ct)
    {
        ITypeSymbol? contextType = null;
        var contextProject = ctx.Project;
        if (!binding.ContextFree)
        {
            var (type, project) = effectiveDataType is not null
                ? await ResolveTypeTokenAsync(ctx, effectiveDataType, ct).ConfigureAwait(false)
                : await ResolveClrTypeNameAsync(ctx, ctx.StaticDataType, ct).ConfigureAwait(false);
            contextType = type;
            contextProject = project ?? ctx.Project;
        }

        if (binding.Path.Length == 0 || binding.Path == ".")
        {
            if (contextType is not null)
            {
                Emit(ctx, valueSpan.Start, valueSpan.Length, XamlUsageKind.BindingSegment,
                    XamlUsageConfidence.Exact, contextType, ctx.Project);
            }

            return;
        }

        var segments = SplitPath(binding.Path);
        if (segments is null)
        {
            return;
        }

        var walkType = contextType;
        var walkProject = contextProject;
        foreach (var (segment, offset) in segments)
        {
            var spanStart = valueSpan.Start + binding.PathOffset + offset;
            if (walkType is not null)
            {
                var (lookup, _) = _roslyn.LookupTypeMember(walkProject, walkType, segment, publicOnly: true);
                if (lookup?.Member is not (IPropertySymbol or IFieldSymbol))
                {
                    break;
                }

                Emit(ctx, spanStart, segment.Length, XamlUsageKind.BindingSegment,
                    XamlUsageConfidence.Exact, lookup.Member, lookup.Project);
                walkType = lookup.MemberType;
                walkProject = lookup.Project;
                continue;
            }

            var (resolved, _) = await _languages
                .ResolveByNameAsync(ctx.Session, segment, ctx.ProjectId, ct)
                .ConfigureAwait(false);
            if (resolved is null)
            {
                continue;
            }

            var (_, symbol, _) = await _roslyn
                .ResolveHandleSymbolAsync(ctx.Session, resolved.Handle, ct)
                .ConfigureAwait(false);
            if (symbol is not null && symbol is not INamedTypeSymbol &&
                symbol.ContainingSymbol is INamedTypeSymbol)
            {
                Emit(ctx, spanStart, segment.Length, XamlUsageKind.BindingSegment,
                    XamlUsageConfidence.Unverified, symbol, ctx.Project);
            }
        }
    }

    private async Task<(ITypeSymbol? Type, Project? Project)> ResolveTypeTokenAsync(
        ExtractContext ctx,
        string token,
        CancellationToken ct)
    {
        var clrName = XamlDocumentService.ResolveTypeName(token.Trim(), ctx.Xmlns);
        return await ResolveClrTypeNameAsync(ctx, clrName, ct).ConfigureAwait(false);
    }

    private async Task<(ITypeSymbol? Type, Project? Project)> ResolveClrTypeNameAsync(
        ExtractContext ctx,
        string? clrName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clrName))
        {
            return (null, null);
        }

        var (resolved, _) = await _documents
            .ResolveNameInDocumentProjectAsync(ctx.Session, ctx.Root, clrName.Trim(), ct)
            .ConfigureAwait(false);
        if (resolved is null)
        {
            return (null, null);
        }

        var (project, symbol, _) = await _roslyn
            .ResolveHandleSymbolAsync(ctx.Session, resolved.Handle, ct)
            .ConfigureAwait(false);
        return (symbol as ITypeSymbol, project);
    }

    private void Emit(
        ExtractContext ctx,
        int start,
        int length,
        XamlUsageKind kind,
        XamlUsageConfidence confidence,
        ISymbol symbol,
        Project fallbackProject)
    {
        var normalized = RoslynLanguageAdapter.NormalizePositionSymbol(symbol);
        if (normalized is not INamedTypeSymbol && normalized.ContainingSymbol is not INamedTypeSymbol)
        {
            return;
        }

        var span = new TextSpan(start, length);
        var (line, column, endLine, endColumn) = SourcePositions.FromLineSpan(
            new FileLinePositionSpan(ctx.Root.Path, ctx.Text.Lines.GetLinePositionSpan(span)));
        var owner = ProjectForSymbol(ctx.Session, normalized, fallbackProject);
        ctx.Usages.Add(new XamlUsagePoint(
            ctx.Root.Path,
            start,
            length,
            line,
            column,
            endLine,
            endColumn,
            ctx.ProjectId,
            kind,
            confidence,
            normalized.ToDisplayString(SymbolDisplayFormats.SignatureQualified),
            _roslyn.FormatHandle(owner, normalized),
            Snippet(ctx.Text, span)));
    }

    /// <summary>
    /// The project <c>symbol_resolve</c> would report: the project containing
    /// the symbol's source declaration, else the searching project (metadata
    /// symbols resolve identically from any referencing project).
    /// </summary>
    private static Project ProjectForSymbol(IWorkspaceSession session, ISymbol symbol, Project fallback)
    {
        foreach (var tree in symbol.DeclaringSyntaxReferences.Select(r => r.SyntaxTree))
        {
            var document = session.Solution.GetDocument(tree);
            if (document is not null)
            {
                return document.Project;
            }
        }

        return fallback;
    }

    private static bool Covers(XamlUsagePoint point, int line, int column) =>
        (line > point.Line || (line == point.Line && column >= point.Column)) &&
        (line < point.EndLine || (line == point.EndLine && column < point.EndColumn));

    private static int NameOffset(SourceText text, IXmlLineInfo? lineInfo) =>
        lineInfo is not null && lineInfo.HasLineInfo()
            ? text.Lines.GetPosition(new LinePosition(lineInfo.LineNumber - 1, lineInfo.LinePosition - 1))
            : -1;

    /// <summary>
    /// Raw-text value span of the attribute whose name starts at
    /// <paramref name="nameOffset"/>: skips <c>=</c> and the quote, ends before
    /// the matching quote so entity-encoded content keeps raw offsets.
    /// </summary>
    private static bool TryGetAttributeValueSpan(
        SourceText text,
        int nameOffset,
        string name,
        out TextSpan valueSpan)
    {
        valueSpan = default;
        var raw = text.ToString();
        var i = nameOffset + name.Length;
        while (i < raw.Length && char.IsWhiteSpace(raw[i]))
        {
            i++;
        }

        if (i >= raw.Length || raw[i] != '=')
        {
            return false;
        }

        i++;
        while (i < raw.Length && char.IsWhiteSpace(raw[i]))
        {
            i++;
        }

        if (i >= raw.Length || (raw[i] != '"' && raw[i] != '\''))
        {
            return false;
        }

        var end = raw.IndexOf(raw[i], i + 1);
        if (end < 0)
        {
            return false;
        }

        valueSpan = new TextSpan(i + 1, end - i - 1);
        return true;
    }

    /// <summary>Locate a decoded token inside the raw attribute value.</summary>
    private static bool TryFindToken(string rawValue, string token, out (string Value, int Offset) found)
    {
        var offset = token.Length > 0 ? rawValue.IndexOf(token, StringComparison.Ordinal) : -1;
        found = offset >= 0 ? (token, offset) : default;
        return offset >= 0;
    }

    /// <summary>A type token is plain (<c>local:Customer</c>) or markup (<c>{x:Type local:Customer}</c>).</summary>
    private static bool TryExtractTypeToken(string rawValue, out (string Value, int Offset) token)
    {
        var trimmed = rawValue.Trim();
        token = default;
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (TryExtractMarkupToken(trimmed, "x:Type", out var markup, out var markupOffset))
        {
            token = (markup, rawValue.IndexOf('{', StringComparison.Ordinal) + markupOffset);
            return true;
        }

        token = (trimmed, rawValue.IndexOf(trimmed, StringComparison.Ordinal));
        return true;
    }

    private static bool TryExtractMarkupToken(
        string value,
        string keyword,
        out string token,
        out int offset)
    {
        token = string.Empty;
        offset = 0;
        if (!value.StartsWith("{" + keyword, StringComparison.Ordinal))
        {
            return false;
        }

        var i = keyword.Length + 1;
        while (i < value.Length && value[i] is ' ' or '\t')
        {
            i++;
        }

        var end = i;
        while (end < value.Length && value[end] is not (' ' or '\t' or '}'))
        {
            end++;
        }

        if (end == i)
        {
            return false;
        }

        token = value[i..end];
        offset = i;
        return true;
    }

    /// <summary><c>{x:Static local:Constants.Foo}</c> → the type part <c>local:Constants</c>.</summary>
    private static string StaticTypePart(string token)
    {
        var colon = token.IndexOf(':');
        var searchFrom = colon < 0 ? 0 : colon + 1;
        var lastDot = token.LastIndexOf('.', token.Length - 1, token.Length - searchFrom);
        return lastDot <= searchFrom ? token : token[..lastDot];
    }

    private static bool TryExtractBinding(string value, out ExtractedBinding? binding)
    {
        binding = null;
        if (value.Length < 2 || value[0] != '{')
        {
            return false;
        }

        var i = 1;
        while (i < value.Length && char.IsLetter(value[i]))
        {
            i++;
        }

        if (value[1..i] is not ("Binding" or "CompiledBinding"))
        {
            return false;
        }

        var contextFree =
            value.Contains("ElementName=", StringComparison.Ordinal) ||
            value.Contains("RelativeSource=", StringComparison.Ordinal) ||
            value.Contains("$parent", StringComparison.Ordinal);

        while (i < value.Length && value[i] is ' ' or '\t')
        {
            i++;
        }

        string? path = null;
        int pathOffset;
        var close = value.IndexOf('}', i);
        var limit = close < 0 ? value.Length : close;
        var pathEq = value.IndexOf("Path=", i, StringComparison.Ordinal);
        if (pathEq >= 0 && pathEq < limit)
        {
            pathOffset = pathEq + "Path=".Length;
        }
        else if (i < limit)
        {
            pathOffset = i;
        }
        else
        {
            path = string.Empty;
            pathOffset = i;
        }

        if (path is null)
        {
            var end = pathOffset;
            while (end < limit && value[end] is not (' ' or '\t' or ','))
            {
                end++;
            }

            path = value[pathOffset..end];
        }

        binding = new ExtractedBinding(path, pathOffset, contextFree);
        return true;
    }

    /// <summary>Per-segment (name, offset-in-path) pairs, or null when the path is not a plain member path.</summary>
    private static List<(string Segment, int Offset)>? SplitPath(string path)
    {
        var segments = new List<(string Segment, int Offset)>();
        var i = 0;
        while (i < path.Length)
        {
            var end = path.IndexOf('.', i);
            if (end < 0)
            {
                end = path.Length;
            }

            var segment = path[i..end];
            if (!IsIdentifier(segment))
            {
                return null;
            }

            segments.Add((segment, i));
            if (end >= path.Length)
            {
                break;
            }

            i = end + 1;
        }

        return segments;
    }

    private static bool IsIdentifier(string segment) =>
        segment.Length > 0 &&
        (char.IsLetter(segment[0]) || segment[0] == '_') &&
        segment.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_');

    private static IEventSymbol? FindElementEvent(ITypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var found = current.GetMembers(name)
                .OfType<IEventSymbol>()
                .FirstOrDefault(e => !e.IsStatic && e.DeclaredAccessibility == Accessibility.Public);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static IMethodSymbol? FindHandlerMethod(ITypeSymbol classType, string name)
    {
        for (var current = classType; current is not null; current = current.BaseType)
        {
            var found = current.GetMembers(name)
                .OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string Snippet(SourceText text, TextSpan span)
    {
        var line = text.Lines.GetLineFromPosition(span.Start);
        var raw = line.ToString();
        if (raw.Length <= SnippetMaxChars)
        {
            return raw.Trim();
        }

        var startInLine = span.Start - line.Start;
        var window = Math.Max(0, Math.Min(startInLine - 20, line.Span.Length - SnippetMaxChars));
        return raw.Substring(window, SnippetMaxChars).Trim();
    }

    private sealed class ExtractContext
    {
        public required IWorkspaceSession Session { get; init; }
        public required Project Project { get; init; }
        public required string ProjectId { get; init; }
        public required XamlDocumentService.XamlDocumentRoot Root { get; init; }
        public required SourceText Text { get; init; }
        public required IReadOnlyList<XamlXmlnsMapping> Xmlns { get; init; }
        public required List<XamlUsagePoint> Usages { get; init; }
        public INamedTypeSymbol? ClassType { get; set; }
        public string? StaticDataType { get; set; }
        public string? CurrentDataType { get; set; }
        public Stack<string?> DataTypeStack { get; } = new();
        public Dictionary<(string Prefix, string Local), INamedTypeSymbol?> ElementTypes { get; } = new();
    }

    private sealed record ExtractedBinding(string Path, int PathOffset, bool ContextFree);
}
