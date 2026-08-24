using System.Diagnostics;

namespace DotNetMcp.Bench;

internal sealed class ProcessSampler : IDisposable
{
    private readonly Process _process;
    private long _peakWorkingSetBytes;

    private ProcessSampler(Process process)
    {
        _process = process;
        Sample();
    }

    public static ProcessSampler Start() => new(Process.GetCurrentProcess());

    public long PeakWorkingSetBytes => _peakWorkingSetBytes;

    public double PeakWorkingSetMiB => _peakWorkingSetBytes / (1024.0 * 1024.0);

    public void Sample()
    {
        _process.Refresh();
        _peakWorkingSetBytes = Math.Max(_peakWorkingSetBytes, _process.WorkingSet64);
    }

    public void Dispose() => Sample();
}

internal static class Statistics
{
    public static TimingStats From(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return new TimingStats();
        }

        var sorted = values.OrderBy(v => v).ToArray();
        return new TimingStats
        {
            Min = sorted[0],
            Max = sorted[^1],
            Mean = values.Average(),
            P50 = Percentile(sorted, 0.50),
            P95 = Percentile(sorted, 0.95),
        };
    }

    public static double Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (sortedAscending.Count == 0)
        {
            return 0;
        }

        var idx = (int)Math.Clamp(Math.Ceiling(p * sortedAscending.Count) - 1, 0, sortedAscending.Count - 1);
        return sortedAscending[idx];
    }
}
