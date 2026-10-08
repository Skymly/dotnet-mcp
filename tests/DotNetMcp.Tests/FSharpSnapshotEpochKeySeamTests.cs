using DotNetMcp.FSharp;

namespace DotNetMcp.Tests;

public class FSharpSnapshotEpochKeySeamTests
{
    private const string PathA = @"C:\fake-fs-epoch\A\A.fs";
    private const string PathB = @"C:\fake-fs-epoch\B\B.fs";

    [Fact]
    public void publishing_one_project_does_not_drop_another_projects_text()
    {
        var service = new FSharpSymbolQueryService();
        service.PublishSnapshots(1, [(PathA, "alpha")]);
        service.PublishSnapshots(1, [(PathB, "beta")]);

        Assert.True(service.TryGetSnapshot(1, PathA, out _, out var text));
        Assert.Equal("alpha", text);
        Assert.Equal("beta", Read(service, 1, PathB));
    }

    [Fact]
    public void newer_epoch_does_not_replace_an_in_flight_epoch()
    {
        var service = new FSharpSymbolQueryService();
        service.EnterEpochForTests(1);
        try
        {
            service.PublishSnapshots(1, [(PathA, "v1")]);
            service.PublishSnapshots(2, [(PathA, "v2"), (PathB, "other")]);

            Assert.Equal("v1", Read(service, 1, PathA));
            Assert.Equal("v2", Read(service, 2, PathA));
            Assert.False(service.TryGetSnapshot(1, PathB, out _, out _));
        }
        finally
        {
            service.ExitEpochForTests(1);
        }
    }

    private static string Read(FSharpSymbolQueryService service, long epoch, string path)
    {
        Assert.True(service.TryGetSnapshot(epoch, path, out _, out var text));
        return text;
    }
}
