using DotNetMcp.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

public static class ServerHost
{
    public static void AddDotNetMcp(
        IServiceCollection services,
        TrustedRoots trustedRoots,
        ISolutionLoader? solutionLoader = null,
        WorkspaceHostOptions? workspaceHostOptions = null,
        SoftBudgetOptions? softBudgetOptions = null)
    {
        services.AddSingleton(trustedRoots);
        services.AddSingleton<ISolutionLoader>(solutionLoader ?? new MsBuildSolutionLoader());
        services.AddSingleton(workspaceHostOptions ?? WorkspaceHostOptions.Default);
        services.AddSingleton(softBudgetOptions ?? SoftBudgetOptions.FromEnvironment());
        services.AddSingleton<WorkspaceHost>();
        services.AddSingleton<SymbolQueryService>();
        services.AddSingleton<DiagnosticQueryService>();
        services.AddSingleton<GeneratorQueryService>();
        services.AddSingleton<WorkspaceTools>();
        services.AddSingleton<SymbolTools>();
        services.AddSingleton<ProjectTools>();
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

        var taskStore = new InMemoryMcpTaskStore { DefaultPollIntervalMs = 250 };
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(ServerHost).Assembly)
            .WithTasks(taskStore);

        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }
}
