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

    [Fact]
    public void utf8_without_bom_roundtrip_is_lossless()
    {
        var bytes = Encoding.UTF8.GetBytes("class C { int X() => 1; }\n");
        Assert.True(FileTextCodec.TryReadLossless(bytes, out var text, out var encoding));
        Assert.Equal("class C { int X() => 1; }\n", text);
        Assert.Empty(encoding.GetPreamble());
    }

    [Fact]
    public void utf8_bom_is_lossless_and_latin1_byte_is_not()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("hello")).ToArray();
        Assert.True(FileTextCodec.TryReadLossless(bom, out var text, out var encoding));
        Assert.Equal("hello", text);
        Assert.NotEmpty(encoding.GetPreamble());
        Assert.False(FileTextCodec.TryReadLossless(new byte[] { 0xE9 }, out _, out _));
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("hello")).ToArray();
        Assert.True(FileTextCodec.TryReadLossless(utf16, out var utf16Text, out var utf16Encoding));
        Assert.Equal("hello", utf16Text);
        Assert.Equal(0xFF, utf16Encoding.GetPreamble()[0]);
    }
}

public class FSharpCompileOrderTests
{
    [Fact]
    public async Task capture_uses_compile_include_order_not_directory_enumeration()
    {
        var dir = CreateOrderFixture();
        try
        {
            var loaded = LoadFsproj(dir);
            var snapshot = WorkspaceSession.CaptureFSharpSnapshot(loaded.Solution, epoch: 1, TrustedRoots.Create([dir]));
            using var session = new WorkspaceSession(loaded, epoch: 1, fsharpSnapshot: snapshot);
            var project = Assert.Single(session.FSharpSnapshot.Projects);
            Assert.Equal(new[] { "Zebra.fs", "Apple.fs" }, project.Documents.Select(d => Path.GetFileName(d.Path)).ToArray());
            Assert.Equal(
                new[] { "Zebra.fs", "Apple.fs" },
                FSharpProjectFile.ReadCompilePaths(Path.Combine(dir, "Lib.fsproj")).Select(Path.GetFileName).ToArray());
            Assert.Contains("CUSTOM", project.Defines);

            var (page, error) = await new DotNetMcp.FSharp.FSharpSymbolQueryService().GetProjectDiagnosticsAsync(session, project.ProjectId);
            Assert.Null(error);
            Assert.DoesNotContain(page!.Items, d => d.Id == "FS0039");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task reversed_compile_order_fails_fsharp_check_with_fs0039()
    {
        var dir = CreateOrderFixture();
        try
        {
            var loaded = LoadFsproj(dir);
            var snapshot = WorkspaceSession.CaptureFSharpSnapshot(loaded.Solution, epoch: 1, TrustedRoots.Create([dir]));
            var project = Assert.Single(snapshot.Projects);
            var reversed = new FSharpWorkspaceSnapshot(1, [
                new FSharpProjectSnapshot(
                    project.ProjectId,
                    project.Name,
                    project.FilePath,
                    project.Documents.Reverse().ToArray(),
                    project.Defines,
                    project.References)
            ]);
            using var session = new WorkspaceSession(loaded, epoch: 1, fsharpSnapshot: reversed);
            var (page, error) = await new DotNetMcp.FSharp.FSharpSymbolQueryService().GetProjectDiagnosticsAsync(session, project.ProjectId);
            Assert.Null(error);
            Assert.Contains(page!.Items, d => d.Id == "FS0039");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static string CreateOrderFixture()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fsord-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Apple.fs"), "module Apple\nlet ping () = Zebra.marker\n");
        File.WriteAllText(Path.Combine(dir, "Zebra.fs"), "module Zebra\nlet marker = 7\n");
        File.WriteAllText(Path.Combine(dir, "Lib.fsproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <DefineConstants>TRACE;CUSTOM</DefineConstants>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Zebra.fs" />
                <Compile Include="Apple.fs" />
              </ItemGroup>
            </Project>
            """);
        return dir;
    }

    private static LoadedSolution LoadFsproj(string dir)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Create(), "Lib", "Lib", LanguageNames.FSharp,
            filePath: Path.Combine(dir, "Lib.fsproj")));
        Assert.True(workspace.TryApplyChanges(solution));
        return new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}

public class FSharpMetadataReferenceCaptureTests
{
    [Fact]
    public void capture_copies_existing_metadata_reference_into_snapshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fsref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, "Pack.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "DotNetMcp.Core.dll"), dll);
        try
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var project = ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "Lib",
                    "Lib",
                    LanguageNames.FSharp,
                    filePath: Path.Combine(dir, "Lib.fsproj"))
                .WithMetadataReferences([MetadataReference.CreateFromFile(dll)]);
            Assert.True(workspace.TryApplyChanges(workspace.CurrentSolution.AddProject(project)));
            var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
            var snapshot = WorkspaceSession.CaptureFSharpSnapshot(loaded.Solution, epoch: 1, TrustedRoots.Create([dir]));
            var captured = Assert.Single(snapshot.Projects);
            Assert.Contains(
                captured.References,
                path => string.Equals(path, Path.GetFullPath(dll), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

public class FSharpSignatureFileTests
{
    [Fact]
    public void compile_include_order_places_fsi_before_fs_when_xml_says_so()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fsi-xml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Widget.fs"), "module Widget\nlet ping () = 1\n");
            File.WriteAllText(Path.Combine(dir, "Widget.fsi"), "module Widget\nval ping: unit -> int\n");
            File.WriteAllText(Path.Combine(dir, "Lib.fsproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Compile Include="Widget.fsi" />
                    <Compile Include="Widget.fs" />
                  </ItemGroup>
                </Project>
                """);

            var names = CaptureNames(dir);
            Assert.Equal(new[] { "Widget.fsi", "Widget.fs" }, names);
            Assert.Equal(
                new[] { "Widget.fsi", "Widget.fs" },
                FSharpProjectFile.ReadCompilePaths(Path.Combine(dir, "Lib.fsproj")).Select(Path.GetFileName).ToArray());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void enumeration_fallback_includes_fsi_after_fs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dotnet-mcp-fsi-enum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Widget.fs"), "module Widget\nlet ping () = 1\n");
            File.WriteAllText(Path.Combine(dir, "Widget.fsi"), "module Widget\nval ping: unit -> int\n");
            File.WriteAllText(Path.Combine(dir, "Lib.fsproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var names = CaptureNames(dir);
            Assert.Equal(new[] { "Widget.fs", "Widget.fsi" }, names);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static string[] CaptureNames(string dir)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Create(), "Lib", "Lib", LanguageNames.FSharp,
            filePath: Path.Combine(dir, "Lib.fsproj")));
        Assert.True(workspace.TryApplyChanges(solution));
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        var snapshot = WorkspaceSession.CaptureFSharpSnapshot(loaded.Solution, epoch: 1, TrustedRoots.Create([dir]));
        return Assert.Single(snapshot.Projects).Documents.Select(d => Path.GetFileName(d.Path)).ToArray();
    }
}