using System.Globalization;

namespace DotNetMcp.Bench;

internal sealed class BenchOptions
{
    public string Suite { get; init; } = "fixtures";
    public int Iterations { get; init; } = 5;
    public int Warmup { get; init; } = 1;
    public string? Filter { get; init; }
    public string OutDir { get; init; } = "";
    public bool Cold { get; init; }
    public bool AllowWrites { get; init; }
    public bool NoGates { get; init; }
    public bool JsonOnly { get; init; }
    public string? SolutionPath { get; init; }
    public string? SymbolName { get; init; }
    public int SyntheticProjects { get; init; } = 20;
    public int SyntheticFiles { get; init; } = 8;
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public static BenchOptions Parse(string[] args)
    {
        var suite = "fixtures";
        var iterations = 5;
        var warmup = 1;
        string? filter = null;
        var outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "data"));
        var cold = false;
        var allowWrites = false;
        var noGates = false;
        var jsonOnly = false;
        string? solution = Environment.GetEnvironmentVariable("DOTNET_MCP_BENCH_SOLUTION");
        string? symbol = Environment.GetEnvironmentVariable("DOTNET_MCP_BENCH_SYMBOL");
        var projects = 20;
        var files = 8;
        var readySeconds = 180;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    return new BenchOptions { Suite = "help" };
                case "--suite" when i + 1 < args.Length:
                    suite = args[++i];
                    break;
                case "--iterations" when i + 1 < args.Length:
                    iterations = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--warmup" when i + 1 < args.Length:
                    warmup = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--filter" when i + 1 < args.Length:
                    filter = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    outDir = Path.GetFullPath(args[++i]);
                    break;
                case "--solution" when i + 1 < args.Length:
                    solution = args[++i];
                    break;
                case "--symbol" when i + 1 < args.Length:
                    symbol = args[++i];
                    break;
                case "--projects" when i + 1 < args.Length:
                    projects = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--files" when i + 1 < args.Length:
                    files = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--ready-timeout-s" when i + 1 < args.Length:
                    readySeconds = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--cold":
                    cold = true;
                    break;
                case "--allow-writes":
                    allowWrites = true;
                    break;
                case "--no-gates":
                    noGates = true;
                    break;
                case "--json-only":
                    jsonOnly = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (iterations < 1)
        {
            throw new ArgumentException("--iterations must be >= 1");
        }

        if (warmup < 0)
        {
            throw new ArgumentException("--warmup must be >= 0");
        }

        return new BenchOptions
        {
            Suite = suite.Trim().ToLowerInvariant(),
            Iterations = iterations,
            Warmup = warmup,
            Filter = filter,
            OutDir = outDir,
            Cold = cold,
            AllowWrites = allowWrites,
            NoGates = noGates,
            JsonOnly = jsonOnly,
            SolutionPath = solution,
            SymbolName = symbol,
            SyntheticProjects = Math.Max(2, projects),
            SyntheticFiles = Math.Max(1, files),
            ReadyTimeout = TimeSpan.FromSeconds(Math.Max(10, readySeconds)),
        };
    }

    public static string Usage =>
        """
        Product benchmark harness for dotnet-mcp (see docs/perf/benchmark.md).

        Usage:
          dotnet run --project benches/DotNetMcp.Bench -c Release -- [options]

        Options:
          --suite fixtures|synthetic|scale|smoke|all|help
          --iterations <n>          Measured iterations per scenario (default 5)
          --warmup <n>              Discarded warmup calls (default 1)
          --filter <substring>      Scenario id contains
          --out <dir>               JSON report directory
          --solution <path>         Scale workspace (.sln/.slnx/.slnf/.csproj)
          --symbol <name>           Scale symbol_resolve name
          --projects <n>            Synthetic project count (default 20)
          --files <n>               Files per synthetic project (default 8)
          --ready-timeout-s <n>     workspace_status poll budget (default 180)
          --cold                    Delete bin/obj under the workspace root first
          --allow-writes            Also measure apply_* Workspace Edit
          --no-gates                Do not fail the process on budget gates
          --json-only               Suppress console table
          --help
        """;
}
