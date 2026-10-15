using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace DotNetMcp.Server;

/// <summary>
/// Process-wide MSBuildLocator registration (ported from Spike S2).
/// </summary>
public static class MsBuildBootstrap
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            if (!MSBuildLocator.IsRegistered)
            {
                var sdkDir = TryFindNewestSdkDirectory();
                if (sdkDir is not null)
                {
                    MSBuildLocator.RegisterMSBuildPath(sdkDir);
                }
                else
                {
                    var runtimeMajor = Environment.Version.Major;
                    var chosen = ChooseFallbackInstance(
                        MSBuildLocator.QueryVisualStudioInstances()
                            .Select(i => new MSBuildCandidate(i.DiscoveryType, i.Version, i.MSBuildPath, i)),
                        runtimeMajor);
                    if (chosen?.Instance is not { } instance)
                    {
                        throw new InvalidOperationException(
                            $"No MSBuild instance that .NET {runtimeMajor} can load was found via MSBuildLocator. "
                            + $"Install a .NET {runtimeMajor} SDK, or set DOTNET_ROOT to the dotnet install that has one.");
                    }

                    MSBuildLocator.RegisterInstance(instance);
                }
            }

            _registered = true;
        }
    }

    /// <summary>
    /// Newest SDK version directory (<c>.../dotnet/sdk/x.y.z</c>) that this process can load MSBuild from, or null.
    /// </summary>
    internal static string? TryFindNewestSdkDirectory() =>
        TryFindNewestSdkDirectory(
        [
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
            @"C:\Program Files\dotnet",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
        ]);

    internal static string? TryFindNewestSdkDirectory(IEnumerable<string?> roots) =>
        TryFindNewestSdkDirectory(roots, Environment.Version.Major);

    /// <summary>
    /// Picks the SDK directory under <paramref name="roots"/> whose MSBuild this process can load.
    /// A candidate must contain MSBuild.dll and must not have a major version newer than
    /// <paramref name="runtimeMajorVersion"/>: that MSBuild targets the newer runtime and cannot load here.
    /// A stable SDK beats a preview one even when the preview is numerically higher; within each class
    /// the highest version wins. Roots are tried in order and the first root with a usable SDK wins.
    /// Does not read global.json. Production passes the install roots above.
    /// </summary>
    internal static string? TryFindNewestSdkDirectory(IEnumerable<string?> roots, int runtimeMajorVersion)
    {
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sdkRoot = Path.Combine(root!, "sdk");
            if (!Directory.Exists(sdkRoot))
            {
                continue;
            }

            var best = Directory.GetDirectories(sdkRoot)
                .Select(ParseSdkDirectory)
                .OfType<SdkDirectory>()
                .Where(sdk => sdk.Version.Major <= runtimeMajorVersion
                    && File.Exists(Path.Combine(sdk.FullPath, "MSBuild.dll")))
                .OrderBy(sdk => sdk.IsPreview)
                .ThenByDescending(sdk => sdk.Version)
                .FirstOrDefault();

            if (best is not null)
            {
                return best.FullPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Dotnet install root (parent of <c>sdk</c>), derived from <see cref="TryFindNewestSdkDirectory"/>.
    /// </summary>
    internal static string? TryGetDotNetInstallRoot()
    {
        var sdkVersionDir = TryFindNewestSdkDirectory();
        if (sdkVersionDir is null)
        {
            return null;
        }

        return Directory.GetParent(sdkVersionDir)?.Parent?.FullName;
    }

    /// <summary>One MSBuildLocator instance reduced to what the fallback choice needs.</summary>
    internal sealed record MSBuildCandidate(
        DiscoveryType Discovery,
        Version Version,
        string? MSBuildPath,
        VisualStudioInstance? Instance = null);

    /// <summary>
    /// Fallback when no SDK folder was found under the known install roots: picks the instance to register
    /// from what MSBuildLocator discovered. A .NET SDK instance whose major version is newer than
    /// <paramref name="runtimeMajorVersion"/> is skipped, a stable SDK beats a preview one, and the highest
    /// version wins. Returns null when nothing usable remains.
    /// </summary>
    internal static MSBuildCandidate? ChooseFallbackInstance(
        IEnumerable<MSBuildCandidate> candidates,
        int runtimeMajorVersion)
    {
        var usable = candidates
            .Where(c => c.Discovery != DiscoveryType.DotNetSdk || c.Version.Major <= runtimeMajorVersion)
            .ToArray();

        return usable
                .Where(c => c.Discovery == DiscoveryType.DotNetSdk)
                .OrderBy(c => IsPreviewSdkDirectory(c.MSBuildPath))
                .ThenByDescending(c => c.Version)
                .FirstOrDefault()
            ?? usable.OrderByDescending(c => c.Version).FirstOrDefault();
    }

    private sealed record SdkDirectory(string FullPath, Version Version, bool IsPreview);

    /// <summary>
    /// Reads an <c>sdk/&lt;version&gt;</c> folder name such as <c>10.0.300</c> or <c>10.0.300-preview.1</c>.
    /// Folders that are not versions (for example <c>NuGetFallbackFolder</c>) yield null.
    /// </summary>
    private static SdkDirectory? ParseSdkDirectory(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var parts = name.Split('-', 2);
        return Version.TryParse(parts[0], out var version)
            ? new SdkDirectory(fullPath, version, IsPreview: parts.Length > 1)
            : null;
    }

    private static bool IsPreviewSdkDirectory(string? fullPath) =>
        fullPath is not null && ParseSdkDirectory(fullPath) is { IsPreview: true };
}

public sealed class MsBuildSolutionLoader : ISolutionLoader
{
    private readonly TrustedRoots _trustedRoots;

    public MsBuildSolutionLoader(TrustedRoots trustedRoots)
    {
        _trustedRoots = trustedRoots ?? throw new ArgumentNullException(nameof(trustedRoots));
    }

    public async Task<LoadedSolution> OpenAsync(
        string path,
        IProgress<LoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Workspace path not found.", fullPath);
        }

        MsBuildBootstrap.EnsureRegistered();
        var ext = Path.GetExtension(fullPath);

        // LoadedSolution.Warnings is this same list, so warnings added after the load show up in the status.
        var warnings = new List<string>();
        LoadedSolution loaded;
        if (ext.Equals(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            loaded = await OpenSlnfAsync(fullPath, warnings, progress, cancellationToken).ConfigureAwait(false);
        }
        else if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            loaded = await OpenSolutionAsync(fullPath, warnings, progress, cancellationToken).ConfigureAwait(false);
        }
        else if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase))
        {
            loaded = await OpenProjectAsync(fullPath, warnings, progress, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidDataException(
                $"Unsupported workspace path extension '{ext}'. Use .sln, .slnx, .slnf, or a project file.");
        }

        await EnsureGraphOrDisposeAsync(loaded, _trustedRoots).ConfigureAwait(false);

        // Only after the gate: the analyzer files are known to sit under trusted or toolchain roots.
        warnings.AddRange(AnalyzerCompilerSkew.Detect(loaded.Solution));

        return loaded;
    }

    internal static async Task EnsureGraphOrDisposeAsync(LoadedSolution loaded, TrustedRoots trustedRoots)
    {
        try
        {
            TrustedGraphGate.EnsureLoadedSolutionUnderRoots(loaded, trustedRoots);
        }
        catch
        {
            await loaded.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static MSBuildWorkspace CreateWorkspace(List<string> warnings)
    {
        // Keep Visual Basic language services in the default host catalog.
        _ = typeof(Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilation);
        var workspace = MSBuildWorkspace.Create();
#pragma warning disable CS0618
        workspace.WorkspaceFailed += (_, e) =>
        {
            warnings.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");
        };
#pragma warning restore CS0618
        return workspace;
    }

    private static async Task<LoadedSolution> OpenSolutionAsync(
        string solutionPath,
        List<string> warnings,
        IProgress<LoadProgress>? progress,
        CancellationToken ct)
    {
        var workspace = CreateWorkspace(warnings);
        progress?.Report(new LoadProgress(0, 1));

        var sw = Stopwatch.StartNew();
        try
        {
            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct)
                .ConfigureAwait(false);
            sw.Stop();
            var count = Math.Max(1, solution.ProjectIds.Count);
            progress?.Report(new LoadProgress(count, count));
            return new LoadedSolution(workspace, solution, warnings);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private static async Task<LoadedSolution> OpenProjectAsync(
        string projectPath,
        List<string> warnings,
        IProgress<LoadProgress>? progress,
        CancellationToken ct)
    {
        var workspace = CreateWorkspace(warnings);
        progress?.Report(new LoadProgress(0, 1));

        try
        {
            _ = await workspace.OpenProjectAsync(projectPath, cancellationToken: ct)
                .ConfigureAwait(false);
            var count = Math.Max(1, workspace.CurrentSolution.ProjectIds.Count);
            progress?.Report(new LoadProgress(count, count));
            return new LoadedSolution(workspace, workspace.CurrentSolution, warnings);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private async Task<LoadedSolution> OpenSlnfAsync(
        string slnfPath,
        List<string> warnings,
        IProgress<LoadProgress>? progress,
        CancellationToken ct)
    {
        var projectPaths = SlnfParser.ResolveProjectPaths(slnfPath);
        // Hard-fail escaping .slnf entries before MSBuild opens (and executes) them.
        TrustedGraphGate.EnsureProjectPathsUnderRoots(projectPaths, _trustedRoots, ".slnf");
        var workspace = CreateWorkspace(warnings);
        var total = Math.Max(1, projectPaths.Count);
        progress?.Report(new LoadProgress(0, total));

        try
        {
            var completed = 0;
            foreach (var projectPath in projectPaths)
            {
                ct.ThrowIfCancellationRequested();
                await workspace.OpenProjectAsync(projectPath, cancellationToken: ct)
                    .ConfigureAwait(false);
                completed++;
                progress?.Report(new LoadProgress(completed, total));
            }

            return new LoadedSolution(workspace, workspace.CurrentSolution, warnings);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }
}
