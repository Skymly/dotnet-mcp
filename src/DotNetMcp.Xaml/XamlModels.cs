namespace DotNetMcp.Xaml;

public abstract record XamlQueryError(string Code, string Message, string SuggestedAction);

public sealed record MissingXamlClassError(string Message, string SuggestedAction)
    : XamlQueryError(XamlQueryErrorCodes.MissingXamlClass, Message, SuggestedAction);

public sealed record XamlDocumentNotFoundError(string Message, string SuggestedAction)
    : XamlQueryError(XamlQueryErrorCodes.XamlDocumentNotFound, Message, SuggestedAction);

public sealed record UnsupportedXamlDocumentError(string Message, string SuggestedAction)
    : XamlQueryError(XamlQueryErrorCodes.UnsupportedXamlDocument, Message, SuggestedAction);

public sealed record UnknownXmlnsPrefixError(string Message, string SuggestedAction)
    : XamlQueryError(XamlQueryErrorCodes.UnknownXmlnsPrefix, Message, SuggestedAction);

public static class XamlQueryErrorCodes
{
    public const string MissingXamlClass = "MissingXamlClass";
    public const string XamlDocumentNotFound = "XamlDocumentNotFound";
    public const string UnsupportedXamlDocument = "UnsupportedXamlDocument";
    public const string UnknownXmlnsPrefix = "UnknownXmlnsPrefix";
}

public static class XamlXmlns
{
    public const string Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string Avalonia = "https://github.com/avaloniaui";
}

public static class XamlXmlnsSource
{
    public const string Using = "using";
    public const string ClrNamespace = "clr-namespace";
    public const string XmlnsDefinition = "xmlns-definition";
    public const string XmlNamespace = "xml-namespace";
}

public sealed record XamlXmlnsMapping(
    string Prefix,
    string XmlNamespace,
    string? ClrNamespace,
    string? AssemblyName,
    string Source);
