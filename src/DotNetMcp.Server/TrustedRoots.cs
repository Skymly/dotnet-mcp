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
        var normalized = new List<string>();
        foreach (var root in roots)
        {
            try
            {
                normalized.Add(PathPolicy.Normalize(root));
            }
            catch (PathPolicyException ex)
            {
                throw new ArgumentException(
                    "A trusted root path could not be canonicalized (unresolvable reparse point).",
                    nameof(roots),
                    ex);
            }
        }

        return new TrustedRoots(normalized.Distinct(PathComparer).ToArray());
    }

    /// <summary>
    /// Resolve roots from CLI <c>--roots</c> (Path.PathSeparator-delimited) and
    /// <c>DOTNET_MCP_TRUSTED_ROOTS</c>. When neither is set, startup fails closed —
    /// the process working directory is never used as an implicit sandbox.
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
            throw new InvalidOperationException(
                "Trusted roots must be configured via --roots or DOTNET_MCP_TRUSTED_ROOTS. " +
                "Refusing to default to the process working directory.");
        }

        return Create(collected);
    }

    public bool Contains(string path)
    {
        string normalized;
        try
        {
            normalized = PathPolicy.Normalize(path);
        }
        catch (PathPolicyException)
        {
            // Fail closed: unresolvable reparse points are outside the trust boundary.
            return false;
        }

        return ContainsNormalized(normalized);
    }

    /// <summary>
    /// Prefix-check an already-normalized (canonical) path against trusted roots.
    /// </summary>
    public bool ContainsNormalized(string normalizedPath)
    {
        foreach (var root in _normalizedRoots)
        {
            if (PathPolicy.IsUnderRoot(normalizedPath, root))
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
