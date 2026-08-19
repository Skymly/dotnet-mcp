using System.Diagnostics;
using System.Text.Json;
using DotNetMcp.Server;
using Xunit.Abstractions;

namespace S4.Verification;

public sealed class Q5_CostTests
{
    private readonly ITestOutputHelper _output;

    public Q5_CostTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task small_multi_file_rename_is_single_shot_without_soft_budget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s4-q5-" + Guid.NewGuid().ToString("N"));
        try
        {
            RenameWorkspace.CopyRenameApp(dir);
            var loaded = RenameWorkspace.LoadHandwritten(dir);
            var session = new WorkspaceSession(loaded, epoch: 1);
            var method = await RenameWorkspace.RequireMethodAsync(session, "RenameApp.Widget", "Ping");

            // Warm compilation so the measured rename is not first-compile noise.
            _ = await session.GetCompilationAsync(session.Solution.Projects.Single().Id);

            var samples = new List<double>();
            IReadOnlyList<TextSlice> last = [];
            for (var i = 0; i < 5; i++)
            {
                var (_, slices, elapsed) = await RenameWorkspace.PreviewRenameAsync(
                    session.Solution,
                    method,
                    "Pong");
                samples.Add(elapsed.TotalMilliseconds);
                last = slices;
            }

            samples.Sort();
            var median = samples[samples.Count / 2];
            var payload = new
            {
                samplesMs = samples,
                medianMs = median,
                documentCount = last.Count,
                files = last.Select(s => Path.GetFileName(s.Path)).ToArray()
            };
            Observation.WriteJson("rename-cost.json", payload);
            _output.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

            Assert.Equal(2, last.Count);
            Assert.True(median < 5_000, $"median {median}ms should be well under the 5s scoped-query soft budget");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
