using System.Diagnostics;
using System.Xml;
using DotNetMcp.Core;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Xaml;

/// <summary>
/// Registered-framework XAML queries: Avalonia (.axaml) and MAUI (.xaml + MAUI xmlns).
/// </summary>
public sealed class XamlDocumentService
{
    public const string AvaloniaDocumentExtension = ".axaml";
    public const string MauiDocumentExtension = ".xaml";

    private readonly LanguageAdapters _languages;
    private readonly RoslynLanguageAdapter _roslyn;
    private readonly SoftBudgetOptions _softBudgets;

    public XamlDocumentService(
        LanguageAdapters languages,
        RoslynLanguageAdapter roslyn,
        SoftBudgetOptions? softBudgets = null)
    {
        _languages = languages;
        _roslyn = roslyn;
        _softBudgets = softBudgets ?? SoftBudgetOptions.Default;
    }

    public async Task<(SymbolResolveSuccess? Success, XamlQueryError? XamlError, SymbolQueryError? SymbolError)>
        ResolveClassAsync(
            IWorkspaceSession session,
            string path,
            CancellationToken cancellationToken = default)
    {
        var (root, xamlError) = await ReadDocumentAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (xamlError is not null)
        {
            return (null, xamlError, null);
        }

        if (string.IsNullOrWhiteSpace(root!.ClassName))
        {
            return (null, MissingClassError(), null);
        }

        var (success, symbolError) = await _languages
            .ResolveByNameAsync(session, root.ClassName, projectId: null, cancellationToken)
            .ConfigureAwait(false);
        return (success, null, symbolError);
    }

    public async Task<(SymbolResolveSuccess? Success, XamlQueryError? XamlError, SymbolQueryError? SymbolError)>
        ResolveNameAsync(
            IWorkspaceSession session,
            string path,
            string name,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, new MissingXamlNameError(
                "x:Name is empty.",
                "Pass an x:Name from the XAML document to xaml_resolve_name."), null);
        }

        var (root, docError) = await ReadDocumentAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (docError is not null)
        {
            return (null, docError, null);
        }

        var names = CollectXNames(root!.Text);
        if (!names.Contains(name.Trim()))
        {
            return (null, new MissingXamlNameError(
                $"No x:Name '{name}' is declared in the XAML document.",
                "Inspect the XAML document for x:Name values, then retry xaml_resolve_name."), null);
        }

        if (string.IsNullOrWhiteSpace(root!.ClassName))
        {
            return (null, MissingClassError(), null);
        }

        var (resolved, symbolError) = await _languages
            .ResolveByNameAsync(session, root.ClassName, projectId: null, cancellationToken)
            .ConfigureAwait(false);
        if (symbolError is not null)
        {
            return (null, null, symbolError);
        }

        var (lookup, lookupError) = await _roslyn
            .LookupTypeMemberAsync(session, resolved!.Handle, name.Trim(), publicOnly: false, cancellationToken)
            .ConfigureAwait(false);
        if (lookupError is not null)
        {
            return (null, new NameGeneratorNotRunError(
                $"x:Name '{name}' was found in the document but no matching field exists on '{root.ClassName}'.",
                "Ensure the XAML name generator has run (build the project), then retry xaml_resolve_name."), null);
        }

        if (lookup!.Member.Kind != Microsoft.CodeAnalysis.SymbolKind.Field)
        {
            return (null, new NameGeneratorNotRunError(
                $"x:Name '{name}' resolved to a non-field member; NameGenerator fields were not found.",
                "Ensure the XAML name generator has run (build the project), then retry xaml_resolve_name."), null);
        }

        var handle = _roslyn.FormatHandle(lookup.Project, lookup.Member);
        var success = await _languages.GetSummaryAsync(session, handle, cancellationToken).ConfigureAwait(false);
        return (success.Success, null, success.Error);
    }

    public async Task<(IReadOnlyList<XamlBindingSegment>? Success, XamlQueryError? XamlError, SymbolQueryError? SymbolError)>
        ResolveBindingAsync(
            IWorkspaceSession session,
            string path,
            string bindingPath,
            string? dataType = null,
            CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bindingPath))
        {
            return (null, new BindingPropertyNotFoundError(
                "Binding path is empty.",
                "Pass a Binding Path such as Name or Home.City."), null);
        }

        var (root, docError) = await ReadDocumentAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (docError is not null)
        {
            return (null, docError, null);
        }

        var (xmlns, xmlnsError) = await ListXmlnsAsync(session, path, prefix: null, cancellationToken)
            .ConfigureAwait(false);
        if (xmlnsError is not null)
        {
            return (null, xmlnsError, null);
        }

        var typeName = dataType;
        if (string.IsNullOrWhiteSpace(typeName))
        {
            typeName = FindFirstDataType(root!.Text);
        }

        if (string.IsNullOrWhiteSpace(typeName))
        {
            var (fromContext, contextError) = await TryResolveStaticDataContextTypeAsync(
                session, root!, cancellationToken).ConfigureAwait(false);
            if (contextError is not null)
            {
                return (null, null, contextError);
            }

            if (fromContext is null)
            {
                return (null, new MissingDataTypeError(
                    "No x:DataType was found, and code-behind has no static DataContext type.",
                    "Set x:DataType, or declare DataContext as a typed field/property / `DataContext = new Foo()` in the constructor."), null);
            }

            typeName = fromContext;
        }

        var resolvedTypeName = ResolveTypeName(typeName.Trim(), xmlns!);
        var (resolved, symbolError) = await _languages
            .ResolveByNameAsync(session, resolvedTypeName, projectId: null, cancellationToken)
            .ConfigureAwait(false);
        if (symbolError is not null)
        {
            return (null, null, symbolError);
        }

        var (startProject, startSymbol, startError) = await _roslyn
            .ResolveHandleSymbolAsync(session, resolved!.Handle, cancellationToken)
            .ConfigureAwait(false);
        if (startError is not null)
        {
            return (null, null, startError);
        }

        if (startSymbol is not ITypeSymbol currentType)
        {
            return (null, new BindingTypeMismatchError(
                $"x:DataType '{resolvedTypeName}' is not a type.",
                "Point x:DataType at a ViewModel/type, then retry xaml_resolve_binding."), null);
        }

        var segments = new List<XamlBindingSegment>();
        ITypeSymbol walkType = currentType;
        Project walkProject = startProject!;
        foreach (var segment in bindingPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (lookup, lookupError) = _roslyn.LookupTypeMember(walkProject, walkType, segment, publicOnly: true);
            if (lookupError is not null)
            {
                if (HasOrdinaryMethod(walkType, segment))
                {
                    return (null, new BindingTypeMismatchError(
                        $"Binding path segment '{segment}' on '{walkType.ToDisplayString()}' is a method, not a property or field.",
                        "Bind to a public instance property or field, not a method."), null);
                }

                return (null, new BindingPropertyNotFoundError(
                    $"Binding path segment '{segment}' was not found on '{walkType.ToDisplayString()}'.",
                    "Check the Binding Path against the x:DataType public instance properties."), null);
            }

            if (lookup!.Member.Kind is not SymbolKind.Property and not SymbolKind.Field)
            {
                return (null, new BindingTypeMismatchError(
                    $"Binding path segment '{segment}' is not a property or field.",
                    "Bind to a public instance property or field."), null);
            }

            var handle = _roslyn.FormatHandle(lookup.Project, lookup.Member);
            var (summary, summaryError) = await _languages.GetSummaryAsync(session, handle, cancellationToken)
                .ConfigureAwait(false);
            if (summaryError is not null)
            {
                return (null, null, summaryError);
            }

            segments.Add(new XamlBindingSegment(segment, handle, summary!.Summary));
            walkType = lookup.MemberType;
            walkProject = lookup.Project;
        }

        return (segments, null, null);
    }

    public async Task<(PagedResult<DiagnosticItem>? Success, XamlQueryError? Error, SymbolQueryError? SymbolError)>
        GetDiagnosticsAsync(
            IWorkspaceSession session,
            string path,
            int? limit = null,
            string? cursor = null,
            TimeSpan? softBudget = null,
            CancellationToken cancellationToken = default)
    {
        var (root, docError) = await ReadDocumentAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (docError is not null)
        {
            return (null, docError, null);
        }

        var epoch = session.Epoch;
        var pageLimit = limit is null or <= 0 ? 50 : Math.Min(limit.Value, 100);
        if (!SoftBudgetPage.TryReadOffset(cursor, epoch, "xaml_diagnostics", out _, out var cursorError))
        {
            return (null, null, cursorError);
        }

        var (xmlns, xmlnsError) = await ListXmlnsAsync(session, path, prefix: null, cancellationToken)
            .ConfigureAwait(false);
        if (xmlnsError is not null)
        {
            return (null, xmlnsError, null);
        }

        var budget = softBudget ?? _softBudgets.SingleProjectCompile;
        var clock = Stopwatch.StartNew();
        var all = await CollectSemanticDiagnosticsAsync(
                session, path, root!, xmlns!, clock, budget, cancellationToken)
            .ConfigureAwait(false);

        var (page, pageError) = SoftBudgetPage.Page(
            all,
            epoch,
            budgetHit: clock.Elapsed >= budget && budget >= TimeSpan.Zero,
            cursor,
            pageLimit,
            "xaml_diagnostics",
            "No semantic XAML diagnostics.",
            "XAML diagnostic page complete.",
            "the diagnostic list");
        return (page, null, pageError);
    }

    public async Task<(IReadOnlyList<XamlXmlnsMapping>? Success, XamlQueryError? Error)> ListXmlnsAsync(
        IWorkspaceSession session,
        string path,
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        var (root, error) = await ReadDocumentAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var declarations = root!.XmlnsDeclarations;
        if (prefix is not null)
        {
            declarations = declarations
                .Where(d => string.Equals(d.Prefix, prefix, StringComparison.Ordinal))
                .ToArray();
            if (declarations.Count == 0)
            {
                return (null, new UnknownXmlnsPrefixError(
                    $"No xmlns prefix '{prefix}' is declared on the XAML document.",
                    "Call xaml_list_xmlns without prefix to list declared prefixes, then retry with one of those."));
            }
        }

        var defaultAssembly = await ResolveDefaultAssemblyNameAsync(session, root.ClassName, cancellationToken)
            .ConfigureAwait(false);
        var definitions = await CollectXmlnsDefinitionsAsync(session, cancellationToken).ConfigureAwait(false);

        var mappings = new List<XamlXmlnsMapping>();
        foreach (var declaration in declarations)
        {
            mappings.AddRange(ResolveDeclaration(declaration.Prefix, declaration.XmlNamespace, defaultAssembly, definitions));
        }

        return (mappings, null);
    }

    public async Task<(string? ClassName, XamlQueryError? Error)> ReadClassName(
        IWorkspaceSession session,
        string path,
        CancellationToken cancellationToken = default)
    {
        var (root, error) = await ReadDocumentAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (string.IsNullOrWhiteSpace(root!.ClassName))
        {
            return (null, MissingClassError());
        }

        return (root.ClassName, null);
    }

    internal async Task<(XamlDocumentRoot? Root, XamlQueryError? Error)> ReadDocumentAsync(
        IWorkspaceSession session,
        string path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, new XamlDocumentNotFoundError(
                "XAML document path is empty.",
                "Pass the path of an Avalonia .axaml or MAUI .xaml document under a trusted root."));
        }

        var extension = Path.GetExtension(path);
        var avalonia = string.Equals(extension, AvaloniaDocumentExtension, StringComparison.OrdinalIgnoreCase);
        var maybeMaui = string.Equals(extension, MauiDocumentExtension, StringComparison.OrdinalIgnoreCase);
        if (!avalonia && !maybeMaui)
        {
            return (null, new UnsupportedXamlDocumentError(
                "Only Avalonia .axaml and MAUI .xaml documents are supported.",
                "Pass an Avalonia .axaml path or a MAUI .xaml path (MAUI xmlns). WPF/WinUI .xaml is not registered."));
        }

        var text = await TryGetSnapshotTextAsync(session, path, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return (null, new XamlDocumentNotFoundError(
                "The XAML document was not found in the workspace snapshot.",
                "Confirm the document path is a workspace XAML document under a trusted root, then retry."));
        }

        try
        {
            using var reader = CreateReader(text, ignoreWhitespace: true);

            if (!reader.Read())
            {
                return maybeMaui
                    ? (null, new UnsupportedXamlDocumentError(
                        "This .xaml document is not a registered MAUI document.",
                        "Pass a MAUI .xaml whose default xmlns is http://schemas.microsoft.com/dotnet/2021/maui."))
                    : (new XamlDocumentRoot(path, ClassName: null, XmlnsDeclarations: [], Text: text), null);
            }

            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element)
            {
                return maybeMaui
                    ? (null, new UnsupportedXamlDocumentError(
                        "This .xaml document is not a registered MAUI document.",
                        "Pass a MAUI .xaml whose default xmlns is http://schemas.microsoft.com/dotnet/2021/maui."))
                    : (new XamlDocumentRoot(path, ClassName: null, XmlnsDeclarations: [], Text: text), null);
            }

            var className = reader.GetAttribute("Class", XamlXmlns.Xaml);
            var xmlns = new List<(string Prefix, string XmlNamespace)>();
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    if (reader.Prefix == "xmlns")
                    {
                        xmlns.Add((reader.LocalName, reader.Value));
                    }
                    else if (reader.Name == "xmlns")
                    {
                        xmlns.Add(("", reader.Value));
                    }
                } while (reader.MoveToNextAttribute());
            }

            if (maybeMaui &&
                !xmlns.Any(x => x.Prefix == "" &&
                                string.Equals(x.XmlNamespace, XamlXmlns.Maui, StringComparison.Ordinal)))
            {
                return (null, new UnsupportedXamlDocumentError(
                    "This .xaml document is not a registered MAUI document.",
                    "Pass a MAUI .xaml whose default xmlns is http://schemas.microsoft.com/dotnet/2021/maui. WPF/WinUI .xaml is not registered."));
            }

            return (new XamlDocumentRoot(
                path,
                string.IsNullOrWhiteSpace(className) ? null : className.Trim(),
                xmlns,
                text), null);
        }
        catch (XmlException)
        {
            return (new XamlDocumentRoot(path, ClassName: null, XmlnsDeclarations: [], Text: text), null);
        }
    }


    private static IEnumerable<XamlXmlnsMapping> ResolveDeclaration(
        string prefix,
        string xmlNamespace,
        string? defaultAssembly,
        IReadOnlyList<XmlnsDefinition> definitions)
    {
        if (xmlNamespace.StartsWith("using:", StringComparison.Ordinal))
        {
            yield return new XamlXmlnsMapping(
                prefix,
                xmlNamespace,
                xmlNamespace["using:".Length..].Trim(),
                defaultAssembly,
                XamlXmlnsSource.Using);
            yield break;
        }

        if (xmlNamespace.StartsWith("clr-namespace:", StringComparison.Ordinal))
        {
            var (clr, assembly) = ParseClrNamespace(xmlNamespace);
            yield return new XamlXmlnsMapping(
                prefix,
                xmlNamespace,
                clr,
                assembly ?? defaultAssembly,
                XamlXmlnsSource.ClrNamespace);
            yield break;
        }

        var hits = definitions
            .Where(d => string.Equals(d.XmlNamespace, xmlNamespace, StringComparison.Ordinal))
            .ToArray();
        if (hits.Length == 0)
        {
            yield return new XamlXmlnsMapping(
                prefix,
                xmlNamespace,
                ClrNamespace: null,
                AssemblyName: null,
                XamlXmlnsSource.XmlNamespace);
            yield break;
        }

        foreach (var hit in hits)
        {
            yield return new XamlXmlnsMapping(
                prefix,
                xmlNamespace,
                hit.ClrNamespace,
                hit.AssemblyName,
                XamlXmlnsSource.XmlnsDefinition);
        }
    }

    private static (string ClrNamespace, string? Assembly) ParseClrNamespace(string value)
    {
        var body = value["clr-namespace:".Length..];
        var parts = body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var clr = parts.Length == 0 ? "" : parts[0];
        string? assembly = null;
        foreach (var part in parts.Skip(1))
        {
            const string prefix = "assembly=";
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                assembly = part[prefix.Length..].Trim();
            }
        }

        return (clr, assembly);
    }

    private async Task<(string? TypeName, SymbolQueryError? Error)> TryResolveStaticDataContextTypeAsync(
        IWorkspaceSession session,
        XamlDocumentRoot root,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root.ClassName))
        {
            return (null, null);
        }

        var (resolved, symbolError) = await _languages
            .ResolveByNameAsync(session, root.ClassName, projectId: null, cancellationToken)
            .ConfigureAwait(false);
        if (symbolError is not null || resolved is null)
        {
            return (null, symbolError);
        }

        var (_, symbol, handleError) = await _roslyn
            .ResolveHandleSymbolAsync(session, resolved.Handle, cancellationToken)
            .ConfigureAwait(false);
        if (handleError is not null || symbol is not INamedTypeSymbol type)
        {
            return (null, handleError);
        }

        foreach (var member in type.GetMembers("DataContext"))
        {
            ITypeSymbol? declared = member switch
            {
                IPropertySymbol property => property.Type,
                IFieldSymbol field => field.Type,
                _ => null
            };
            if (declared is null ||
                declared.SpecialType == SpecialType.System_Object ||
                declared.TypeKind == TypeKind.Dynamic)
            {
                continue;
            }

            return (declared.ToDisplayString(), null);
        }

        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var syntaxRef in ctor.DeclaringSyntaxReferences)
            {
                var text = (await syntaxRef.GetSyntaxAsync(cancellationToken).ConfigureAwait(false)).ToString();
                foreach (var marker in new[] { "DataContext = new ", "DataContext=new ", "DataContext = New ", "Me.DataContext = New " })
                {
                    var idx = text.IndexOf(marker, StringComparison.Ordinal);
                    if (idx < 0)
                    {
                        continue;
                    }

                    var start = idx + marker.Length;
                    var end = start;
                    while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '.' or '_'))
                    {
                        end++;
                    }

                    if (end > start)
                    {
                        return (text[start..end], null);
                    }
                }
            }
        }

        return (null, null);
    }

    private static async Task<string?> ResolveDefaultAssemblyNameAsync(
        IWorkspaceSession session,
        string? className,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(className))
        {
            foreach (var project in session.Solution.Projects.Where(p => p.Language is LanguageNames.CSharp or LanguageNames.VisualBasic))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Compilation compilation;
                try
                {
                    compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (compilation.GetTypeByMetadataName(className) is not null)
                {
                    return compilation.AssemblyName ?? project.Name;
                }
            }
        }

        var first = session.Solution.Projects.FirstOrDefault(p => p.Language is LanguageNames.CSharp or LanguageNames.VisualBasic);
        return first?.AssemblyName ?? first?.Name;
    }

    private static async Task<IReadOnlyList<XmlnsDefinition>> CollectXmlnsDefinitionsAsync(
        IWorkspaceSession session,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<XmlnsDefinition>();

        foreach (var project in session.Solution.Projects.Where(IsRoslynProject))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Compilation compilation;
            try
            {
                compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            AddDefinitions(compilation.Assembly, definitions, seen);
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                {
                    AddDefinitions(assembly, definitions, seen);
                }
            }
        }

        return definitions;
    }

    private static void AddDefinitions(
        IAssemblySymbol assembly,
        List<XmlnsDefinition> definitions,
        HashSet<string> seen)
    {
        foreach (var attribute in assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.Name != "XmlnsDefinitionAttribute" ||
                attribute.ConstructorArguments.Length < 2)
            {
                continue;
            }

            var xml = attribute.ConstructorArguments[0].Value as string;
            var clr = attribute.ConstructorArguments[1].Value as string;
            if (string.IsNullOrWhiteSpace(xml) || string.IsNullOrWhiteSpace(clr))
            {
                continue;
            }

            var key = $"{xml}\u001f{clr}\u001f{assembly.Name}";
            if (!seen.Add(key))
            {
                continue;
            }

            definitions.Add(new XmlnsDefinition(xml, clr, assembly.Name));
        }
    }

    private static bool HasOrdinaryMethod(ITypeSymbol type, string name) =>
        type.GetMembers(name).OfType<IMethodSymbol>().Any(m =>
            m.MethodKind == MethodKind.Ordinary && !m.IsStatic);

    private static string? FindFirstDataType(string text)
    {
        try
        {
            using var reader = CreateReader(text);

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                var dataType = reader.GetAttribute("DataType", XamlXmlns.Xaml);
                if (!string.IsNullOrWhiteSpace(dataType))
                {
                    return dataType.Trim();
                }
            }
        }
        catch (XmlException)
        {
        }

        return null;
    }

    private static string ResolveTypeName(string dataType, IReadOnlyList<XamlXmlnsMapping> xmlns)
    {
        var colon = dataType.IndexOf(':');
        if (colon <= 0)
        {
            var defaultNs = xmlns.FirstOrDefault(x => x.Prefix == "" && x.ClrNamespace is not null);
            return defaultNs?.ClrNamespace is { Length: > 0 } ns
                ? $"{ns}.{dataType}"
                : dataType;
        }

        var prefix = dataType[..colon];
        var name = dataType[(colon + 1)..];
        var mapping = xmlns.FirstOrDefault(x =>
            string.Equals(x.Prefix, prefix, StringComparison.Ordinal) && x.ClrNamespace is not null);
        return mapping?.ClrNamespace is { Length: > 0 } clr
            ? $"{clr}.{name}"
            : dataType;
    }

    internal static IReadOnlySet<string> CollectXNames(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var reader = CreateReader(text);

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                var name = reader.GetAttribute("Name", XamlXmlns.Xaml);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name.Trim());
                }
            }
        }
        catch (XmlException)
        {
        }

        return names;
    }

    private async Task<List<DiagnosticItem>> CollectSemanticDiagnosticsAsync(
        IWorkspaceSession session,
        string path,
        XamlDocumentRoot root,
        IReadOnlyList<XamlXmlnsMapping> xmlns,
        Stopwatch clock,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        var items = new List<DiagnosticItem>();
        var projectId = session.Solution.Projects.FirstOrDefault(IsRoslynProject)
            ?.Id.Id.ToString("D") ?? "";

        INamedTypeSymbol? classType = null;
        if (!string.IsNullOrWhiteSpace(root.ClassName))
        {
            var (resolved, _) = await _languages.ResolveByNameAsync(session, root.ClassName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (resolved is not null)
            {
                var (_, symbol, _) = await _roslyn.ResolveHandleSymbolAsync(session, resolved.Handle, cancellationToken)
                    .ConfigureAwait(false);
                classType = symbol as INamedTypeSymbol;
                projectId = resolved.Summary.ProjectId;
            }
        }

        try
        {
            using var reader = CreateReader(root.Text);

            string? currentDataType = null;
            var lineInfo = reader as IXmlLineInfo;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget > TimeSpan.Zero && clock.Elapsed >= budget)
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                var dataType = reader.GetAttribute("DataType", XamlXmlns.Xaml);
                if (!string.IsNullOrWhiteSpace(dataType))
                {
                    currentDataType = dataType.Trim();
                }

                var prefix = reader.Prefix;
                var local = reader.LocalName;
                if (!string.Equals(local, "Window", StringComparison.Ordinal) || prefix.Length > 0)
                {
                    var elementType = await ResolveElementTypeAsync(session, prefix, local, xmlns, cancellationToken)
                        .ConfigureAwait(false);
                    if (elementType is null && !IsLanguageElement(prefix, local))
                    {
                        items.Add(Diag("XAML0001", "Error",
                            $"Unknown element '{FormatName(prefix, local)}' given xmlns.",
                            path, lineInfo, projectId));
                    }

                    if (reader.HasAttributes && reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            if (IsSkippableAttribute(reader.Prefix, reader.LocalName, reader.Name))
                            {
                                continue;
                            }

                            if (LooksLikeBinding(reader.Value) && !string.IsNullOrWhiteSpace(currentDataType))
                            {
                                var bindingPath = ExtractBindingPath(reader.Value);
                                if (!string.IsNullOrWhiteSpace(bindingPath))
                                {
                                    var (_, bindError, _) = await ResolveBindingAsync(
                                            session, path, bindingPath, currentDataType, cancellationToken)
                                        .ConfigureAwait(false);
                                    if (bindError is BindingPropertyNotFoundError or BindingTypeMismatchError)
                                    {
                                        items.Add(Diag("XAML0003", "Error",
                                            $"Binding path '{bindingPath}' is invalid: {bindError.Message}",
                                            path, lineInfo, projectId));
                                    }
                                }
                            }

                            if (elementType is not null &&
                                !HasPublicMember(elementType, reader.LocalName))
                            {
                                items.Add(Diag("XAML0002", "Error",
                                    $"Unknown property '{reader.LocalName}' on '{elementType.ToDisplayString()}'.",
                                    path, lineInfo, projectId));
                            }
                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                }
                else if (reader.HasAttributes && reader.MoveToFirstAttribute())
                {
                    do
                    {
                        if (LooksLikeBinding(reader.Value) && !string.IsNullOrWhiteSpace(currentDataType))
                        {
                            var bindingPath = ExtractBindingPath(reader.Value);
                            if (!string.IsNullOrWhiteSpace(bindingPath))
                            {
                                var (_, bindError, _) = await ResolveBindingAsync(
                                        session, path, bindingPath, currentDataType, cancellationToken)
                                    .ConfigureAwait(false);
                                if (bindError is BindingPropertyNotFoundError or BindingTypeMismatchError)
                                {
                                    items.Add(Diag("XAML0003", "Error",
                                        $"Binding path '{bindingPath}' is invalid: {bindError.Message}",
                                        path, lineInfo, projectId));
                                }
                            }
                        }
                    } while (reader.MoveToNextAttribute());
                    reader.MoveToElement();
                }

                var xName = reader.GetAttribute("Name", XamlXmlns.Xaml);
                if (!string.IsNullOrWhiteSpace(xName) && classType is not null)
                {
                    var field = classType.GetMembers(xName.Trim())
                        .OfType<IFieldSymbol>()
                        .FirstOrDefault();
                    if (field is null)
                    {
                        items.Add(Diag("XAML0004", "Error",
                            $"x:Name '{xName}' has no matching NameGenerator field on '{classType.ToDisplayString()}'.",
                            path, lineInfo, projectId));
                    }
                }
            }
        }
        catch (XmlException)
        {
            // Semantic contract: well-formedness is not the diagnostic surface.
        }

        return items;
    }

    private async Task<INamedTypeSymbol?> ResolveElementTypeAsync(
        IWorkspaceSession session,
        string prefix,
        string localName,
        IReadOnlyList<XamlXmlnsMapping> xmlns,
        CancellationToken cancellationToken)
    {
        var mapping = xmlns.FirstOrDefault(x =>
            string.Equals(x.Prefix, prefix, StringComparison.Ordinal) && x.ClrNamespace is not null);
        if (mapping?.ClrNamespace is null)
        {
            return null;
        }

        var metadataName = $"{mapping.ClrNamespace}.{localName}";
        foreach (var project in session.Solution.Projects.Where(IsRoslynProject))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Compilation compilation;
            try
            {
                compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var type = compilation.GetTypeByMetadataName(metadataName);
            if (type is not null)
            {
                return type;
            }
        }

        return null;
    }

    private static bool HasPublicMember(ITypeSymbol type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetMembers(name).Any(m =>
                    m.DeclaredAccessibility == Accessibility.Public &&
                    m.Kind is SymbolKind.Property or SymbolKind.Field or SymbolKind.Event))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLanguageElement(string prefix, string local) =>
        string.Equals(prefix, "x", StringComparison.Ordinal);

    private static bool IsSkippableAttribute(string prefix, string local, string name) =>
        name.StartsWith("xmlns", StringComparison.Ordinal) ||
        string.Equals(prefix, "x", StringComparison.Ordinal) ||
        string.Equals(prefix, "xml", StringComparison.Ordinal);

    private static bool LooksLikeBinding(string value) =>
        value.StartsWith("{Binding", StringComparison.Ordinal);

    private static string? ExtractBindingPath(string value)
    {
        var inner = value.Trim();
        if (inner.StartsWith("{", StringComparison.Ordinal) && inner.EndsWith("}", StringComparison.Ordinal))
        {
            inner = inner[1..^1].Trim();
        }

        if (inner.StartsWith("Binding", StringComparison.Ordinal))
        {
            inner = inner["Binding".Length..].Trim();
        }

        if (inner.Length == 0)
        {
            return null;
        }

        const string pathEq = "Path=";
        var pathIdx = inner.IndexOf(pathEq, StringComparison.Ordinal);
        if (pathIdx >= 0)
        {
            var rest = inner[(pathIdx + pathEq.Length)..];
            var end = rest.IndexOfAny([' ', ',', '}']);
            return end < 0 ? rest.Trim() : rest[..end].Trim();
        }

        var tokenEnd = inner.IndexOfAny([' ', ',', '}']);
        return tokenEnd < 0 ? inner : inner[..tokenEnd].Trim();
    }

    private static string FormatName(string prefix, string local) =>
        string.IsNullOrEmpty(prefix) ? local : $"{prefix}:{local}";

    private static DiagnosticItem Diag(
        string id,
        string severity,
        string message,
        string path,
        IXmlLineInfo? lineInfo,
        string projectId) =>
        new(
            id,
            severity,
            message,
            path,
            lineInfo is not null && lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
            lineInfo is not null && lineInfo.HasLineInfo() ? lineInfo.LinePosition : null,
            lineInfo is not null && lineInfo.HasLineInfo() ? lineInfo.LineNumber : null,
            lineInfo is not null && lineInfo.HasLineInfo() ? lineInfo.LinePosition : null,
            projectId);


    private static bool IsRoslynProject(Project project) =>
        project.Language is LanguageNames.CSharp or LanguageNames.VisualBasic;

    private static XmlReader CreateReader(string text, bool ignoreWhitespace = false) =>
        XmlReader.Create(new StringReader(text), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = ignoreWhitespace
        });

    private static async Task<string?> TryGetSnapshotTextAsync(
        IWorkspaceSession session,
        string path,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in SnapshotPathCandidates(path))
        {
            foreach (var documentId in session.Solution.GetDocumentIdsWithFilePath(candidate))
            {
                var document = (TextDocument?)session.Solution.GetDocument(documentId)
                    ?? session.Solution.GetAdditionalDocument(documentId);
                if (document is null)
                {
                    continue;
                }

                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                return text.ToString();
            }
        }

        string? fullPath = null;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var project in session.Solution.Projects)
        {
            foreach (var document in EnumerateTextDocuments(project))
            {
                if (string.IsNullOrWhiteSpace(document.FilePath))
                {
                    continue;
                }

                string documentPath;
                try
                {
                    documentPath = Path.GetFullPath(document.FilePath);
                }
                catch (Exception)
                {
                    continue;
                }

                if (!string.Equals(documentPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                return text.ToString();
            }
        }

        return null;
    }

    private static IEnumerable<string> SnapshotPathCandidates(string path)
    {
        yield return path;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            yield break;
        }

        if (!string.Equals(fullPath, path, StringComparison.OrdinalIgnoreCase))
        {
            yield return fullPath;
        }
    }

    private static IEnumerable<TextDocument> EnumerateTextDocuments(Project project)
    {
        foreach (var document in project.Documents)
        {
            yield return document;
        }

        foreach (var document in project.AdditionalDocuments)
        {
            yield return document;
        }
    }

    private static MissingXamlClassError MissingClassError() =>
        new(
            "The XAML document has no x:Class on the root element.",
            "Add x:Class to the document root (xmlns:x maps to the XAML namespace), then retry xaml_resolve_class.");

    internal sealed record XamlDocumentRoot(
        string Path,
        string? ClassName,
        IReadOnlyList<(string Prefix, string XmlNamespace)> XmlnsDeclarations,
        string Text);

    private sealed record XmlnsDefinition(string XmlNamespace, string ClrNamespace, string AssemblyName);
}
