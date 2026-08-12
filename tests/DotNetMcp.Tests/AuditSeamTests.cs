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
