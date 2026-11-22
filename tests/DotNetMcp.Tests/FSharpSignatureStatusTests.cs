using DotNetMcp.Core;
using DotNetMcp.FSharp;
using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class FSharpSignatureStatusTests
{
    private const string Signature = """
        module Example.Api
        val visible: int -> int
        module One =
            val same: int
        module Two =
            val exposed: int
        """;

    private const string Implementation = """
        module Example.Api
        let visible (x: int) = x
        module One =
            let same = 1
        module Two =
            let exposed = 2
            let same = 3
        let private secret = 4
        """;

    private readonly string _path = Path.Combine(Path.GetTempPath(), "signature-" + Guid.NewGuid().ToString("N"), "Api.fs");

    private FSharpWorkspaceSnapshot Snapshot(string impl, string? sig)
    {
        List<FSharpDocumentSnapshot> files = [];
        if (sig is not null)
        {
            files.Add(new(Path.ChangeExtension(_path, ".fsi"), sig));
        }
        files.Add(new(_path, impl));
        return new(1, [new("lib", "Lib", Path.ChangeExtension(_path, ".fsproj"), files)]);
    }

    [Fact]
    public async Task hidden_sibling_with_same_leaf_name_has_a_located_implementation_and_no_error()
    {
        var (result, error) = await new FSharpSymbolQueryService().SignatureStatusAsync(
            Snapshot(Implementation, Signature), "lib", _path, default);
        Assert.Null(error);
        Assert.Empty(result!.CompilerErrors);
        Assert.Equal("conforms", result.Status);
        var hidden = Assert.Single(result.HiddenBySignature);
        Assert.Equal("same", hidden.Name);
        Assert.Contains("Two", hidden.Signature);
        Assert.Equal(_path, hidden.Location.FilePath);
        Assert.Equal(7, hidden.Location.Line);
        Assert.Equal(9, hidden.Location.Column);
    }

    [Fact]
    public async Task distinguishes_hidden_overload_from_its_exposed_namesake()
    {
        const string contract = """
            namespace Example
            type Box =
                new: unit -> Box
                member Run: int -> int
            """;
        const string implementation = """
            namespace Example
            type Box() =
                member _.Run(value: int) = value
                member _.Run(value: string) = value
            """;
        var (result, _) = await new FSharpSymbolQueryService().SignatureStatusAsync(
            Snapshot(implementation, contract), "lib", _path, default);
        Assert.Empty(result!.CompilerErrors);
        var hidden = Assert.Single(result.HiddenBySignature);
        Assert.Equal("Run", hidden.Name);
        Assert.Equal(4, hidden.Location.Line);
        Assert.Contains("System.String", hidden.Signature);
    }

    [Fact]
    public async Task distinguishes_an_unrelated_project_error_from_a_signature_error()
    {
        var project = Assert.Single(Snapshot(Implementation, Signature).Projects);
        var other = Path.ChangeExtension(_path, "Other.fs");
        var snapshot = new FSharpWorkspaceSnapshot(2, [new("lib", "Lib", project.FilePath,
            [.. project.Documents, new(other, "module Example.Other\nlet broken: int = true")])]);
        var (result, _) = await new FSharpSymbolQueryService().SignatureStatusAsync(snapshot, "lib", _path, default);
        Assert.Equal("projectCheckErrors", result!.Status);
        Assert.Equal(other, Assert.Single(result.CompilerErrors).FilePath);
        Assert.Empty(result.HiddenBySignature);
    }

    [Theory]
    [InlineData("val missing: int")]
    [InlineData("val visible: string")]
    public async Task incompatible_and_missing_implementations_return_located_compiler_errors(string contract)
    {
        var (result, error) = await new FSharpSymbolQueryService().SignatureStatusAsync(
            Snapshot("module Example.Api\nlet visible = 1", "module Example.Api\n" + contract), "lib", _path, default);
        Assert.Null(error);
        Assert.Equal("checkErrors", result!.Status);
        Assert.Empty(result.HiddenBySignature);
        Assert.Contains(result.CompilerErrors, e => e.Id.StartsWith("FS", StringComparison.Ordinal)
            && e.Line > 0 && e.Column > 0 && (e.FilePath == _path || e.FilePath == Path.ChangeExtension(_path, ".fsi")));
    }

    [Fact]
    public async Task mcp_client_opens_a_signature_pair_and_reads_hidden_declarations()
    {
        var directory = Directory.CreateTempSubdirectory("dotnet-mcp-fsi-");
        try
        {
            var project = Path.Combine(directory.FullName, "Example.fsproj");
            var impl = Path.Combine(directory.FullName, "Api.fs");
            await File.WriteAllTextAsync(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="Api.fsi"/><Compile Include="Api.fs"/></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.ChangeExtension(impl, ".fsi"), Signature);
            await File.WriteAllTextAsync(impl, Implementation);
            await using var fx = new InProcessMcpFixture(TrustedRoots.Create([directory.FullName]));
            await WorkspaceReady.OpenUntilReadyAsync(fx, project, WorkspaceReady.MsBuildTimeout);
            var list = await fx.Client.CallToolAsync("workspace_list_projects");
            var id = Assert.Single(InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(list).Projects).ProjectId;
            var response = await fx.Client.CallToolAsync("fsharp_signature_status", new Dictionary<string, object?>
            {
                ["projectId"] = id,
                ["implementationFile"] = impl
            });
            Assert.True(response.IsError is not true, InProcessMcpFixture.TextOf(response));
            var status = InProcessMcpFixture.Deserialize<FSharpSignatureStatus>(response);
            Assert.Equal("conforms", status.Status);
            Assert.Equal("same", Assert.Single(status.HiddenBySignature).Name);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task absent_signature_reports_only_compiled_siblings_and_inputs()
    {
        var snapshot = Snapshot(Implementation, null);
        var service = new FSharpSymbolQueryService();
        var (result, _) = await service.SignatureStatusAsync(snapshot, "lib", _path, default);
        Assert.Equal("noSignature", result!.Status);
        Assert.Null(result.SignatureFile);
        var (_, missing) = await service.SignatureStatusAsync(snapshot, "lib", _path + ".fs", default);
        Assert.IsType<DocumentNotInWorkspaceError>(missing);
    }
}
