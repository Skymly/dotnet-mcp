using System.Collections.Concurrent;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

internal sealed class RecordingAuditLogger : IAuditLogger
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();
    private readonly AuditOptions _options;

    public RecordingAuditLogger(AuditOptions? options = null)
    {
        _options = options ?? AuditOptions.Default;
    }

    public IReadOnlyList<AuditEvent> Snapshot() => _events.ToArray();

    public void ToolInvoked(string toolName, string? path = null)
    {
        if (!_options.Enabled)
        {
            return;
        }

        _events.Enqueue(new AuditEvent("tool_invoked", toolName, path));
    }

    public void PathPolicyDenied(string toolName, string? path = null)
    {
        if (!_options.Enabled)
        {
            return;
        }

        _events.Enqueue(new AuditEvent("path_policy_denied", toolName, path));
    }

    public readonly record struct AuditEvent(string Kind, string ToolName, string? Path);
}
