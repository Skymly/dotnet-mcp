using System.Xml;
using DotNetMcp.Core;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Xaml;

/// <summary>
/// Avalonia-first XAML document queries. Other UI frameworks are not registered.
/// </summary>
public sealed class XamlDocumentService
{
    public const string AvaloniaDocumentExtension = ".axaml";

    private readonly SymbolQueryService _symbols;

    public XamlDocumentService(SymbolQueryService symbols)
    {
        _symbols = symbols;
    }

    public async Task<(SymbolResolveSuccess? Success, XamlQueryError? XamlError, SymbolQueryError? SymbolError)>
        ResolveClassAsync(
            IWorkspaceSession session,
            string path,
            CancellationToken cancellationToken = default)
    {
        var (root, xamlError) = ReadDocument(path);
        if (xamlError is not null)
        {
            return (null, xamlError, null);
        }

        if (string.IsNullOrWhiteSpace(root!.ClassName))
        {
            return (null, MissingClassError(), null);
        }

        var (success, symbolError) = await _symbols
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
                "Pass an x:Name from the Avalonia document to xaml_resolve_name."), null);
        }

        var (root, docError) = ReadDocument(path);
        if (docError is not null)
        {
            return (null, docError, null);
        }

        var names = CollectXNames(path);
        if (!names.Contains(name.Trim()))
        {
            return (null, new MissingXamlNameError(
                $"No x:Name '{name}' is declared in the Avalonia document.",
                "Inspect the .axaml for x:Name values, then retry xaml_resolve_name."), null);
        }

        if (string.IsNullOrWhiteSpace(root!.ClassName))
        {
            return (null, MissingClassError(), null);
        }

        var (resolved, symbolError) = await _symbols
            .ResolveByNameAsync(session, root.ClassName, projectId: null, cancellationToken)
            .ConfigureAwait(false);
        if (symbolError is not null)
        {
            return (null, null, symbolError);
        }

        var (lookup, lookupError) = await _symbols
            .LookupTypeMemberAsync(session, resolved!.Handle, name.Trim(), publicOnly: false, cancellationToken)
            .ConfigureAwait(false);
        if (lookupError is not null)
        {
            return (null, new NameGeneratorNotRunError(
                $"x:Name '{name}' was found in the document but no matching field exists on '{root.ClassName}'.",
                "Ensure Avalonia NameGenerator has run (build the project), then retry xaml_resolve_name."), null);
        }

        if (lookup!.Member.Kind != Microsoft.CodeAnalysis.SymbolKind.Field)
        {
            return (null, new NameGeneratorNotRunError(
                $"x:Name '{name}' resolved to a non-field member; NameGenerator fields were not found.",
                "Ensure Avalonia NameGenerator has run (build the project), then retry xaml_resolve_name."), null);
        }

        var handle = _symbols.FormatHandle(lookup.Project, lookup.Member);
        var success = await _symbols.GetSummaryAsync(session, handle, cancellationToken).ConfigureAwait(false);
        return (success.Success, null, success.Error);
    }

    public async Task<(IReadOnlyList<XamlXmlnsMapping>? Success, XamlQueryError? Error)> ListXmlnsAsync(
        IWorkspaceSession session,
        string path,
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        var (root, error) = ReadDocument(path);
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
                    $"No xmlns prefix '{prefix}' is declared on the Avalonia document.",
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

    public (string? ClassName, XamlQueryError? Error) ReadClassName(string path)
    {
        var (root, error) = ReadDocument(path);
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

    internal (XamlDocumentRoot? Root, XamlQueryError? Error) ReadDocument(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (null, new XamlDocumentNotFoundError(
                "XAML document path is empty.",
                "Pass the path of an Avalonia .axaml document under a trusted root."));
        }

        if (!string.Equals(Path.GetExtension(path), AvaloniaDocumentExtension, StringComparison.OrdinalIgnoreCase))
        {
            return (null, new UnsupportedXamlDocumentError(
                "Only Avalonia .axaml documents are supported.",
                "Pass a .axaml document path. Other UI frameworks are not registered."));
        }

        if (!File.Exists(path))
        {
            return (null, new XamlDocumentNotFoundError(
                "The Avalonia XAML document was not found.",
                "Confirm the .axaml path under a trusted root, then retry."));
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true
            });

            if (!reader.Read())
            {
                return (new XamlDocumentRoot(path, ClassName: null, XmlnsDeclarations: []), null);
            }

            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element)
            {
                return (new XamlDocumentRoot(path, ClassName: null, XmlnsDeclarations: []), null);
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

            return (new XamlDocumentRoot(
                path,
                string.IsNullOrWhiteSpace(className) ? null : className.Trim(),
                xmlns), null);
        }
        catch (XmlException)
        {
            return (new XamlDocumentRoot(path, ClassName: null, XmlnsDeclarations: []), null);
        }
        catch (IOException)
        {
            return (null, new XamlDocumentNotFoundError(
                "The Avalonia XAML document could not be read.",
                "Confirm the .axaml path under a trusted root, then retry."));
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

    private static async Task<string?> ResolveDefaultAssemblyNameAsync(
        IWorkspaceSession session,
        string? className,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(className))
        {
            foreach (var project in session.Solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
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

        var first = session.Solution.Projects.FirstOrDefault(p => p.Language == LanguageNames.CSharp);
        return first?.AssemblyName ?? first?.Name;
    }

    private static async Task<IReadOnlyList<XmlnsDefinition>> CollectXmlnsDefinitionsAsync(
        IWorkspaceSession session,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<XmlnsDefinition>();

        foreach (var project in session.Solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
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

    internal static IReadOnlySet<string> CollectXNames(string path)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });

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
        catch (IOException)
        {
        }

        return names;
    }

    private static MissingXamlClassError MissingClassError() =>
        new(
            "The Avalonia document has no x:Class on the root element.",
            "Add x:Class to the .axaml root (xmlns:x maps to the XAML namespace), then retry xaml_resolve_class.");

    internal sealed record XamlDocumentRoot(
        string Path,
        string? ClassName,
        IReadOnlyList<(string Prefix, string XmlNamespace)> XmlnsDeclarations);

    private sealed record XmlnsDefinition(string XmlNamespace, string ClrNamespace, string AssemblyName);
}
