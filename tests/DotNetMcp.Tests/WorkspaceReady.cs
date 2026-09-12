using DotNetMcp.Server;

namespace DotNetMcp.Tests;

internal static class WorkspaceReady
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan MsBuildTimeout = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    public static async Task<WorkspaceStatusDto> OpenUntilReadyAsync(
        InProcessMcpFixture fx,
        string path,
        TimeSpan? timeout = null)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = path });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
        return await WaitUntilReadyAsync(fx, timeout).ConfigureAwait(false);
    }

    public static async Task<WorkspaceStatusDto> WaitUntilReadyAsync(
        InProcessMcpFixture fx,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        WorkspaceStatusDto? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            Assert.True(poll.IsError is not true, InProcessMcpFixture.TextOf(poll));
            last = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (last.Phase is "failed" or "cancelled")
            {
                Assert.Fail($"workspace {last.Phase}: error={last.Error} message={last.Message}");
            }

            if (last.Phase == "ready")
            {
                return last;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }

        Assert.Fail($"workspace did not become ready: phase={last?.Phase} error={last?.Error} message={last?.Message}");
        return last!;
    }

    public static async Task OpenUntilReadyAsync(
        WorkspaceHost host,
        string path,
        TimeSpan? timeout = null)
    {
        host.BeginOpen(path);
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        WorkspaceStatusDto? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = host.GetStatus();
            if (last.Phase is "failed" or "cancelled")
            {
                Assert.Fail($"workspace {last.Phase}: error={last.Error} message={last.Message}");
            }

            if (last.Phase == "ready")
            {
                return;
            }

            await Task.Delay(PollInterval).ConfigureAwait(false);
        }

        Assert.Fail($"workspace did not become ready: phase={last?.Phase} error={last?.Error} message={last?.Message}");
    }
}
