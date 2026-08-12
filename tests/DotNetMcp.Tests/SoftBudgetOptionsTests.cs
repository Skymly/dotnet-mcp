using DotNetMcp.Core;

namespace DotNetMcp.Tests;

public class SoftBudgetOptionsTests
{
    [Fact]
    public void FromEnvironment_missing_values_use_adr_defaults()
    {
        var options = SoftBudgetOptions.FromEnvironment(_ => null);

        Assert.Equal(TimeSpan.FromSeconds(5), options.SingleProjectCompile);
        Assert.Equal(TimeSpan.FromSeconds(5), options.FindRefsScoped);
        Assert.Equal(TimeSpan.FromSeconds(20), options.FindRefsEntireSolution);
        Assert.Equal(TimeSpan.FromSeconds(15), options.BatchDiagnostics);
    }

    [Fact]
    public void FromEnvironment_parses_valid_milliseconds()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SoftBudgetOptions.SingleProjectCompileEnvName] = "1234",
            [SoftBudgetOptions.FindRefsScopedEnvName] = "0",
            [SoftBudgetOptions.FindRefsEntireSolutionEnvName] = "25000",
            [SoftBudgetOptions.BatchDiagnosticsEnvName] = "15000"
        };

        var options = SoftBudgetOptions.FromEnvironment(
            name => env.TryGetValue(name, out var value) ? value : null);

        Assert.Equal(TimeSpan.FromMilliseconds(1234), options.SingleProjectCompile);
        Assert.Equal(TimeSpan.Zero, options.FindRefsScoped);
        Assert.Equal(TimeSpan.FromMilliseconds(25000), options.FindRefsEntireSolution);
        Assert.Equal(TimeSpan.FromMilliseconds(15000), options.BatchDiagnostics);
    }

    [Fact]
    public void FromEnvironment_invalid_or_negative_falls_back_to_defaults()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SoftBudgetOptions.SingleProjectCompileEnvName] = "not-a-number",
            [SoftBudgetOptions.FindRefsScopedEnvName] = "-1",
            [SoftBudgetOptions.FindRefsEntireSolutionEnvName] = "  ",
            [SoftBudgetOptions.BatchDiagnosticsEnvName] = "3.5"
        };

        var options = SoftBudgetOptions.FromEnvironment(
            name => env.TryGetValue(name, out var value) ? value : null);

        Assert.Equal(SoftBudgetOptions.Default.SingleProjectCompile, options.SingleProjectCompile);
        Assert.Equal(SoftBudgetOptions.Default.FindRefsScoped, options.FindRefsScoped);
        Assert.Equal(SoftBudgetOptions.Default.FindRefsEntireSolution, options.FindRefsEntireSolution);
        Assert.Equal(SoftBudgetOptions.Default.BatchDiagnostics, options.BatchDiagnostics);
    }
}
