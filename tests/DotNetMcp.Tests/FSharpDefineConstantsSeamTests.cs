using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class FSharpDefineConstantsSeamTests
{
    [Fact]
    public async Task evaluated_debug_define_keeps_debug_symbol_and_drops_not_debug_errors()
    {
        var root = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fsdef-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "App.fsproj");
        var source = Path.Combine(root, "Widget.fs");
        try
        {
            await File.WriteAllTextAsync(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <DefineConstants>$(DefineConstants);CUSTOM</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Widget.fs" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(source, """
                module App.Widget

                let always () = 1

                #if DEBUG
                let debugOnly () = 2
                #endif

                #if !DEBUG
                let notDebug () = missingIdentifier
                #endif
                """);

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
                new Dictionary<string, object?> { ["name"] = "App.Widget.debugOnly" });
            Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));

            var listed = await fx.Client.CallToolAsync(
                "workspace_list_projects",
                new Dictionary<string, object?>());
            var projectId = Assert.Single(
                InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(listed).Projects).ProjectId;
            var diagnostics = await fx.Client.CallToolAsync(
                "project_diagnostics",
                new Dictionary<string, object?> { ["projectId"] = projectId });
            Assert.True(diagnostics.IsError is not true, InProcessMcpFixture.TextOf(diagnostics));
            var body = InProcessMcpFixture.Deserialize<ProjectDiagnosticsResultDto>(diagnostics);
            Assert.DoesNotContain(body.Items, i =>
                (i.Message ?? "").Contains("missingIdentifier", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void split_defines_drops_unevaluated_msbuild_properties()
    {
        var defines = FSharpProjectFile.SplitDefines("$(DefineConstants);CUSTOM;DEBUG");
        Assert.Equal(new[] { "CUSTOM", "DEBUG" }, defines);
        Assert.DoesNotContain(defines, d => d.Contains('$', StringComparison.Ordinal));
    }
}
