using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

/// <summary>
/// Security High-finding fixtures: parent-symlink canonicalize, fail-closed links,
/// explicit trusted roots, .slnf graph gate, write final-path gate, F# snapshot freeze.
/// </summary>
public class SecurityHighFixTests
{
    [Fact]
    public void path_policy_resolves_parent_directory_symlink_for_existing_leaf()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var secret = Path.Combine(outside, "secret.cs");
        File.WriteAllText(secret, "OUTSIDE");
        var link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, outside);

        try
        {
            var lexical = Path.Combine(link, "secret.cs");
            Assert.True(File.Exists(lexical));

            var normalized = PathPolicy.Normalize(lexical);
            Assert.Equal(PathPolicy.Normalize(secret), normalized);

            var trusted = TrustedRoots.Create([root]);
            Assert.False(trusted.Contains(lexical));
            Assert.True(trusted.Contains(Path.Combine(root, "ok.txt")));
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public void path_policy_fail_closed_when_symlink_target_missing()
    {
        var root = CreateTempDir("root");
        var missingTarget = Path.Combine(root, "missing-target");
        var link = Path.Combine(root, "broken");
        Directory.CreateSymbolicLink(link, missingTarget);

        try
        {
            Assert.ThrowsAny<PathPolicyException>(() => PathPolicy.Normalize(link));

            var trusted = TrustedRoots.Create([root]);
            Assert.False(trusted.Contains(link));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void trusted_roots_from_startup_fail_closed_without_explicit_roots()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS", null);
            var ex = Assert.Throws<InvalidOperationException>(() => TrustedRoots.FromStartup([]));
            Assert.Contains("--roots", ex.Message, StringComparison.Ordinal);
            Assert.Contains("DOTNET_MCP_TRUSTED_ROOTS", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS", previous);
        }
    }

    [Fact]
    public void trusted_roots_from_startup_accepts_explicit_roots_arg()
    {
        var root = CreateTempDir("explicit");
        var previous = Environment.GetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS", null);
            var trusted = TrustedRoots.FromStartup(["--roots", root]);
            Assert.True(trusted.Contains(root));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_MCP_TRUSTED_ROOTS", previous);
            TryDelete(root);
        }
    }

    [Fact]
    public void loaded_graph_with_out_of_root_project_reference_is_rejected()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var outsideProj = Path.Combine(outside, "Evil.csproj");
        File.WriteAllText(outsideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var insideProj = Path.Combine(root, "App.csproj");
        File.WriteAllText(insideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var workspace = new AdhocWorkspace();
        var insideId = ProjectId.CreateNewId();
        var outsideId = ProjectId.CreateNewId();
        workspace.AddProject(ProjectInfo.Create(
            insideId,
            VersionStamp.Create(),
            "App",
            "App",
            LanguageNames.CSharp,
            filePath: insideProj,
            projectReferences: [new ProjectReference(outsideId)]));
        workspace.AddProject(ProjectInfo.Create(
            outsideId,
            VersionStamp.Create(),
            "Evil",
            "Evil",
            LanguageNames.CSharp,
            filePath: outsideProj));

        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, []);
        var trusted = TrustedRoots.Create([root]);

        try
        {
            var ex = Assert.ThrowsAny<InvalidOperationException>(
                () => TrustedGraphGate.EnsureLoadedSolutionUnderRoots(loaded, trusted));
            Assert.Contains("trusted roots", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public void slnf_escaping_project_path_is_rejected_before_load()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var outsideProj = Path.Combine(outside, "Evil.csproj");
        File.WriteAllText(outsideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var sln = Path.Combine(root, "App.sln");
        File.WriteAllText(sln, "Microsoft Visual Studio Solution File, Format Version 12.00\n");
        var slnf = Path.Combine(root, "App.slnf");
        File.WriteAllText(
            slnf,
            $$"""
            {
              "solution": {
                "path": "App.sln",
                "projects": [ "{{outsideProj.Replace("\\", "/")}}" ]
              }
            }
            """);

        try
        {
            var trusted = TrustedRoots.Create([root]);
            var loader = new MsBuildSolutionLoader(trusted);
            var ex = Assert.ThrowsAny<InvalidOperationException>(
                () => loader.OpenAsync(slnf).GetAwaiter().GetResult());
            Assert.Contains("trusted roots", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task write_declared_paths_refuses_when_leaf_retargeted_outside_root()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var insideFile = Path.Combine(root, "Widget.cs");
        File.WriteAllText(insideFile, "old");
        var outsideFile = Path.Combine(outside, "Widget.cs");
        File.WriteAllText(outsideFile, "old");

        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Lib", LanguageNames.CSharp);
        var widgetId = DocumentId.CreateNewId(project.Id);
        workspace.TryApplyChanges(
            workspace.CurrentSolution.AddDocument(
                DocumentInfo.Create(
                    widgetId,
                    "Widget.cs",
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From("old"), VersionStamp.Create())),
                    filePath: insideFile)));
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, []);

        var host = new WorkspaceHost(
            new FixedSolutionLoader(loaded),
            new WorkspaceHostOptions { Debounce = TimeSpan.Zero, FileWatcher = new ManualWorkspaceFileWatcher() },
            TrustedRoots.Create([root]));

        try
        {
            host.BeginOpen(Path.Combine(root, "Lib.csproj"));
            WaitReady(host);

            // Retarget the leaf path to an outside file after preview-time trust checks would have passed.
            File.Delete(insideFile);
            File.CreateSymbolicLink(insideFile, outsideFile);

            var outcome = host.WriteDeclaredPaths(
                [new WorkspaceEditDocument(insideFile, "old", "NEW")]);

            Assert.True(outcome.Failed);
            Assert.Equal(PolicyErrorCodes.PathOutsideTrustedRoots, outcome.Error!.Error);
            Assert.Equal("old", File.ReadAllText(outsideFile));
        }
        finally
        {
            await host.DisposeAsync();
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public void fsharp_capture_skips_symlink_directories_and_outside_roots()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var outsideFs = Path.Combine(outside, "Leak.fs");
        File.WriteAllText(outsideFs, "module Leak");

        var projDir = Path.Combine(root, "FsLib");
        Directory.CreateDirectory(projDir);
        var insideFs = Path.Combine(projDir, "Ok.fs");
        File.WriteAllText(insideFs, "module Ok");
        var link = Path.Combine(projDir, "escape");
        Directory.CreateSymbolicLink(link, outside);

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name: "FsLib",
            assemblyName: "FsLib",
            language: LanguageNames.FSharp,
            filePath: Path.Combine(projDir, "FsLib.fsproj"));
        workspace.AddProject(projectInfo);
        var docId1 = DocumentId.CreateNewId(projectId);
        workspace.AddDocument(DocumentInfo.Create(docId1, "Ok.fs", loader: TextLoader.From(TextAndVersion.Create(SourceText.From("module Ok"), VersionStamp.Create())), filePath: insideFs));

        try
        {
            var trusted = TrustedRoots.Create([root]);
            var snapshot = WorkspaceSession.CaptureFSharpSnapshot(workspace.CurrentSolution, epoch: 1, trusted);

            var paths = snapshot.Projects.SelectMany(p => p.Documents).Select(d => d.Path).ToArray();
            Assert.Contains(paths, p => PathPolicy.Normalize(p) == PathPolicy.Normalize(insideFs));
            Assert.DoesNotContain(paths, p => PathPolicy.Normalize(p) == PathPolicy.Normalize(outsideFs));
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task fsharp_snapshot_is_frozen_across_ready_sessions_for_same_epoch()
    {
        var root = CreateTempDir("root");
        var projDir = Path.Combine(root, "FsLib");
        Directory.CreateDirectory(projDir);
        var fsPath = Path.Combine(projDir, "Ok.fs");
        File.WriteAllText(fsPath, "module Ok // v1");

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "FsLib",
            "FsLib",
            LanguageNames.FSharp,
            filePath: Path.Combine(projDir, "FsLib.fsproj")));
        var docId = DocumentId.CreateNewId(projectId);
        workspace.AddDocument(DocumentInfo.Create(docId, "Ok.fs", loader: TextLoader.From(TextAndVersion.Create(SourceText.From("module Ok // v1"), VersionStamp.Create())), filePath: fsPath));
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, []);

        var host = new WorkspaceHost(
            new FixedSolutionLoader(loaded),
            new WorkspaceHostOptions { Debounce = TimeSpan.Zero, FileWatcher = new ManualWorkspaceFileWatcher() },
            TrustedRoots.Create([root]));

        try
        {
            host.BeginOpen(Path.Combine(projDir, "FsLib.fsproj"));
            WaitReady(host);

            Assert.True(host.TryGetReadySession(out var session1));
            var text1 = Assert.Single(Assert.Single(session1!.FSharpSnapshot.Projects).Documents).Text;

            File.WriteAllText(fsPath, "module Ok // v2-mutated-on-disk");

            Assert.True(host.TryGetReadySession(out var session2));
            var text2 = Assert.Single(Assert.Single(session2!.FSharpSnapshot.Projects).Documents).Text;

            Assert.Equal(session1.Epoch, session2.Epoch);
            Assert.Equal(text1, text2);
            Assert.Contains("v1", text2, StringComparison.Ordinal);
            Assert.DoesNotContain("v2-mutated-on-disk", text2, StringComparison.Ordinal);
        }
        finally
        {
            await host.DisposeAsync();
            TryDelete(root);
        }
    }

    private static void WaitReady(WorkspaceHost host)
    {
        for (var i = 0; i < 50; i++)
        {
            if (host.GetStatus().Phase == "ready")
            {
                return;
            }

            Thread.Sleep(20);
        }

        Assert.Fail($"workspace not ready: {host.GetStatus().Phase} {host.GetStatus().Error}");
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-sec-{label}-{Guid.NewGuid():N}");
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
            // best-effort
        }
    }

    private sealed class FixedSolutionLoader(LoadedSolution loaded) : ISolutionLoader
    {
        public Task<LoadedSolution> OpenAsync(
            string path,
            IProgress<LoadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new LoadProgress(1, 1));
            return Task.FromResult(loaded);
        }
    }
}
