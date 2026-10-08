using System.Text;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class SourceEncodingApplySeamTests
{
    [Fact]
    public async Task apply_refuses_no_bom_latin1_source_and_leaves_bytes_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet-mcp-enc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "App.csproj");
        var widget = Path.Combine(root, "Widget.cs");
        try
        {
            await File.WriteAllTextAsync(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <ImplicitUsings>disable</ImplicitUsings>
                    <Nullable>disable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            var latin1 = Encoding.ASCII.GetBytes("namespace App;\npublic static class Widget\n{\n    // caf")
                .Concat(new byte[] { 0xE9 })
                .Concat(Encoding.ASCII.GetBytes("\n    public static int Ping() => 1;\n}\n"))
                .ToArray();
            await File.WriteAllBytesAsync(widget, latin1);
            var before = await File.ReadAllBytesAsync(widget);

            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                solutionLoader: null);

            var open = await fx.Client.CallToolAsync(
                "workspace_open",
                new Dictionary<string, object?> { ["path"] = project });
            Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
            await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "App.Widget.Ping" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
            var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;

            var preview = await fx.Client.CallToolAsync(
                "symbol_preview_rename",
                new Dictionary<string, object?>
                {
                    ["handle"] = handle,
                    ["newName"] = "Pong"
                });
            Assert.True(preview.IsError is not true, InProcessMcpFixture.TextOf(preview));
            var previewBody = InProcessMcpFixture.Deserialize<SymbolPreviewRenameResultDto>(preview);
            Assert.Equal(before, await File.ReadAllBytesAsync(widget));

            var apply = await fx.Client.CallToolAsync(
                "symbol_apply_rename",
                new Dictionary<string, object?> { ["previewId"] = previewBody.PreviewId });
            Assert.True(apply.IsError is true, InProcessMcpFixture.TextOf(apply));
            var error = InProcessMcpFixture.Deserialize<PolicyErrorDto>(apply);
            Assert.Equal(PolicyErrorCodes.SourceEncodingRefused, error.Error);
            Assert.False(string.IsNullOrWhiteSpace(error.SuggestedAction));
            Assert.DoesNotContain("U+FFFD", error.Message, StringComparison.Ordinal);
            Assert.Equal(before, await File.ReadAllBytesAsync(widget));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
