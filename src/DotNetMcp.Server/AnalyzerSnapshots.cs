using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DotNetMcp.Server;

/// <summary>
/// Issue #549: analyzer references as <c>MSBuildWorkspace</c> hands them out are lazy
/// <see cref="AnalyzerFileReference"/> instances whose default loader memory-maps the
/// analyzer DLL once <c>GetAnalyzers</c>/<c>GetGenerators</c> first runs — the file then
/// stays locked for the rest of the session and the loaded bytes can never move on, so a
/// user-side <c>dotnet build</c> that rebuilds an in-workspace generator collides on disk
/// and diagnostics keep running the stale generator.
///
/// The pieces here keep the same reference shape (<see cref="AnalyzerFileReference"/>, so
/// <see cref="AnalyzerReference.FullPath"/>-based checks like <see cref="AnalyzerCompilerSkew"/>
/// and the trusted-graph gate keep working) but load assemblies from in-memory bytes inside
/// a per-path collectible <see cref="AssemblyLoadContext"/> — nothing is memory-mapped, so
/// analyzer DLLs are never locked — and each content change gets a fresh loader instance,
/// which both re-reads the bytes and makes the swapped reference compare unequal to the old
/// one (<see cref="AnalyzerFileReference"/> equality includes the loader), so the solution
/// diff actually applies the refresh.
/// </summary>
internal sealed class NonLockingAnalyzerLoader : IAnalyzerAssemblyLoader
{
    private readonly Func<string, bool> _allowRead;
    private readonly ConcurrentDictionary<string, AnalyzerImageContext> _contexts = new(PathPolicy.Comparer);
    private readonly ConcurrentDictionary<string, string> _dependencyPathsByName = new(StringComparer.OrdinalIgnoreCase);

    internal NonLockingAnalyzerLoader(Func<string, bool> allowRead)
    {
        _allowRead = allowRead ?? throw new ArgumentNullException(nameof(allowRead));
    }

    /// <summary>Registered analyzer paths act as cross-analyzer dependency probes.</summary>
    public void AddDependencyLocation(string fullPath)
    {
        var normalized = TryNormalize(fullPath);
        if (normalized is null)
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(normalized);
        if (!string.IsNullOrWhiteSpace(name))
        {
            _dependencyPathsByName.TryAdd(name, normalized);
        }
    }

    /// <summary>
    /// Called by <see cref="AnalyzerFileReference"/> when analyzers/generators materialize.
    /// Reads bytes once per (path, loader generation); the file is never mapped, so it is
    /// never locked.
    /// </summary>
    public Assembly LoadFromPath(string fullPath)
    {
        var normalized = TryNormalize(fullPath)
            ?? throw new UnauthorizedAccessException(
                $"Analyzer path '{fullPath}' cannot be canonicalized; refusing to load.");
        var context = _contexts.GetOrAdd(normalized, static (path, self) => new AnalyzerImageContext(path, self), this);
        return context.LoadAnalyzer();
    }

    private byte[] ReadAllowedBytes(string path)
    {
        if (!_allowRead(path))
        {
            throw new UnauthorizedAccessException(
                $"Analyzer path '{path}' is outside the configured trusted roots and toolchain roots; refusing to load.");
        }

        return File.ReadAllBytes(path);
    }

    private bool TryReadAllowedBytes(string path, out byte[] bytes)
    {
        var normalized = TryNormalize(path);
        try
        {
            if (normalized is not null && _allowRead(normalized) && File.Exists(normalized))
            {
                bytes = File.ReadAllBytes(normalized);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        bytes = [];
        return false;
    }

    private bool TryGetRegisteredDependencyPath(string simpleName, out string? path) =>
        _dependencyPathsByName.TryGetValue(simpleName, out path);

    private static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return PathPolicy.Normalize(path);
        }
        catch (Exception ex) when (ex is PathPolicyException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// One collectible load context per analyzer path. Dependency resolution defers to the
    /// default context for anything the host can already provide (Microsoft.CodeAnalysis,
    /// the BCL, the server itself) so analyzer types stay identity-compatible with
    /// <see cref="ISourceGenerator"/>/<see cref="DiagnosticAnalyzer"/>; private dependencies
    /// are byte-loaded from the analyzer's own directory, its deps.json probing, or the
    /// MSBuild-registered dependency locations.
    /// </summary>
    private sealed class AnalyzerImageContext : AssemblyLoadContext
    {
        private readonly string _analyzerPath;
        private readonly string _analyzerDirectory;
        private readonly NonLockingAnalyzerLoader _owner;
        private readonly AssemblyDependencyResolver _resolver;
        private readonly object _loadGate = new();
        private Assembly? _analyzer;

        internal AnalyzerImageContext(string analyzerPath, NonLockingAnalyzerLoader owner)
            : base("dotnet-mcp analyzers: " + Path.GetFileNameWithoutExtension(analyzerPath), isCollectible: true)
        {
            _analyzerPath = analyzerPath;
            _analyzerDirectory = Path.GetDirectoryName(analyzerPath) ?? string.Empty;
            _owner = owner;
            _resolver = new AssemblyDependencyResolver(analyzerPath);
        }

        internal Assembly LoadAnalyzer()
        {
            lock (_loadGate)
            {
                _analyzer ??= LoadFromStream(new MemoryStream(_owner.ReadAllowedBytes(_analyzerPath), writable: false));
                return _analyzer;
            }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName.Name))
            {
                return null;
            }

            if (DefaultsToHostAssembly(assemblyName))
            {
                return null;
            }

            var sibling = Path.Combine(_analyzerDirectory, assemblyName.Name + ".dll");
            if (_owner.TryReadAllowedBytes(sibling, out var siblingBytes))
            {
                return LoadFromStream(new MemoryStream(siblingBytes, writable: false));
            }

            var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
            if (resolved is not null && _owner.TryReadAllowedBytes(resolved, out var resolvedBytes))
            {
                return LoadFromStream(new MemoryStream(resolvedBytes, writable: false));
            }

            if (_owner.TryGetRegisteredDependencyPath(assemblyName.Name, out var registered)
                && registered is not null
                && _owner.TryReadAllowedBytes(registered, out var registeredBytes))
            {
                return LoadFromStream(new MemoryStream(registeredBytes, writable: false));
            }

            return null;
        }

        /// <summary>
        /// True when the default context can satisfy this name — then returning null lets the
        /// runtime bind the host copy, keeping a single Microsoft.CodeAnalysis in play.
        /// </summary>
        private static bool DefaultsToHostAssembly(AssemblyName assemblyName)
        {
            try
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName) is not null;
            }
            catch (Exception ex) when (ex is IOException
                or FileNotFoundException
                or FileLoadException
                or BadImageFormatException
                or ArgumentException)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// File-content stamp for one analyzer path: presence, length, and last-write ticks.
/// Rebuilds always touch the file, so this notices a rewritten analyzer without hashing
/// bytes on every epoch (issue #549).
/// </summary>
internal readonly record struct AnalyzerFileStamp(bool Exists, long Length, long LastWriteTimeUtcTicks)
{
    internal static AnalyzerFileStamp Capture(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            return info.Exists
                ? new AnalyzerFileStamp(Exists: true, info.Length, info.LastWriteTimeUtc.Ticks)
                : new AnalyzerFileStamp(Exists: false, 0, 0);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return new AnalyzerFileStamp(Exists: false, 0, 0);
        }
    }
}

/// <summary>
/// Pending analyzer swap computed outside the workspace gate (issue #549): full replacement
/// reference lists per project plus the stamps to record once the swap applies.
/// </summary>
internal sealed class AnalyzerRefreshPlan
{
    internal AnalyzerRefreshPlan(
        IReadOnlyList<KeyValuePair<ProjectId, IReadOnlyList<AnalyzerReference>>> swaps,
        IReadOnlyDictionary<string, AnalyzerFileStamp> newStamps)
    {
        Swaps = swaps;
        NewStamps = newStamps;
    }

    internal IReadOnlyList<KeyValuePair<ProjectId, IReadOnlyList<AnalyzerReference>>> Swaps { get; }

    internal IReadOnlyDictionary<string, AnalyzerFileStamp> NewStamps { get; }
}
