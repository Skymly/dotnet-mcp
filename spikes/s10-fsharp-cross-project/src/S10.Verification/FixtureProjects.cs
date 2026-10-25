// Fixture materialization for the spike. Two F# project chains are written to a temp
// directory on first use (contents copied from tests/fixtures/FsharpCrossProject plus a
// three-deep chain and an interface the dependent project implements):
//
//   two/    FsLib  <-  FsApp        (the tests/fixtures pair)
//   three/  FsLib  <-  FsMid  <-  FsTop
//
// Route B needs a real dependency dll: produced either by `dotnet build` (what the
// product consumes) or by the copied FCS CompileLibrary (in-process, no SDK invoke).

using System.Diagnostics;

namespace S10.Verification;

public sealed record FixtureProject
{
    public required string Name { get; init; }
    public required string ProjectFilePath { get; init; }
    public required (string Path, string Text)[] Files { get; init; }

    /// <summary>-r: arguments exactly as the product snapshot would supply them.</summary>
    public required string[] CompilerReferencePaths { get; init; }

    /// <summary>Where `dotnet build` writes this project's output.</summary>
    public required string OutputDllPath { get; init; }

    /// <summary>
    /// The stable -r:/FSharpReference map key for dependents (route A). Under bin/spike-ref/
    /// so it is never produced on disk — route A must prove it works while the file is absent.
    /// </summary>
    public required string ReferencedDllPath { get; init; }
}

public static class Fixtures
{
    public const string LibDllName = "FsLib.dll";
    public const string MidDllName = "FsMid.dll";

    // ---------- sources (tests/fixtures/FsharpCrossProject + spike additions) ----------

    public const string LibSource = """
        module FsLib.Core

        let private bump (n: int) = n + 1

        let doubleThenBump (n: int) = bump (n + n)

        type Greeter =
            static member Greet (name: string) = "hi " + name

        type IShape =
            abstract member Area: float

        """;

    public const string AppSource = """
        module FsApp.Use

        let doubled (n: int) = FsLib.Core.doubleThenBump n

        let greeting (name: string) = FsLib.Core.Greeter.Greet name

        let own (n: int) = n * 3

        type Coin(radius: float) =
            interface FsLib.Core.IShape with
                member _.Area = 3.0 * radius * radius

        let areaOfCoin () =
            let c = Coin(2.0)
            (c :> FsLib.Core.IShape).Area
        """;

    public const string MidSource = """
        module FsMid.Extra

        let tripleThenBump (n: int) = FsLib.Core.doubleThenBump n + n
        """;

    public const string TopSource = """
        module FsTop.Run

        let viaMid (n: int) = FsMid.Extra.tripleThenBump n

        let viaLib (n: int) = FsLib.Core.doubleThenBump (n + 1)
        """;

    // Single materialization for the whole test assembly — tests run in parallel.
    public static readonly Lazy<(FixtureProject Lib, FixtureProject App)> Two = new(TwoChain);
    public static readonly Lazy<(FixtureProject Lib, FixtureProject Mid, FixtureProject Top)> Three = new(ThreeChain);
    public static readonly Lazy<string> BuiltTwoLibDll = new(() => BuildViaDotNet(Two.Value.Lib));

    /// <summary>Materialize the two-project chain (FsLib + FsApp) under temp.</summary>
    private static (FixtureProject Lib, FixtureProject App) TwoChain()
    {
        var root = RootOf("two");
        var libDir = Path.Combine(root, "FsLib");
        var appDir = Path.Combine(root, "FsApp");
        var lib = WriteProject(libDir, "FsLib", [("Library.fs", LibSource)], []);
        var app = WriteProject(
            appDir,
            "FsApp",
            [("Use.fs", AppSource)],
            [lib.ReferencedDllPath],
            [@"..\FsLib\FsLib.fsproj"]);
        return (lib, app);
    }

    /// <summary>Materialize the three-project chain (FsLib + FsMid + FsTop) under temp.</summary>
    private static (FixtureProject Lib, FixtureProject Mid, FixtureProject Top) ThreeChain()
    {
        var root = RootOf("three");
        var libDir = Path.Combine(root, "FsLib");
        var midDir = Path.Combine(root, "FsMid");
        var topDir = Path.Combine(root, "FsTop");
        var lib = WriteProject(libDir, "FsLib", [("Library.fs", LibSource)], []);
        var mid = WriteProject(
            midDir,
            "FsMid",
            [("Mid.fs", MidSource)],
            [lib.ReferencedDllPath],
            [@"..\FsLib\FsLib.fsproj"]);
        var top = WriteProject(
            topDir,
            "FsTop",
            [("Top.fs", TopSource)],
            [mid.ReferencedDllPath, lib.ReferencedDllPath],
            [@"..\FsMid\FsMid.fsproj", @"..\FsLib\FsLib.fsproj"]);
        return (lib, mid, top);
    }

    private static string RootOf(string layout) =>
        Path.Combine(Path.GetTempPath(), "s10-fsx", layout);

    private static FixtureProject WriteProject(
        string dir,
        string name,
        (string File, string Text)[] sources,
        string[] compilerRefs,
        string[]? projectRefs = null)
    {
        Directory.CreateDirectory(dir);
        var files = sources
            .Select(s =>
            {
                var path = Path.Combine(dir, s.File);
                WriteIfChanged(path, s.Text);
                return (path, s.Text);
            })
            .ToArray();

        // Minimal fsproj so `dotnet build` can produce the route-B dll. Compile items are
        // emitted in the order the sources were passed.
        var compiles = string.Join(
            Environment.NewLine,
            sources.Select(s => $"    <Compile Include=\"{s.File}\" />"));
        var refs = projectRefs is { Length: > 0 }
            ? string.Join(Environment.NewLine, projectRefs.Select(r => $"    <ProjectReference Include=\"{r}\" />"))
            : "";
        var fsproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
            {compiles}
            {refs}
              </ItemGroup>
            </Project>
            """;
        var projectPath = Path.Combine(dir, name + ".fsproj");
        WriteIfChanged(projectPath, fsproj);

        return new FixtureProject
        {
            Name = name,
            ProjectFilePath = projectPath,
            Files = files,
            CompilerReferencePaths = compilerRefs,
            OutputDllPath = Path.Combine(dir, "bin", "Debug", "net10.0", name + ".dll"),
            ReferencedDllPath = Path.Combine(dir, "bin", "spike-ref", name + ".dll"),
        };
    }

    private static void WriteIfChanged(string path, string text)
    {
        try
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal))
            {
                return;
            }
        }
        catch (IOException)
        {
            // Another test process may be reading; fall through and try to write anyway.
        }

        File.WriteAllText(path, text);
    }

    /// <summary>Route B dependency dll via `dotnet build` (the output msbuild would produce).</summary>
    public static string BuildViaDotNet(FixtureProject project)
    {
        var psi = new ProcessStartInfo("dotnet", $"build \"{project.ProjectFilePath}\" -c Debug --nologo -v:q")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("dotnet failed to start");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000);
        if (proc.ExitCode != 0 || !File.Exists(project.OutputDllPath))
        {
            throw new InvalidOperationException(
                $"dotnet build failed ({proc.ExitCode}) for {project.Name}: {stdout} {stderr}");
        }

        return project.OutputDllPath;
    }
}
