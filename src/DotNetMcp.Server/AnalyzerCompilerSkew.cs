using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Server;

/// <summary>
/// Spots analyzer assemblies that reference a newer Microsoft.CodeAnalysis than this server bundles.
/// Roslyn refuses to load those (<c>ReferencesNewerCompiler</c>, CS9057) and
/// <c>AnalyzerReference.GetGenerators</c> then quietly returns nothing, so generator queries, diagnostics
/// and attribution would be incomplete without any sign of why.
/// Only assembly metadata is read. The analyzer is never loaded: loading it would execute it, which is what
/// the trusted-graph gate exists to prevent (ADR-0004 Amendment 6).
/// </summary>
public static class AnalyzerCompilerSkew
{
    public const string WarningCode = "AnalyzerReferencesNewerCompiler";

    private const string CompilerAssemblyName = "Microsoft.CodeAnalysis";

    /// <summary>Warnings for every analyzer in the loaded solution that references a newer compiler than the host.</summary>
    internal static IReadOnlyList<string> Detect(Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
        return Detect(
            solution.Projects.SelectMany(static project => project.AnalyzerReferences).Select(static reference => reference.FullPath),
            typeof(Compilation).Assembly.GetName().Version!);
    }

    /// <summary>
    /// One warning per distinct analyzer file name and referenced version. Missing files, files that are not
    /// managed assemblies and unreadable files are skipped. The warning names the file only, not its path.
    /// </summary>
    internal static IReadOnlyList<string> Detect(IEnumerable<string?> analyzerPaths, Version hostCompilerVersion)
    {
        ArgumentNullException.ThrowIfNull(analyzerPaths);
        ArgumentNullException.ThrowIfNull(hostCompilerVersion);

        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in analyzerPaths
                     .Where(static p => !string.IsNullOrWhiteSpace(p))
                     .Distinct(PathPolicy.Comparer))
        {
            var referenced = TryReadCompilerReference(path!);
            if (referenced is not null && referenced > hostCompilerVersion)
            {
                warnings.Add(Format(Path.GetFileName(path!), referenced, hostCompilerVersion));
            }
        }

        return [.. warnings];
    }

    /// <summary>The highest Microsoft.CodeAnalysis version the assembly at <paramref name="path"/> references, if any.</summary>
    private static Version? TryReadCompilerReference(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }

            var reader = pe.GetMetadataReader();
            Version? highest = null;
            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                if (reader.GetString(reference.Name) == CompilerAssemblyName
                    && (highest is null || reference.Version > highest))
                {
                    highest = reference.Version;
                }
            }

            return highest;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or BadImageFormatException
            or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Format(string fileName, Version referenced, Version host) =>
        $"{WarningCode}: '{fileName}' references Microsoft.CodeAnalysis {referenced}, newer than the {host} bundled with this server. "
        + "Roslyn refuses to load it, so its source generators and analyzers are missing from generator queries, diagnostics, and attribution. "
        + "Use a dotnet-mcp release that bundles a newer Roslyn.";
}
