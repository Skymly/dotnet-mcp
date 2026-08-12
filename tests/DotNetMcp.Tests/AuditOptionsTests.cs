using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class AuditOptionsTests
{
    [Fact]
    public void FromEnvironment_missing_value_defaults_to_enabled()
    {
        var options = AuditOptions.FromEnvironment(_ => null);

        Assert.True(options.Enabled);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("off")]
    [InlineData("no")]
    public void FromEnvironment_disable_tokens_set_enabled_false(string raw)
    {
        var options = AuditOptions.FromEnvironment(
            name => name == AuditOptions.EnvName ? raw : null);

        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("on")]
    [InlineData("maybe")]
    [InlineData("  ")]
    public void FromEnvironment_other_values_keep_enabled(string raw)
    {
        var options = AuditOptions.FromEnvironment(
            name => name == AuditOptions.EnvName ? raw : null);

        Assert.True(options.Enabled);
    }
}
