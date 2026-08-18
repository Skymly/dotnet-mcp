using System.Xml;
using DotNetMcp.Core;

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
        var (className, xamlError) = ReadClassName(path);
        if (xamlError is not null)
        {
            return (null, xamlError, null);
        }

        var (success, symbolError) = await _symbols
            .ResolveByNameAsync(session, className!, projectId: null, cancellationToken)
            .ConfigureAwait(false);
        return (success, null, symbolError);
    }

    public (string? ClassName, XamlQueryError? Error) ReadClassName(string path)
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
                "Confirm the .axaml path under a trusted root, then retry xaml_resolve_class."));
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
                return MissingClass();
            }

            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element)
            {
                return MissingClass();
            }

            var className = reader.GetAttribute("Class", XamlXmlns.Xaml);
            if (string.IsNullOrWhiteSpace(className))
            {
                return MissingClass();
            }

            return (className.Trim(), null);
        }
        catch (XmlException)
        {
            return MissingClass();
        }
        catch (IOException)
        {
            return (null, new XamlDocumentNotFoundError(
                "The Avalonia XAML document could not be read.",
                "Confirm the .axaml path under a trusted root, then retry xaml_resolve_class."));
        }
    }

    private static (string? ClassName, XamlQueryError? Error) MissingClass() =>
        (null, new MissingXamlClassError(
            "The Avalonia document has no x:Class on the root element.",
            "Add x:Class to the .axaml root (xmlns:x maps to the XAML namespace), then retry xaml_resolve_class."));
}
