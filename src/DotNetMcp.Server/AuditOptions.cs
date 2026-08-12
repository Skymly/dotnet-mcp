namespace DotNetMcp.Server;

/// <summary>
/// Local audit logging switch (ADR-0004 §5). Default on; disable via <c>DOTNET_MCP_AUDIT</c>.
/// </summary>
public sealed class AuditOptions
{
    public const string EnvName = "DOTNET_MCP_AUDIT";

    public static AuditOptions Default { get; } = new();

    /// <summary>When false, <see cref="IAuditLogger"/> implementations no-op.</summary>
    public bool Enabled { get; init; } = true;

    public static AuditOptions FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Build options from an env lookup. Missing/blank/unrecognized values keep audit enabled.
    /// Disable tokens: <c>0</c>, <c>false</c>, <c>off</c>, <c>no</c> (case-insensitive).
    /// </summary>
    public static AuditOptions FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var raw = getEnvironmentVariable(EnvName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Default;
        }

        var token = raw.Trim();
        var disabled =
            token.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("no", StringComparison.OrdinalIgnoreCase);

        return disabled ? new AuditOptions { Enabled = false } : Default;
    }
}
