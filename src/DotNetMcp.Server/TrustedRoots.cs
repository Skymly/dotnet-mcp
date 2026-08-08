namespace DotNetMcp.Server;

/// <summary>
/// Sanctioned filesystem roots (ADR-0004). Paths must resolve under one of these roots.
/// </summary>
public sealed class TrustedRoots
{
    private readonly string[] _normalizedRoots;

    private TrustedRoots(string[] normalizedRoots)
    {
        if (normalizedRoots.Length == 0)
        {
            throw new ArgumentException("At least one trusted root is required.", nameof(normalizedRoots));
        }

        _normalizedRoots = normalizedRoots;
    }

    public IReadOnlyList<string> Roots => _normalizedRoots;

    public static TrustedRoots Create(IEnumerable<string> roots)
    {
        var normalized = roots
            .Select(PathPolicy.Normalize)
            .Distinct(PathComparer)
            .ToArray();
        return new TrustedRoots(normalized);
    }

    /// <summary>
    /// Resolve roots from CLI <c>--roots</c> (Path.PathSeparator-delimited) and
    /// <c>DOTNET_MCP_TRUSTED_ROOTS</c>; default is the current working directory.
    /// </summary>
    public static TrustedRoots FromStartup(string[] args)
    {
        var collected = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--roots", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException("--roots requires a path list argument.");
            }

            collected.AddRange(SplitRootList(args[++i]));
        }

        var env = Environment.GetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS");
        if (!string.IsNullOrWhiteSpace(env))
        {
            collected.AddRange(SplitRootList(env));
        }

        if (collected.Count == 0)
        {
            collected.Add(Directory.GetCurrentDirectory());
        }

        return Create(collected);
    }

    public bool Contains(string path)
    {
        var normalized = PathPolicy.Normalize(path);
        foreach (var root in _normalizedRoots)
        {
            if (PathPolicy.IsUnderRoot(normalized, root))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SplitRootList(string value) =>
        value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
