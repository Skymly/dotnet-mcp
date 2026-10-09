using DotNetMcp.Server;
using Microsoft.Extensions.Logging;

namespace DotNetMcp.Tests;

public class LoggerAuditLoggerTests
{
    [Fact]
    public void path_control_characters_are_escaped_before_they_reach_the_log()
    {
        var logger = new CaptureLogger();
        var audit = new LoggerAuditLogger(new SingleLoggerFactory(logger), AuditOptions.Default);

        audit.ToolInvoked("workspace_open", "C:\\repo\\file.cs\r\n audit forged \u001b[31mred");

        var message = Assert.Single(logger.Messages);
        Assert.Contains("workspace_open", message, StringComparison.Ordinal);
        Assert.Contains("file.cs", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", message, StringComparison.Ordinal);
        Assert.Contains("\\u000D", message, StringComparison.Ordinal);
        Assert.Contains("\\u000A", message, StringComparison.Ordinal);
        Assert.Contains("\\u001B", message, StringComparison.Ordinal);
    }

    [Fact]
    public void long_audit_path_is_truncated()
    {
        var logger = new CaptureLogger();
        var audit = new LoggerAuditLogger(new SingleLoggerFactory(logger), AuditOptions.Default);
        var path = new string('a', 2000) + "\n";

        audit.PathPolicyDenied("xaml_diagnostics", path);

        var message = Assert.Single(logger.Messages);
        Assert.DoesNotContain("\n", message, StringComparison.Ordinal);
        Assert.Contains("...", message, StringComparison.Ordinal);
        Assert.True(message.Length < 800, message.Length.ToString());
    }

    private sealed class SingleLoggerFactory : ILoggerFactory
    {
        private readonly ILogger _logger;

        public SingleLoggerFactory(ILogger logger) => _logger = logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => _logger;

        public void Dispose()
        {
        }
    }

    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}