using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DotNetMcp.Server;

/// <summary>
/// Writes audit events to the local process logger (stderr under stdio host). No outbound telemetry.
/// </summary>
public sealed class LoggerAuditLogger : IAuditLogger
{
    public const string CategoryName = "DotNetMcp.Audit";
    public const int MaxPathLength = 512;

    private readonly ILogger _logger;
    private readonly AuditOptions _options;

    public LoggerAuditLogger(ILoggerFactory loggerFactory, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(options);
        _logger = loggerFactory.CreateLogger(CategoryName);
        _options = options;
    }

    public void ToolInvoked(string toolName, string? path = null) =>
        Write("tool_invoked", toolName, path);

    public void PathPolicyDenied(string toolName, string? path = null) =>
        Write("path_policy_denied", toolName, path);

    private void Write(string kind, string toolName, string? path)
    {
        if (!_options.Enabled)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        if (path is null)
        {
            _logger.LogInformation("audit {Kind} tool={ToolName}", kind, toolName);
        }
        else
        {
            _logger.LogInformation(
                "audit {Kind} tool={ToolName} path={Path}",
                kind,
                toolName,
                SanitizePath(path));
        }
    }

    private static string SanitizePath(string path)
    {
        var builder = new StringBuilder(Math.Min(path.Length, MaxPathLength + 3));
        var truncated = false;
        foreach (var ch in path)
        {
            var piece = NeedsEscape(ch)
                ? "\\u" + ((int)ch).ToString("X4", CultureInfo.InvariantCulture)
                : ch.ToString();
            if (builder.Length + piece.Length > MaxPathLength)
            {
                truncated = true;
                break;
            }

            builder.Append(piece);
        }

        if (truncated)
        {
            builder.Append("...");
        }

        return builder.ToString();
    }

    private static bool NeedsEscape(char ch) =>
        char.IsControl(ch) || ch is '\u2028' or '\u2029';
}