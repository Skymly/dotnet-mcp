using System.Diagnostics;

namespace S2.Core;

public sealed class ProcessMetrics : IDisposable
{
    private readonly Process _process;
    private long _peakWorkingSetBytes;
    private long _peakGcTotalMemory;

    private ProcessMetrics(Process process)
    {
        _process = process;
        Sample();
    }

    public static ProcessMetrics Start() => new(Process.GetCurrentProcess());

    public long PeakWorkingSetBytes => _peakWorkingSetBytes;
    public long PeakGcTotalMemory => _peakGcTotalMemory;

    public void Sample()
    {
        _process.Refresh();
        _peakWorkingSetBytes = Math.Max(_peakWorkingSetBytes, _process.WorkingSet64);
        _peakGcTotalMemory = Math.Max(_peakGcTotalMemory, GC.GetTotalMemory(forceFullCollection: false));
    }

    public void Dispose() => Sample();
}
