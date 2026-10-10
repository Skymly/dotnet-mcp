using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class PathOutsideTrustedRootsCopyTests
{
    [Fact]
    public async Task path_tools_share_one_outside_root_message_and_suggested_action()
    {
        var root = CreateTempDir();
        var outside = CreateTempDir();
        var secretPath = Path.Combine(outside, "secret.txt");
        await File.WriteAllTextAsync(secretPath, "TOP_SECRET_CONTENT");
        try
        {
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));
            var tools = new[]
            {
                "workspace_open",
                "xaml_resolve_class",
                "xaml_list_xmlns",
                "xaml_resolve_name",
                "xaml_resolve_binding",
                "xaml_diagnostics",
            };
            PolicyErrorDto? first = null;
            foreach (var tool in tools)
            {
                var result = await fx.Client.CallToolAsync(
                    tool,
                    new Dictionary<string, object?>
                    {
                        ["path"] = secretPath,
                        ["name"] = "Title",
                        ["bindingPath"] = "Name",
                    });
                Assert.True(result.IsError is true, tool);
                var body = InProcessMcpFixture.Deserialize<PolicyErrorDto>(result);
                Assert.Equal(PolicyErrorCodes.PathOutsideTrustedRoots, body.Error);
                Assert.Contains("No target content is returned.", body.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("TOP_SECRET_CONTENT", InProcessMcpFixture.TextOf(result));
                if (first is null)
                {
                    first = body;
                }
                else
                {
                    Assert.Equal(first.Message, body.Message);
                    Assert.Equal(first.SuggestedAction, body.SuggestedAction);
                }
            }
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-path-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}