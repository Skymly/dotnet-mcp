namespace DotNetMcp.Server;

/// <summary>
/// Local audit sink for tool invocations and path-policy denials (ADR-0004 §5).
/// Records tool name and path metadata only — never source or generated text.
/// </summary>
public interface IAuditLogger
{
    void ToolInvoked(string toolName, string? path = null);

    void PathPolicyDenied(string toolName, string? path = null);

    /// <summary>
    /// A tool let an unexpected exception escape (issue #548). The exception — stack trace
    /// included — stays on this local sink; the tool response only carries the sanitized
    /// structured error.
    /// </summary>
    void ToolFailed(string toolName, Exception exception);
}
