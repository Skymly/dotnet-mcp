using System.Text;
using DotNetMcp.Core;
using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class FileTextCodecTests
{
    [Fact]
    public void utf8_bom_roundtrip_keeps_preamble()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-enc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "a.cs");
        try
        {
            var bomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(path, "hello", bomUtf8);
            var (text, encoding) = FileTextCodec.Read(path);
            Assert.Equal("hello", text);
            Assert.True(encoding.GetPreamble().Length > 0);
            FileTextCodec.Write(path, "hello-new", encoding);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

public class FSharpCompileOrderTests
{
    [Fact]
    public void capture_uses_compile_include_order_not_directory_enumeration()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fsord-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Use.fs"), "module Use\nlet ping () = Types.Marker.Value\n");
            File.WriteAllText(Path.Combine(dir, "Types.fs"), "module Types\ntype Marker = { Value: int }\n");
            File.WriteAllText(Path.Combine(dir, "Lib.fsproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <DefineConstants>TRACE;CUSTOM</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Types.fs" />
                    <Compile Include="Use.fs" />
                  </ItemGroup>
                </Project>
                """);

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
                projectId, VersionStamp.Create(), "Lib", "Lib", LanguageNames.FSharp,
                filePath: Path.Combine(dir, "Lib.fsproj")));
            Assert.True(workspace.TryApplyChanges(solution));
            var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
            using var session = new WorkspaceSession(loaded, epoch: 1);
            var project = Assert.Single(session.FSharpSnapshot.Projects);
            Assert.Equal(new[] { "Types.fs", "Use.fs" }, project.Documents.Select(d => Path.GetFileName(d.Path)).ToArray());
            Assert.Contains("CUSTOM", project.Defines);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
