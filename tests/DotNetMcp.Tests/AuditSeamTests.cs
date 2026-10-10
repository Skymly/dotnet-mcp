using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class AuditSeamTests
{
    [Fact]
    public async Task workspace_status_emits_tool_invoked_without_path()
    {
        var audit = new RecordingAuditLogger();
        await using var fx = new InProcessMcpFixture(auditLogger: audit);

        var result = await fx.Client.CallToolAsync(
            "workspace_status",
            new Dictionary<string, object?>());

        Assert.False(result.IsError is true);

        var events = audit.Snapshot();
        Assert.Contains(
            events,
            e => e.Kind == "tool_invoked" && e.ToolName == "workspace_status" && e.Path is null);
        Assert.DoesNotContain(
            events,
            e => e.Path is not null && e.Path.Contains("TOP_SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task workspace_open_path_denial_emits_policy_denied_without_file_contents()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var secretPath = Path.Combine(outside, "secret.txt");
        await File.WriteAllTextAsync(secretPath, "TOP_SECRET_CONTENT");

        try
        {
            var audit = new RecordingAuditLogger();
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                auditLogger: audit);

            var result = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = secretPath });

            Assert.True(result.IsError is true);
            var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
            Assert.Equal(PolicyErrorCodes.PathOutsideTrustedRoots, body.Error);

            var events = audit.Snapshot();
            Assert.Contains(
                events,
                e => e.Kind == "tool_invoked" &&
                     e.ToolName == "workspace_open" &&
                     e.Path == secretPath);
            Assert.Contains(
                events,
                e => e.Kind == "path_policy_denied" &&
                     e.ToolName == "workspace_open" &&
                     e.Path == secretPath);

            foreach (var e in events)
            {
                Assert.DoesNotContain("TOP_SECRET_CONTENT", e.ToolName, StringComparison.Ordinal);
                if (e.Path is not null)
                {
                    Assert.DoesNotContain("TOP_SECRET_CONTENT", e.Path, StringComparison.Ordinal);
                }
            }

            Assert.DoesNotContain("TOP_SECRET_CONTENT", InProcessMcpFixture.TextOf(result));
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task disabled_audit_emits_no_events()
    {
        var options = new AuditOptions { Enabled = false };
        var audit = new RecordingAuditLogger(options);
        await using var fx = new InProcessMcpFixture(auditOptions: options, auditLogger: audit);

        await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());

        Assert.Empty(audit.Snapshot());
    }

    [Fact]
    public async Task loaded_graph_denial_emits_policy_denied_for_the_outside_path()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var outsideProj = Path.Combine(outside, "Evil.csproj");
        await File.WriteAllTextAsync(outsideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var insideProj = Path.Combine(root, "App.csproj");
        await File.WriteAllTextAsync(insideProj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        LoadedSolution Factory()
        {
            var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
            var insideId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
            var outsideId = Microsoft.CodeAnalysis.ProjectId.CreateNewId();
            workspace.AddProject(Microsoft.CodeAnalysis.ProjectInfo.Create(
                insideId,
                Microsoft.CodeAnalysis.VersionStamp.Create(),
                "App",
                "App",
                Microsoft.CodeAnalysis.LanguageNames.CSharp,
                filePath: insideProj,
                projectReferences: [new Microsoft.CodeAnalysis.ProjectReference(outsideId)]));
            workspace.AddProject(Microsoft.CodeAnalysis.ProjectInfo.Create(
                outsideId,
                Microsoft.CodeAnalysis.VersionStamp.Create(),
                "Evil",
                "Evil",
                Microsoft.CodeAnalysis.LanguageNames.CSharp,
                filePath: outsideProj));
            return new LoadedSolution(workspace, workspace.CurrentSolution, []);
        }

        try
        {
            var audit = new RecordingAuditLogger();
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                new FakeSolutionLoader(TimeSpan.Zero, Factory),
                auditLogger: audit);

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = solution });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            var status = await WaitUntilFailedAsync(fx);
            Assert.Equal(PolicyErrorCodes.LoadedGraphOutsideTrustedRoots, status.ErrorCode);

            Assert.Contains(
                audit.Snapshot(),
                e => e.Kind == "path_policy_denied" &&
                     e.ToolName == "workspace_open" &&
                     e.Path == outsideProj);
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Fact]
    public async Task apply_leaf_retarget_emits_policy_denied_and_does_not_write()
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var projectDir = Path.Combine(root, "lib");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");
        var widgetPath = Path.Combine(projectDir, "Widget.cs");
        var outsideFile = Path.Combine(outside, "Widget.cs");
        await File.WriteAllTextAsync(outsideFile, "untouched");

        try
        {
            var audit = new RecordingAuditLogger();
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithRenameOnDisk(projectDir),
                new WorkspaceHostOptions
                {
                    Debounce = TimeSpan.Zero,
                    FileWatcher = new ManualWorkspaceFileWatcher(),
                    BeforeApplyFinalPathGate = () =>
                    {
                        File.Delete(widgetPath);
                        File.CreateSymbolicLink(widgetPath, outsideFile);
                    }
                },
                auditLogger: audit);

            await WorkspaceReady.OpenUntilReadyAsync(fx, solution);
            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "RenameApp.Widget.Ping" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?> { ["handle"] = handle, ["newName"] = "Pong" });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var previewId = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview).PreviewId;

            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewId });
            Assert.True(apply.IsError is true, InProcessMcpFixture.TextOf(apply));
            Assert.Equal(
                PolicyErrorCodes.PathOutsideTrustedRoots,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(apply).Error);
            Assert.Equal("untouched", await File.ReadAllTextAsync(outsideFile));
            Assert.Contains(
                audit.Snapshot(),
                e => e.Kind == "path_policy_denied" &&
                     e.ToolName == "symbol_apply_rename" &&
                     string.Equals(e.Path, outsideFile, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    [Theory]
    [InlineData("xaml_resolve_class", null, null)]
    [InlineData("xaml_list_xmlns", null, null)]
    [InlineData("xaml_resolve_name", "name", "Title")]
    [InlineData("xaml_resolve_binding", "bindingPath", "Title")]
    [InlineData("xaml_diagnostics", null, null)]
    public async Task xaml_path_denial_emits_policy_denied(string tool, string? extraName, string? extraValue)
    {
        var root = CreateTempDir("root");
        var outside = CreateTempDir("outside");
        var secretPath = Path.Combine(outside, "MainPage.xaml");
        await File.WriteAllTextAsync(secretPath, "<ContentPage />");

        try
        {
            var audit = new RecordingAuditLogger();
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                auditLogger: audit);
            var args = new Dictionary<string, object?> { ["path"] = secretPath };
            if (extraName is not null)
            {
                args[extraName] = extraValue;
            }

            var result = await fx.Client.CallToolAsync(tool, args);
            Assert.True(result.IsError is true, InProcessMcpFixture.TextOf(result));
            Assert.Equal(
                PolicyErrorCodes.PathOutsideTrustedRoots,
                InProcessMcpFixture.Deserialize<PolicyErrorDto>(result).Error);
            Assert.Contains(
                audit.Snapshot(),
                e => e.Kind == "path_policy_denied" &&
                     e.ToolName == tool &&
                     e.Path == secretPath);
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    private static async Task<WorkspaceStatusDto> WaitUntilFailedAsync(InProcessMcpFixture fx)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        WorkspaceStatusDto? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            Assert.True(poll.IsError is not true, InProcessMcpFixture.TextOf(poll));
            last = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (last.Phase == "failed")
            {
                return last;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"workspace did not fail: phase={last?.Phase} error={last?.Error}");
    }
    private static string CreateTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-audit-{prefix}-{Guid.NewGuid():N}");
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
            // best-effort cleanup
        }
    }
}
