using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotNetMcp.Tests;

/// <summary>
/// Roslyn refuses an analyzer that references a newer Microsoft.CodeAnalysis than the host
/// (ReferencesNewerCompiler, CS9057) and GetGenerators then quietly returns nothing. The server reports
/// such analyzers from assembly metadata alone, without loading them.
/// </summary>
public class AnalyzerCompilerSkewTests
{
    private static readonly Version HostCompiler = new(5, 9, 0, 0);

    [Fact]
    public void analyzer_referencing_a_newer_compiler_is_reported_by_file_name_without_being_loaded()
    {
        using var temp = new TempDirectory();
        var analyzer = SkewedAnalyzer.Emit(temp.Path, "NewerCompilerGenerator", "99.0.0.0");

        var warnings = AnalyzerCompilerSkew.Detect([analyzer], HostCompiler);

        var warning = Assert.Single(warnings);
        Assert.StartsWith(AnalyzerCompilerSkew.WarningCode + ": ", warning, StringComparison.Ordinal);
        Assert.Contains("'NewerCompilerGenerator.dll'", warning, StringComparison.Ordinal);
        Assert.Contains("99.0.0.0", warning, StringComparison.Ordinal);
        Assert.Contains("5.9.0.0", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Path, warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            AppDomain.CurrentDomain.GetAssemblies(),
            static assembly => assembly.GetName().Name == "NewerCompilerGenerator");
    }

    [Theory]
    [InlineData("4.0.0.0")]
    [InlineData("5.9.0.0")]
    public void analyzer_referencing_an_older_or_equal_compiler_is_not_reported(string referenced)
    {
        using var temp = new TempDirectory();
        var analyzer = SkewedAnalyzer.Emit(temp.Path, "OlderCompilerGenerator", referenced);

        Assert.Empty(AnalyzerCompilerSkew.Detect([analyzer], HostCompiler));
    }

    [Fact]
    public void missing_and_non_assembly_files_are_ignored_without_throwing()
    {
        using var temp = new TempDirectory();
        var notAnAssembly = Path.Combine(temp.Path, "notes.dll");
        File.WriteAllText(notAnAssembly, "this is not a portable executable");

        var warnings = AnalyzerCompilerSkew.Detect(
            [null, "", "   ", Path.Combine(temp.Path, "missing.dll"), notAnAssembly, temp.Path],
            HostCompiler);

        Assert.Empty(warnings);
    }

    [Fact]
    public void the_same_analyzer_used_by_several_projects_is_reported_once()
    {
        using var temp = new TempDirectory();
        var first = SkewedAnalyzer.Emit(Path.Combine(temp.Path, "a"), "SharedGenerator", "99.0.0.0");
        var second = SkewedAnalyzer.Emit(Path.Combine(temp.Path, "b"), "SharedGenerator", "99.0.0.0");

        // Same path listed repeatedly, plus another copy with the same name and the same problem.
        var warnings = AnalyzerCompilerSkew.Detect([first, first, second], HostCompiler);

        Assert.Single(warnings);
    }

    [Fact]
    public async Task workspace_status_reports_an_analyzer_that_references_a_newer_compiler()
    {
        using var temp = new TempDirectory();
        SkewedAnalyzer.Emit(Path.Combine(temp.Path, "analyzers"), "NewerCompilerGenerator", "99.0.0.0");
        var project = Path.Combine(temp.Path, "App.csproj");
        File.WriteAllText(
            project,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Analyzer Include="analyzers/NewerCompilerGenerator.dll" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(temp.Path, "Class1.cs"), "namespace App; public class Class1 { }");

        await using var fx = new InProcessMcpFixture(TrustedRoots.Create([temp.Path]), solutionLoader: null);
        await WorkspaceReady.OpenUntilReadyAsync(fx, project, WorkspaceReady.MsBuildTimeout);

        var status = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(
            await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>()));

        Assert.NotNull(status.Warnings);
        var warning = Assert.Single(
            status.Warnings,
            static w => w.StartsWith(AnalyzerCompilerSkew.WarningCode + ":", StringComparison.Ordinal));
        Assert.Contains("'NewerCompilerGenerator.dll'", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Path, warning, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Emits a small analyzer assembly that references a Microsoft.CodeAnalysis of a chosen version.</summary>
    private static class SkewedAnalyzer
    {
        public static string Emit(string directory, string assemblyName, string compilerVersion)
        {
            Directory.CreateDirectory(directory);
            var systemReferences = SystemReferences();

            var fakeCompiler = Compile(
                "Microsoft.CodeAnalysis",
                $$"""
                [assembly: System.Reflection.AssemblyVersion("{{compilerVersion}}")]
                namespace Microsoft.CodeAnalysis
                {
                    public struct GeneratorInitializationContext { }
                    public struct GeneratorExecutionContext { }
                    public interface ISourceGenerator
                    {
                        void Initialize(GeneratorInitializationContext context);
                        void Execute(GeneratorExecutionContext context);
                    }
                    [System.AttributeUsage(System.AttributeTargets.Class)]
                    public sealed class GeneratorAttribute : System.Attribute { }
                }
                """,
                systemReferences);

            var analyzer = Compile(
                assemblyName,
                """
                using Microsoft.CodeAnalysis;
                namespace SkewedAnalyzers
                {
                    [Generator]
                    public sealed class SkewedGenerator : ISourceGenerator
                    {
                        public void Initialize(GeneratorInitializationContext context) { }
                        public void Execute(GeneratorExecutionContext context) { }
                    }
                }
                """,
                [.. systemReferences, MetadataReference.CreateFromImage(fakeCompiler)]);

            var path = Path.Combine(directory, assemblyName + ".dll");
            File.WriteAllBytes(path, analyzer);
            return path;
        }

        private static byte[] Compile(string assemblyName, string source, IReadOnlyList<MetadataReference> references)
        {
            var compilation = CSharpCompilation.Create(
                assemblyName,
                [CSharpSyntaxTree.ParseText(source)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var stream = new MemoryStream();
            var result = compilation.Emit(stream);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return stream.ToArray();
        }

        private static MetadataReference[] SystemReferences() =>
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator)
                .Where(static path => Path.GetFileName(path) is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "netstandard.dll")
                .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToArray();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "dotnet-mcp-skew-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
