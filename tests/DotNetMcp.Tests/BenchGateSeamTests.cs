using DotNetMcp.Bench;

namespace DotNetMcp.Tests;

public class BenchGateSeamTests
{
    [Fact]
    public void empty_scenario_set_fails_every_gate()
    {
        var runner = CreateRunner();
        runner.EvaluateGates();

        Assert.Equal(1, runner.ExitCode());
        Assert.Equal(4, runner.Report.Gates.Count);
        Assert.All(runner.Report.Gates, gate => Assert.Equal("fail", gate.Status));
        Assert.Contains(runner.Report.Gates, gate => gate.Message.Contains("No scenarios ran", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("missing")]
    public void blocked_workspace_open_fails_gates(string phase)
    {
        var runner = CreateRunner();
        runner.Report.Workspaces.Add(new WorkspaceReport
        {
            Name = "Sample",
            Path = "missing.slnx",
            Phase = phase,
            Error = "open blocked",
        });

        runner.EvaluateGates();

        Assert.Equal(1, runner.ExitCode());
        Assert.All(runner.Report.Gates, gate => Assert.Equal("fail", gate.Status));
        Assert.Contains(runner.Report.Gates, gate => gate.Message.Contains(phase, StringComparison.Ordinal));
    }

    [Fact]
    public void required_scenario_under_budget_passes_gates()
    {
        var runner = CreateRunner();
        runner.Report.Workspaces.Add(new WorkspaceReport
        {
            Name = "Sample",
            Path = "Sample.slnx",
            Phase = "ready",
        });
        runner.Report.Scenarios.Add(new ScenarioReport
        {
            Id = "Sample.workspace.open.return",
            Tool = "workspace_open",
            Group = "workspace",
            Workspace = "Sample",
            Required = true,
            BudgetClass = "open-return",
            BudgetMs = 500,
            Iterations = 1,
            ElapsedMs = new TimingStats { Min = 1, Mean = 1, P50 = 1, P95 = 10, Max = 10 },
            BudgetStatus = "pass",
        });

        runner.EvaluateGates();

        Assert.Equal(0, runner.ExitCode());
        Assert.Equal(4, runner.Report.Gates.Count);
        Assert.DoesNotContain(runner.Report.Gates, gate => gate.Status == "fail");
    }

    [Fact]
    public void open_ready_over_60s_does_not_fail_client_timeout_gate()
    {
        var runner = RunnerWith(BudgetClass.OpenReady, p95: 90_000, budgetMs: 120_000, id: "Sample.workspace.open.ready");
        runner.EvaluateGates();

        var timeout = Assert.Single(runner.Report.Gates, gate => gate.Id == "under-client-timeout");
        Assert.Equal("pass", timeout.Status);
    }

    [Fact]
    public void single_call_over_60s_fails_client_timeout_gate()
    {
        var runner = RunnerWith(BudgetClass.SingleProject, p95: 60_000, budgetMs: 120_000, id: "Sample.symbol.resolve.warm");
        runner.EvaluateGates();

        var timeout = Assert.Single(runner.Report.Gates, gate => gate.Id == "under-client-timeout");
        Assert.Equal("fail", timeout.Status);
    }

    private static ScenarioRunner RunnerWith(string budgetClass, double p95, double budgetMs, string id)
    {
        var runner = CreateRunner();
        runner.Report.Workspaces.Add(new WorkspaceReport
        {
            Name = "Sample",
            Path = "Sample.slnx",
            Phase = "ready",
        });
        runner.Report.Scenarios.Add(new ScenarioReport
        {
            Id = "Sample.workspace.open.return",
            Tool = "workspace_open",
            Group = "workspace",
            Workspace = "Sample",
            Required = true,
            BudgetClass = BudgetClass.OpenReturn,
            BudgetMs = 500,
            Iterations = 1,
            ElapsedMs = new TimingStats { Min = 1, Mean = 1, P50 = 1, P95 = 10, Max = 10 },
            BudgetStatus = "pass",
        });
        runner.Report.Scenarios.Add(new ScenarioReport
        {
            Id = id,
            Tool = "workspace_status",
            Group = "workspace",
            Workspace = "Sample",
            Required = true,
            BudgetClass = budgetClass,
            BudgetMs = budgetMs,
            Iterations = 1,
            ElapsedMs = new TimingStats { Min = p95, Mean = p95, P50 = p95, P95 = p95, Max = p95 },
            BudgetStatus = "pass",
        });
        return runner;
    }
    private static ScenarioRunner CreateRunner()
    {
        var options = new BenchOptions { Suite = "smoke", JsonOnly = true, OutDir = Path.GetTempPath() };
        var report = new BenchReport
        {
            TimestampUtc = DateTime.UtcNow,
            Suite = options.Suite,
            Environment = new BenchEnvironment
            {
                Os = "test",
                Framework = "test",
                ProcessorCount = 1,
                MachineName = "test",
                WorkingSetMiBAtStart = 1,
            },
            Options = new BenchOptionsSnapshot
            {
                Suite = options.Suite,
                Iterations = 1,
                Warmup = 0,
                Cold = false,
            },
        };
        return new ScenarioRunner(options, report, ProcessSampler.Start());
    }
}
