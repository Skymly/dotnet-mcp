using System.Runtime.CompilerServices;

namespace S2.Core;

public static class SpikePaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string SpikeRoot { get; } = Path.Combine(RepoRoot, "spikes", "s2-scale");

    public static string MultiTfmProject { get; } =
        Path.Combine(SpikeRoot, "fixtures", "MultiTfm", "MultiTfm.csproj");

    public static string SampleSlnf { get; } =
        Path.Combine(SpikeRoot, "fixtures", "SampleFilter", "Sample.slnf");

    public static string SampleSlnx { get; } =
        Path.Combine(SpikeRoot, "fixtures", "SampleFilter", "Sample.slnx");

    public static string DataDir { get; } = Path.Combine(SpikeRoot, "data");

    public static string DefaultObservablesSlnx { get; } =
        Environment.GetEnvironmentVariable("OBSERVABLES_SLNX")
        ?? @"C:\Code\Skymly\Observables\Observables\Observables.slnx";

    private static string FindRepoRoot([CallerFilePath] string? thisFile = null)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "docs", "adr")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root from assembly path.");
    }
}
