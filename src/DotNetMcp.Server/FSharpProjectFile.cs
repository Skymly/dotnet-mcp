using System.Xml.Linq;

namespace DotNetMcp.Server;

internal static class FSharpProjectFile
{
    public static IReadOnlyList<string> ReadCompilePaths(string fsprojPath)
    {
        if (!File.Exists(fsprojPath))
        {
            return [];
        }

        XDocument document;
        try
        {
            document = XDocument.Load(fsprojPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(fsprojPath)) ?? "";
        var paths = new List<string>();
        foreach (var include in document.Descendants().Where(e => e.Name.LocalName == "Compile"))
        {
            var spec = (string?)include.Attribute("Include");
            if (string.IsNullOrWhiteSpace(spec))
            {
                continue;
            }

            var combined = Path.GetFullPath(Path.Combine(dir, spec.Replace('\\', Path.DirectorySeparatorChar)));
            var ext = Path.GetExtension(combined);
            if (ext.Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".fsi", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".fsx", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(combined);
            }
        }

        return paths;
    }

    public static IReadOnlyList<string> ReadDefines(string fsprojPath)
    {
        if (!File.Exists(fsprojPath))
        {
            return [];
        }

        try
        {
            var document = XDocument.Load(fsprojPath);
            var raw = document.Descendants()
                .Where(e => e.Name.LocalName == "DefineConstants")
                .Select(e => e.Value)
                .LastOrDefault();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
    }
}
