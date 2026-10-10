using System.Runtime.InteropServices;

namespace DotNetMcp.Server;

/// <summary>
/// Runtime-derived roots for SDK and NuGet analyzer assemblies (ADR-0004 Amendment 6).
/// Distinct from <see cref="TrustedRoots"/>: those are user-declared; these are inferred
/// from the machine. Error text must keep the two kinds separate.
/// </summary>
public sealed class ToolchainRoots
{
    private readonly string[] _normalizedRoots;

    private ToolchainRoots(string[] normalizedRoots)
    {
        _normalizedRoots = normalizedRoots;
    }

    public IReadOnlyList<string> Roots => _normalizedRoots;

    public static ToolchainRoots Discover()
    {
        var dotnetRoots = new List<string>();
        TryAddExistingDirectory(dotnetRoots, MsBuildBootstrap.TryGetDotNetInstallRoot());
        TryAddExistingDirectory(dotnetRoots, WalkRuntimeDirectoryToDotNetInstall());

        var nugetRoots = new List<string>();
        TryAddExistingDirectory(nugetRoots, ResolveNuGetPackagesDirectory());
        foreach (var fallback in SplitFallbackPackages())
        {
            TryAddExistingDirectory(nugetRoots, fallback);
        }

        foreach (var dotnetRoot in dotnetRoots)
        {
            TryAddExistingDirectory(nugetRoots, Path.Combine(dotnetRoot, "sdk", "NuGetFallbackFolder"));
        }

        var combined = dotnetRoots
            .Concat(nugetRoots)
            .Distinct(PathPolicy.Comparer)
            .ToArray();
        return new ToolchainRoots(combined);
    }

    public bool Contains(string path)
    {
        string normalized;
        try
        {
            normalized = PathPolicy.Normalize(path);
        }
        catch (Exception ex) when (ex is PathPolicyException or ArgumentException)
        {
            return false;
        }

        foreach (var root in _normalizedRoots)
        {
            if (PathPolicy.IsUnderRoot(normalized, root))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ResolveNuGetPackagesDirectory()
    {
        var env = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
        {
            return null;
        }

        return Path.Combine(profile, ".nuget", "packages");
    }

    private static IEnumerable<string> SplitFallbackPackages()
    {
        var env = Environment.GetEnvironmentVariable("NUGET_FALLBACK_PACKAGES");
        if (string.IsNullOrWhiteSpace(env))
        {
            yield break;
        }

        foreach (var part in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }

    /// <summary>
    /// Walk up from the runtime directory until a folder contains both <c>sdk</c> and <c>shared</c>.
    /// Covers Linux CI when the newest SDK directory lookup returns null.
    /// </summary>
    private static string? WalkRuntimeDirectoryToDotNetInstall()
    {
        try
        {
            return FindInstallRoot(RuntimeEnvironment.GetRuntimeDirectory());
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Walk up from <paramref name="start"/> until a folder contains both sdk and shared.
    /// </summary>
    internal static string? FindInstallRoot(string? start)
    {
        if (string.IsNullOrWhiteSpace(start))
        {
            return null;
        }

        DirectoryInfo? dir;
        try
        {
            dir = new DirectoryInfo(start);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        while (dir is not null)
        {
            var sdk = Path.Combine(dir.FullName, "sdk");
            var shared = Path.Combine(dir.FullName, "shared");
            if (Directory.Exists(sdk) && Directory.Exists(shared))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static void TryAddExistingDirectory(List<string> dest, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            dest.Add(PathPolicy.Normalize(path));
        }
        catch (Exception ex) when (ex is PathPolicyException or ArgumentException)
        {
            // Skip unresolvable toolchain locations rather than fail the whole discovery.
        }
    }
}
