using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class FixtureTrustedRootsTests
{
    [Fact]
    public void fixture_requires_explicit_trusted_roots()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new InProcessMcpFixture());
        Assert.Contains("Trusted roots are required", ex.Message, StringComparison.Ordinal);
        Assert.Contains("working directory", ex.Message, StringComparison.Ordinal);
    }
}
