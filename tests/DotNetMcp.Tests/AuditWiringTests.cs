using DotNetMcp.Server;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetMcp.Tests;

public class AuditWiringTests
{
    [Fact]
    public void omitted_audit_options_follow_dotnet_mcp_audit_and_use_the_production_sink()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet-mcp-audit-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable(AuditOptions.EnvName);
        try
        {
            Environment.SetEnvironmentVariable(AuditOptions.EnvName, "0");
            using var disabled = Build(root);
            Assert.False(disabled.GetRequiredService<AuditOptions>().Enabled);
            Assert.IsType<LoggerAuditLogger>(disabled.GetRequiredService<IAuditLogger>());

            Environment.SetEnvironmentVariable(AuditOptions.EnvName, null);
            using var enabled = Build(root);
            Assert.True(enabled.GetRequiredService<AuditOptions>().Enabled);
            Assert.IsType<LoggerAuditLogger>(enabled.GetRequiredService<IAuditLogger>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(AuditOptions.EnvName, previous);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static ServiceProvider Build(string root)
    {
        var services = new ServiceCollection();
        ServerHost.AddDotNetMcp(services, TrustedRoots.Create([root]));
        return services.BuildServiceProvider();
    }
}
