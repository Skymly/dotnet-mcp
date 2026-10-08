using DotNetMcp.Bench;

namespace DotNetMcp.Tests;

public class BenchWorkingSetSeamTests
{
    [Fact]
    public void scenario_peak_does_not_keep_an_earlier_run_peak()
    {
        var reads = new Queue<long>(
        [
            1000L * 1024 * 1024,
            40L * 1024 * 1024,
            80L * 1024 * 1024,
        ]);
        var sampler = ProcessSampler.ForTest(() => reads.Dequeue());

        Assert.Equal(1000, sampler.RunPeakWorkingSetMiB, precision: 1);
        Assert.Equal(1000, sampler.PeakWorkingSetMiB, precision: 1);

        sampler.BeginScenario();
        Assert.Equal(40, sampler.PeakWorkingSetMiB, precision: 1);

        sampler.Sample();

        Assert.Equal(80, sampler.PeakWorkingSetMiB, precision: 1);
        Assert.Equal(1000, sampler.RunPeakWorkingSetMiB, precision: 1);
    }
}
