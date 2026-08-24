using System.Text.Json.Serialization;

namespace DotNetMcp.Bench;

internal sealed class BenchReport
{
    public required DateTime TimestampUtc { get; init; }
    public required string Suite { get; init; }
    public required BenchEnvironment Environment { get; init; }
    public required BenchOptionsSnapshot Options { get; init; }
    public List<WorkspaceReport> Workspaces { get; } = [];
    public List<ScenarioReport> Scenarios { get; } = [];
    public List<GateReport> Gates { get; } = [];
}

internal sealed class BenchEnvironment
{
    public required string Os { get; init; }
    public required string Framework { get; init; }
    public required int ProcessorCount { get; init; }
    public required string MachineName { get; init; }
    public required long WorkingSetMiBAtStart { get; init; }
}

internal sealed class BenchOptionsSnapshot
{
    public required string Suite { get; init; }
    public required int Iterations { get; init; }
    public required int Warmup { get; init; }
    public string? Filter { get; init; }
    public required bool Cold { get; init; }
    public required bool AllowWrites { get; init; }
    public string? SolutionPath { get; init; }
    public string? SymbolName { get; init; }
    public int? SyntheticProjects { get; init; }
    public int? SyntheticFiles { get; init; }
}

internal sealed class WorkspaceReport
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Phase { get; init; }
    public int ProjectCount { get; init; }
    public double OpenReturnMs { get; init; }
    public double ReadyMs { get; init; }
    public string? Error { get; init; }
}

internal sealed class ScenarioReport
{
    public required string Id { get; init; }
    public required string Tool { get; init; }
    public required string Group { get; init; }
    public required string Workspace { get; init; }
    public required bool Required { get; init; }
    public required string BudgetClass { get; init; }
    public required double BudgetMs { get; init; }
    public required int Iterations { get; init; }
    public required TimingStats ElapsedMs { get; init; }
    public TimingStats? PayloadBytes { get; init; }
    public TimingStats? AllocatedBytes { get; init; }
    public double PeakWorkingSetMiB { get; init; }
    public int? ResultCount { get; init; }
    public bool Truncated { get; init; }
    public bool HasNextCursor { get; init; }
    public string? Error { get; init; }
    public required string BudgetStatus { get; init; }
}

internal sealed class TimingStats
{
    public double Min { get; init; }
    public double Mean { get; init; }
    public double P50 { get; init; }
    public double P95 { get; init; }
    public double Max { get; init; }
}

internal sealed class GateReport
{
    public required string Id { get; init; }
    public required string Status { get; init; }
    public required string Message { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BenchReport))]
internal partial class BenchJsonContext : JsonSerializerContext;
