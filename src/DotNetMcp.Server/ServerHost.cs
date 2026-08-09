using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

public static class ServerHost
{
    public static void AddDotNetMcp(
        IServiceCollection services,
        TrustedRoots trustedRoots,
        ISolutionLoader? solutionLoader = null)
    {
        services.AddSingleton(trustedRoots);
        services.AddSingleton<ISolutionLoader>(solutionLoader ?? new MsBuildSolutionLoader());
        services.AddSingleton<WorkspaceHost>();
        services.AddSingleton<DotNetMcp.Core.SymbolQueryService>();
        services.AddSingleton<WorkspaceTools>();
        services.AddSingleton<SymbolTools>();
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var trustedRoots = TrustedRoots.FromStartup(args);
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        AddDotNetMcp(builder.Services, trustedRoots);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(ServerHost).Assembly);

        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }
}
