using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// Shared observation flag for the test-only cancel probe (singleton in fixture DI).
/// </summary>
public sealed class TasksCancelProbeObservation
{
    private int _cancelled;

    public bool SawCancelled => Volatile.Read(ref _cancelled) != 0;

    public void MarkCancelled() => Volatile.Write(ref _cancelled, 1);
}

/// <summary>
/// Test-only long-running tool so Tasks cancel can observe request CancellationToken.
/// Not part of the product tool surface.
/// </summary>
[McpServerToolType]
public sealed class TasksCancelProbeTools
{
    private readonly TasksCancelProbeObservation _observation;

    public TasksCancelProbeTools(TasksCancelProbeObservation observation)
    {
        _observation = observation;
    }

    [McpServerTool(Name = "tasks_cancel_probe"), Description("Test probe: blocks until cancelled.")]
    public async Task<string> TasksCancelProbe(
        [Description("Seconds to sleep before completing.")] int seconds = 30,
        CancellationToken cancellationToken = default)
    {
        seconds = Math.Clamp(seconds, 1, 600);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
            return """{"ok":true}""";
        }
        catch (OperationCanceledException)
        {
            _observation.MarkCancelled();
            throw;
        }
    }
}
