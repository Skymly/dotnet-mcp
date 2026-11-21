using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;

namespace DotNetMcp.Server;

/// <summary>
/// Read-only MSBuild workspace for SLNX recovery. Uses Roslyn's public loader
/// and workspace publication APIs so the scratch traversal project stays out of
/// the solution exposed by this workspace.
/// </summary>
internal sealed class SlnxWorkspace : Workspace
{
    private readonly MSBuildProjectLoader _loader;

    internal SlnxWorkspace() : base(MefHostServices.DefaultHost, WorkspaceKind.MSBuild)
    {
        _loader = new MSBuildProjectLoader(this);
    }

    internal async Task OpenSolutionFileAsync(string path, CancellationToken ct)
    {
        OnSolutionAdded(await _loader.LoadSolutionInfoAsync(path, cancellationToken: ct).ConfigureAwait(false));
        UpdateReferencesAfterAdd();
    }

    internal async Task OpenProjectFileAsync(string path, CancellationToken ct)
    {
        var infos = await _loader.LoadProjectInfoAsync(
            path, ProjectMap.Create(CurrentSolution), cancellationToken: ct).ConfigureAwait(false);
        foreach (var project in infos)
        {
            OnProjectAdded(project);
        }

        UpdateReferencesAfterAdd();
    }

    internal async Task<bool> TryOpenBatchAsync(
        IReadOnlyList<string> missing, List<string> warnings, CancellationToken ct)
    {
        var before = CurrentSolution;
        DirectoryInfo? scratch = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (SlnxProjectBatch.TryPlan(missing) is not { } batch)
            {
                return false;
            }

            scratch = Directory.CreateTempSubdirectory("dotnet-mcp-slnx-");
            await File.WriteAllTextAsync(Path.Combine(scratch.FullName, "global.json"), batch.GlobalJson, ct)
                .ConfigureAwait(false);
            var entryPath = Path.Combine(scratch.FullName, "Traversal.csproj");
            batch.CreateTraversalProject().Save(entryPath);

            // The probe gets its own diagnostics and reads the same global properties
            // (including SolutionDir) as the solution. It does not change this workspace.
            using var probe = new AdhocWorkspace(Services.HostServices);
            var diagnostics = new ConcurrentQueue<WorkspaceDiagnostic>();
#pragma warning disable CS0618
            probe.WorkspaceFailed += (_, e) => diagnostics.Enqueue(e.Diagnostic);
#pragma warning restore CS0618
            var loader = new MSBuildProjectLoader(probe, _loader.Properties);
            var infos = await loader.LoadProjectInfoAsync(
                entryPath, ProjectMap.Create(before), cancellationToken: ct).ConfigureAwait(false);
            var collected = diagnostics.ToArray();
            var traversal = infos.FirstOrDefault(p => string.Equals(p.FilePath, entryPath, PathPolicy.Comparison));
            if (traversal is null ||
                collected.Any(d => d.Message.Contains(entryPath, PathPolicy.Comparison) ||
                    (d is ProjectDiagnostic projectDiagnostic && projectDiagnostic.ProjectId == traversal.Id)))
            {
                return false;
            }

            var projects = infos.Where(p => p.Id != traversal.Id).ToArray();
            var paths = projects.Select(p => p.FilePath).ToHashSet(PathPolicy.Comparer);
            // The loader can represent an evaluation failure with an empty ProjectInfo.
            // Let the regular retries report that failure for the original project.
            if (!batch.Projects.All(paths.Contains) ||
                projects.Any(p => string.IsNullOrWhiteSpace(p.OutputFilePath) ||
                    p.ProjectReferences.Any(reference => reference.ProjectId == traversal.Id)) ||
                HasOverlappingOutputs(before, projects))
            {
                return false;
            }

            var updated = before;
            foreach (var project in projects)
            {
                ct.ThrowIfCancellationRequested();
                updated = updated.AddProject(project);
            }

            SetCurrentSolution(updated);
            UpdateReferencesAfterAdd();
            warnings.AddRange(collected.Select(d => $"{d.Kind}: {d.Message}"));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!ReferenceEquals(before, CurrentSolution))
            {
                SetCurrentSolution(before);
            }

            // The normal per-project retries below retain the authoritative
            // diagnostics when the traversal could not produce the full batch.
            return false;
        }
        finally
        {
            if (scratch is not null)
            {
                try
                {
                    scratch.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Scratch cleanup must not invalidate a completed workspace load.
                }
            }
        }
    }

    private static bool HasOverlappingOutputs(Solution current, IReadOnlyList<ProjectInfo> projects)
    {
        // Roslyn maps output paths to project references on publication. Overlapping
        // paths can pick a different winner in a batch than in the per-project order.
        var outputs = new HashSet<string>(PathPolicy.Comparer);
        foreach (var project in current.Projects)
        {
            Add(project.OutputFilePath);
            Add(project.OutputRefFilePath);
        }

        foreach (var project in projects)
        {
            if (!Add(project.OutputFilePath) ||
                (project.OutputRefFilePath != project.OutputFilePath && !Add(project.OutputRefFilePath)))
            {
                return true;
            }
        }

        return false;

        bool Add(string? path) => string.IsNullOrEmpty(path) || outputs.Add(path);
    }
}
