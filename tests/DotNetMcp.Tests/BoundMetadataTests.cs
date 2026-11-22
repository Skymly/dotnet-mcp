using System.Diagnostics;
using System.Text.Json;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class BoundMetadataTests
{
    [Fact]
    public async Task inspect_uses_the_restored_asset_of_each_project_and_framework()
    {
        var root = Directory.CreateTempSubdirectory("dnmcp-metadata-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "feed"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "../../../../../global.json"), Path.Combine(root, "global.json"));
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
                <Project><PropertyGroup><RestorePackagesPath>$(MSBuildThisFileDirectory)packages</RestorePackagesPath></PropertyGroup></Project>
                """);
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), """
                <configuration><packageSources><clear/><add key="local" value="./feed"/></packageSources>
                <config><add key="globalPackagesFolder" value="./packages"/></config></configuration>
                """);
            await Pack(root, "Fixture.Contracts", 1, "int", withDocs: true);
            await Pack(root, "Fixture.Contracts", 2, "long", withDocs: false);
            await Pack(root, "Fixture.Other", 1, "bool", withDocs: false);
            var first = Directory.CreateDirectory(Path.Combine(root, "First")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(root, "Second")).FullName;
            File.WriteAllText(Path.Combine(first, "First.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>netstandard2.1;net10.0</TargetFrameworks></PropertyGroup>
                <ItemGroup><PackageReference Include="Fixture.Contracts" Version="1.0.0" Condition="'$(TargetFramework)'=='netstandard2.1'"/>
                <PackageReference Include="Fixture.Contracts" Version="2.0.0" Condition="'$(TargetFramework)'=='net10.0'"/></ItemGroup></Project>
                """);
            File.WriteAllText(Path.Combine(second, "Second.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                <ItemGroup><PackageReference Include="Fixture.Contracts" Version="2.0.0"/>
                <PackageReference Include="Fixture.Other" Version="1.0.0" Aliases="Other"/></ItemGroup></Project>
                """);
            const string code = "extern alias Other; class User { object Read() => Fixture.Api.Get(1); object ViaAlias() => Other::Fixture.Api.Get(1); }";
            File.WriteAllText(Path.Combine(second, "Use.cs"), code);
            var solution = Path.Combine(root, "Cases.slnx");
            File.WriteAllText(solution, """<Solution><Project Path="First/First.csproj"/><Project Path="Second/Second.csproj"/></Solution>""");
            await Dotnet(root, "restore", solution);
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));
            await WorkspaceReady.OpenUntilReadyAsync(fx, solution, WorkspaceReady.MsBuildTimeout);
            var listed = await fx.Client.CallToolAsync("workspace_list_projects");
            var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(listed).Projects;
            Assert.Equal(3, projects.Count);
            Assert.Single(projects, p => p.TargetFramework == "netstandard2.1");
            foreach (var project in projects)
            {
                var isFirst = project.FilePath!.EndsWith("First.csproj", StringComparison.Ordinal);
                var old = isFirst && project.TargetFramework == "netstandard2.1";
                var arguments = new Dictionary<string, object?>
                { ["projectId"] = project.ProjectId, ["typeName"] = "Fixture.Api", ["memberName"] = "Get" };
                var legacyHandle = DotNetMcp.Core.SymbolHandle.Create("csharp", project.ProjectId, "Fixture.Api.Get(int)").Format();
                var legacy = await fx.Client.CallToolAsync("symbol_goto_definition", new Dictionary<string, object?> { ["handle"] = legacyHandle });
                if (isFirst)
                    Assert.True(legacy.IsError is not true, InProcessMcpFixture.TextOf(legacy));
                else
                    Assert.Equal("SymbolAmbiguous", InProcessMcpFixture.Deserialize<PolicyErrorDto>(legacy).Error);
                if (!isFirst)
                {
                    var ambiguous = await fx.Client.CallToolAsync("project_inspect_metadata", arguments);
                    Assert.Equal("SymbolAmbiguous", InProcessMcpFixture.Deserialize<PolicyErrorDto>(ambiguous).Error);
                    var handles = new HashSet<string>();
                    foreach (var (column, assembly, resultType) in new[]
                    {
                        (code.IndexOf("Get", StringComparison.Ordinal) + 1, "Fixture.Contracts", "long"),
                        (code.LastIndexOf("Get", StringComparison.Ordinal) + 1, "Fixture.Other", "bool")
                    })
                    {
                        var resolved = await fx.Client.CallToolAsync("symbol_resolve_at", new Dictionary<string, object?>
                        {
                            ["path"] = Path.Combine(second, "Use.cs"),
                            ["line"] = 1,
                            ["column"] = column,
                            ["projectId"] = project.ProjectId
                        });
                        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
                        var hit = InProcessMcpFixture.Deserialize<SymbolResolveAtResultDto>(resolved);
                        var target = hit.Summary.Metadata;
                        Assert.NotNull(target);
                        Assert.StartsWith(assembly + ",", target.AssemblyIdentity);
                        Assert.True(handles.Add(hit.Handle), "Different bound assemblies must have distinct handles.");
                        var definition = await fx.Client.CallToolAsync("symbol_goto_definition", new Dictionary<string, object?>
                        {
                            ["handle"] = hit.Handle
                        });
                        Assert.True(definition.IsError is not true, InProcessMcpFixture.TextOf(definition));
                        var navigation = InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(definition).Metadata;
                        Assert.Equal(target, navigation);
                        Assert.Equal("project_inspect_metadata", navigation!.Tool);
                        var fromDefinition = await fx.Client.CallToolAsync(navigation.Tool, new Dictionary<string, object?>
                        {
                            ["projectId"] = navigation.ProjectId,
                            ["typeName"] = navigation.TypeName,
                            ["assemblyIdentity"] = navigation.AssemblyIdentity,
                            ["memberName"] = navigation.MemberName
                        });
                        Assert.True(fromDefinition.IsError is not true, InProcessMcpFixture.TextOf(fromDefinition));
                        using var body = JsonDocument.Parse(InProcessMcpFixture.TextOf(fromDefinition));
                        var members = body.RootElement.GetProperty("type").GetProperty("members").GetProperty("items");
                        Assert.All(members.EnumerateArray(), m => Assert.Contains(resultType + " ", m.GetProperty("signature").GetString()));
                        if (assembly == "Fixture.Contracts")
                            arguments["assemblyIdentity"] = target.AssemblyIdentity;
                    }
                }
                var inspection = await fx.Client.CallToolAsync("project_inspect_metadata", arguments);
                Assert.True(inspection.IsError is not true, InProcessMcpFixture.TextOf(inspection));
                using var json = JsonDocument.Parse(InProcessMcpFixture.TextOf(inspection));
                var type = json.RootElement.GetProperty("type");
                Assert.Contains($"Version={(old ? 1 : 2)}.0.0.0", type.GetProperty("assemblyIdentity").GetString());
                var overloads = type.GetProperty("members").GetProperty("items").EnumerateArray().ToArray();
                Assert.Equal(2, overloads.Length);
                Assert.Equal(2, overloads.Select(item => item.GetProperty("documentationId").GetString()).Distinct().Count());
                Assert.All(overloads, item =>
                {
                    Assert.Contains(old ? "int " : "long ", item.GetProperty("signature").GetString());
                    Assert.Equal(old, item.TryGetProperty("xml", out var xml));
                    if (old)
                        Assert.Contains("Reads the fixture", xml.GetString());
                });
            }
        }
        finally
        {
            CodeRefactoringSeamTests.TryDelete(root);
        }
    }

    private static async Task Pack(string root, string id, int version, string returnType, bool withDocs)
    {
        var folder = Directory.CreateDirectory(Path.Combine(root, id + version)).FullName;
        File.WriteAllText(Path.Combine(folder, id + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework>
            <Version>{version}.0.0</Version><GenerateDocumentationFile>{withDocs}</GenerateDocumentationFile></PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(folder, "Api.cs"), $$"""
            namespace Fixture {
            public static class Api {
                /// <summary>Reads the fixture by index.</summary>
                public static {{returnType}} Get(int index) => default;
                /// <summary>Reads the fixture by name.</summary>
                public static {{returnType}} Get(string name) => default;
            }
            }
            """);
        await Dotnet(folder, "pack", "-o", Path.Combine(root, "feed"));
    }

    private static async Task Dotnet(string folder, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet") { WorkingDirectory = folder, RedirectStandardOutput = true };
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in args)
            info.ArgumentList.Add(argument);
        info.ArgumentList.Add("--disable-build-servers");
        using var process = Process.Start(info)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, output);
    }
}
