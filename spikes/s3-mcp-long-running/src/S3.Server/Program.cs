using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Extensions.Tasks;
using S3.Core;
using S3.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<SlowJobStore>();
builder.Services.AddSingleton<ObservationLog>();
builder.Services.AddSingleton<SpikeTools>();

var store = new InMemoryMcpTaskStore { DefaultPollIntervalMs = 250 };

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SpikeTools>()
    .WithTasks(store);

await builder.Build().RunAsync();
