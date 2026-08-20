using System.Globalization;

namespace DotNetMcp.Core;

/// <summary>
/// Soft time budgets for list/scan tools (ADR-0003 §3). Defaults match the S2 table;
/// override via env (<c>DOTNET_MCP_BUDGET_*_MS</c>) or host injection without recompiling.
/// </summary>
public sealed class SoftBudgetOptions
{
    public const string SingleProjectCompileEnvName = "DOTNET_MCP_BUDGET_SINGLE_PROJECT_MS";
    public const string FindRefsScopedEnvName = "DOTNET_MCP_BUDGET_FIND_REFS_SCOPED_MS";
    public const string FindRefsEntireSolutionEnvName = "DOTNET_MCP_BUDGET_FIND_REFS_ENTIRE_MS";
    public const string BatchDiagnosticsEnvName = "DOTNET_MCP_BUDGET_BATCH_DIAGNOSTICS_MS";
    public const string FixAllProjectEnvName = "DOTNET_MCP_BUDGET_FIXALL_PROJECT_MS";

    public static SoftBudgetOptions Default { get; } = new();

    /// <summary>Single-project compile / go-to-definition. ADR default: 5s.</summary>
    public TimeSpan SingleProjectCompile { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Find references within dependency closure. ADR default: 5s.</summary>
    public TimeSpan FindRefsScoped { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Find references across the entire solution. ADR default: 20s.</summary>
    public TimeSpan FindRefsEntireSolution { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Batch diagnostics (multi-project). ADR default: 15s. Used when project_diagnostics omits projectId.</summary>
    public TimeSpan BatchDiagnostics { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Project-scope Fix all preview. ADR default: 15s. Over-budget fails the whole preview.</summary>
    public TimeSpan FixAllProject { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Hard cap on CodeFix applications in one project-scope preview.</summary>
    public int FixAllProjectMaxApplications { get; init; } = 64;

    public static SoftBudgetOptions FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Build options from an env lookup. Missing or non-integer / negative values keep ADR defaults.
    /// Values are milliseconds.
    /// </summary>
    public static SoftBudgetOptions FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        return new SoftBudgetOptions
        {
            SingleProjectCompile = ParseMilliseconds(
                getEnvironmentVariable(SingleProjectCompileEnvName),
                Default.SingleProjectCompile),
            FindRefsScoped = ParseMilliseconds(
                getEnvironmentVariable(FindRefsScopedEnvName),
                Default.FindRefsScoped),
            FindRefsEntireSolution = ParseMilliseconds(
                getEnvironmentVariable(FindRefsEntireSolutionEnvName),
                Default.FindRefsEntireSolution),
            BatchDiagnostics = ParseMilliseconds(
                getEnvironmentVariable(BatchDiagnosticsEnvName),
                Default.BatchDiagnostics),
            FixAllProject = ParseMilliseconds(
                getEnvironmentVariable(FixAllProjectEnvName),
                Default.FixAllProject)
        };
    }

    private static TimeSpan ParseMilliseconds(string? raw, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ||
            ms < 0)
        {
            return fallback;
        }

        return TimeSpan.FromMilliseconds(ms);
    }
}
