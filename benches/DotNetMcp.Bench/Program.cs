using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotNetMcp.Bench;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        BenchOptions options;
        try
        {
            options = BenchOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(BenchOptions.Usage);
            return 2;
        }

        if (options.Suite is "help")
        {
            Console.WriteLine(BenchOptions.Usage);
            return 0;
        }

        if (options.Suite is "smoke")
        {
            options = new BenchOptions
            {
                Suite = "smoke",
                Iterations = 1,
                Warmup = 0,
                Filter = options.Filter,
                OutDir = options.OutDir,
                Cold = options.Cold,
                AllowWrites = false,
                NoGates = options.NoGates,
                JsonOnly = options.JsonOnly,
                SyntheticProjects = 2,
                SyntheticFiles = 2,
                ReadyTimeout = options.ReadyTimeout,
            };
        }

        Directory.CreateDirectory(options.OutDir);
        using var sampler = ProcessSampler.Start();
        var report = new BenchReport
        {
            TimestampUtc = DateTime.UtcNow,
            Suite = options.Suite,
            Environment = new BenchEnvironment
            {
                Os = RuntimeInformation.OSDescription,
                Framework = RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                MachineName = Environment.MachineName,
                WorkingSetMiBAtStart = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024),
            },
            Options = new BenchOptionsSnapshot
            {
                Suite = options.Suite,
                Iterations = options.Iterations,
                Warmup = options.Warmup,
                Filter = options.Filter,
                Cold = options.Cold,
                AllowWrites = options.AllowWrites,
                SolutionPath = options.SolutionPath,
                SymbolName = options.SymbolName,
                SyntheticProjects = options.Suite is "synthetic" or "smoke" or "all" ? options.SyntheticProjects : null,
                SyntheticFiles = options.Suite is "synthetic" or "smoke" or "all" ? options.SyntheticFiles : null,
            },
        };

        var runner = new ScenarioRunner(options, report, sampler);
        if (!options.JsonOnly)
        {
            Console.WriteLine($"suite={options.Suite} iterations={options.Iterations} warmup={options.Warmup}");
            Console.WriteLine($"out={options.OutDir}");
        }

        try
        {
            switch (options.Suite)
            {
                case "fixtures":
                    await Suites.RunFixturesAsync(runner).ConfigureAwait(false);
                    break;
                case "synthetic":
                    await Suites.RunSyntheticAsync(runner, options).ConfigureAwait(false);
                    break;
                case "scale":
                    await Suites.RunScaleAsync(runner, options).ConfigureAwait(false);
                    break;
                case "smoke":
                    await Suites.RunSmokeAsync(runner, options).ConfigureAwait(false);
                    break;
                case "all":
                    await Suites.RunFixturesAsync(runner).ConfigureAwait(false);
                    await Suites.RunSyntheticAsync(runner, options).ConfigureAwait(false);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown suite '{options.Suite}'.");
                    Console.Error.WriteLine(BenchOptions.Usage);
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            report.Gates.Add(new GateReport
            {
                Id = "harness",
                Status = "fail",
                Message = ex.Message,
            });
        }

        runner.EvaluateGates();
        runner.PrintTable();
        await runner.WriteAsync().ConfigureAwait(false);
        return runner.ExitCode();
    }
}
