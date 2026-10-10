using DotNetMcp.Server;
using Microsoft.CodeAnalysis;

namespace DotNetMcp.Tests;

public class FixtureShapeSeamTests
{
    [Fact]
    public async Task workspace_status_surfaces_loader_warnings()
    {
        var root = CreateTempDir("warn");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        const string warning = "WorkspaceFailed: missing import";

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                new WarningLoader(warning));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            var status = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(
                await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>()));
            Assert.NotNull(status.Warnings);
            Assert.Contains(warning, status.Warnings);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task partial_load_reports_units_and_remaining_estimate()
    {
        var root = CreateTempDir("prog");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var loader = new PacedLoader();

        try
        {
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]), loader);
            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            await loader.ReportedZero.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            loader.ContinueToPartial.TrySetResult();

            var partial = await WaitForAsync(fx, status =>
                status.Phase == "loading" && status.CompletedUnits == 1 && status.TotalUnits == 4);
            Assert.True(partial.EstimatedRemainingMs > 0);

            loader.Release.TrySetResult();
            var ready = await WorkspaceReady.WaitUntilReadyAsync(fx);
            Assert.Equal(1, ready.CompletedUnits);
            Assert.Equal(1, ready.TotalUnits);
            Assert.Equal(0, ready.EstimatedRemainingMs);
        }
        finally
        {
            loader.ContinueToPartial.TrySetResult();
            loader.Release.TrySetResult();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task unsupported_extension_is_rejected_by_production_and_fake_loaders()
    {
        var root = CreateTempDir("ext");
        var notes = Path.Combine(root, "notes.txt");
        await File.WriteAllTextAsync(notes, "not a workspace");
        var loader = new MsBuildSolutionLoader(TrustedRoots.Create([root]));

        var production = await Assert.ThrowsAsync<InvalidDataException>(() =>
            loader.OpenAsync(notes));
        Assert.Contains("Unsupported workspace path extension", production.Message, StringComparison.Ordinal);

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateMultiTfm());
            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = notes });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            var failed = await WaitForAsync(fx, status => status.Phase == "failed");
            Assert.Contains("Unsupported workspace path extension", failed.Error, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<WorkspaceStatusDto> WaitForAsync(
        InProcessMcpFixture fx,
        Func<WorkspaceStatusDto, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WorkspaceStatusDto? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            Assert.True(poll.IsError is not true, InProcessMcpFixture.TextOf(poll));
            last = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (predicate(last))
            {
                return last;
            }

            await Task.Delay(15);
        }

        throw new TimeoutException($"status did not match: phase={last?.Phase} units={last?.CompletedUnits}/{last?.TotalUnits} estimate={last?.EstimatedRemainingMs} error={last?.Error}");
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), "dotnet-mcp-shape-" + label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class WarningLoader : ISolutionLoader
    {
        private readonly string _warning;

        public WarningLoader(string warning) => _warning = warning;

        public Task<LoadedSolution> OpenAsync(
            string path,
            IProgress<LoadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            workspace.TryApplyChanges(workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "App",
                "App",
                LanguageNames.CSharp)));
            progress?.Report(new LoadProgress(0, 1));
            progress?.Report(new LoadProgress(1, 1));
            return Task.FromResult(new LoadedSolution(workspace, workspace.CurrentSolution, [_warning]));
        }
    }

    private sealed class PacedLoader : ISolutionLoader
    {
        public TaskCompletionSource ReportedZero { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueToPartial { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LoadedSolution> OpenAsync(
            string path,
            IProgress<LoadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new LoadProgress(0, 4));
            ReportedZero.TrySetResult();
            await ContinueToPartial.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            progress?.Report(new LoadProgress(1, 4));
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            workspace.TryApplyChanges(workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "App",
                "App",
                LanguageNames.CSharp)));
            return new LoadedSolution(workspace, workspace.CurrentSolution, []);
        }
    }
}
