using System.Diagnostics;

namespace DotNetMcp.Bench;

internal sealed class ProcessSampler : IDisposable
{
    private readonly Func<long> _readWorkingSet;
    private long _runPeakBytes;
    private long _windowPeakBytes;

    private ProcessSampler(Func<long> readWorkingSet)
    {
        _readWorkingSet = readWorkingSet;
        Sample();
    }

    public static ProcessSampler Start() => new(ReadProcessWorkingSet);

    internal static ProcessSampler ForTest(Func<long> readWorkingSet) => new(readWorkingSet);

    public double PeakWorkingSetMiB => _windowPeakBytes / (1024.0 * 1024.0);

    public double RunPeakWorkingSetMiB => _runPeakBytes / (1024.0 * 1024.0);

    public void BeginScenario()
    {
        _windowPeakBytes = 0;
        Sample();
    }

    public void Sample()
    {
        var current = _readWorkingSet();
        _runPeakBytes = Math.Max(_runPeakBytes, current);
        _windowPeakBytes = Math.Max(_windowPeakBytes, current);
    }

    public void Dispose() => Sample();

    private static long ReadProcessWorkingSet()
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.WorkingSet64;
    }
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
