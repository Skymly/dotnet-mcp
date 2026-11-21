using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DotNetMcp.Server;

internal sealed record SlnxProjectBatch(IReadOnlyList<string> Projects, string GlobalJson)
{
    private const string DotNetSdk = "Microsoft.NET.Sdk";

    internal static SlnxProjectBatch? TryPlan(IEnumerable<string> paths)
    {
        var projects = paths.Where(IsStandardSdkProject).ToArray();
        if (projects.Length < 2)
        {
            return null;
        }

        // Roslyn reuses one build host per kind in a project traversal. Separate
        // SDK scopes need their own hosts, as in the ordinary per-project path.
        var globalJsonPath = FindGlobalJson(projects[0]);
        if (projects.Skip(1).Any(project =>
            !string.Equals(FindGlobalJson(project), globalJsonPath, PathPolicy.Comparison)))
        {
            return null;
        }

        if (globalJsonPath is null)
        {
            // Stop SDK discovery at the scratch directory even if its parent
            // happens to contain a global.json of its own.
            return new SlnxProjectBatch(projects, "{}");
        }

        var contents = File.ReadAllText(globalJsonPath);
        using var json = JsonDocument.Parse(contents, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            (json.RootElement.TryGetProperty("sdk", out var sdk) &&
             (sdk.ValueKind != JsonValueKind.Object || sdk.TryGetProperty("paths", out _))))
        {
            // A relocated SDK search path can resolve to a different installation.
            return null;
        }

        return new SlnxProjectBatch(projects, contents);
    }

    internal XElement CreateTraversalProject() => new("Project",
        new XElement("Import", new XAttribute("Project", "Sdk.props"), new XAttribute("Sdk", DotNetSdk)),
        new XElement("PropertyGroup",
            new XElement("TargetFramework", "net10.0"),
            new XElement("EnableDefaultItems", "false"),
            new XElement("BuildProjectReferences", "false"),
            new XElement("ImportDirectoryBuildProps", "false"),
            new XElement("ImportDirectoryBuildTargets", "false")),
        new XElement("ItemGroup", Projects.Select(path =>
            new XElement("ProjectReference", new XAttribute("Include", path)))),
        new XElement("Import", new XAttribute("Project", "Sdk.targets"), new XAttribute("Sdk", DotNetSdk)),
        // Supply the design-time targets without compiling or restoring this
        // generated project. Roslyn discovers the actual projects through its references.
        new XElement("Target", new XAttribute("Name", "Compile")),
        new XElement("Target", new XAttribute("Name", "CoreCompile")));

    private static bool IsStandardSdkProject(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        // An Include with MSBuild expression or wildcard syntax needs the ordinary
        // loader, which takes the project filename directly instead of parsing an item.
        if (extension is not (".csproj" or ".vbproj" or ".fsproj") ||
            path.AsSpan().IndexOfAny('*', '?', ';') >= 0 ||
            path.AsSpan().IndexOfAny('$', '@', '%') >= 0 ||
            !File.Exists(path))
        {
            return false;
        }

        try
        {
            return (string?)XDocument.Load(path).Root?.Attribute("Sdk") == DotNetSdk;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }

    private static string? FindGlobalJson(string projectPath)
    {
        for (var directory = Path.GetDirectoryName(projectPath);
             directory is not null;
             directory = Path.GetDirectoryName(directory))
        {
            var path = Path.Combine(directory, "global.json");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
