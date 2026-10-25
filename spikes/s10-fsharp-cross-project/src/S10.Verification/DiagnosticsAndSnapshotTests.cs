// Q2 — what does the #330 "dependency output not built" diagnostic mean under each route,
// and what does route A imply for the FSharpWorkspaceSnapshot shape?
//
//   Route B + missing dll  -> FCS reports resolution errors (today's DependencyOutputNotBuilt
//                             warning explains them).
//   Route A + missing dll  -> in-memory FSharpReference resolves the -r: key; check is clean.
//   Route A + missing NON-F# dll -> no FSharpReference can cover it; errors persist, so the
//                             diagnostic stays relevant for C#/VB dependencies.

using FSharp.Compiler.CodeAnalysis;
using Xunit.Abstractions;

namespace S10.Verification;

public sealed class DiagnosticsAndSnapshotTests(ITestOutputHelper output)
{
    private static Lazy<(FixtureProject Lib, FixtureProject App)> Two => Fixtures.Two;

    private static void Dump(FSharpCheckProjectResults check, string label, ITestOutputHelper output)
    {
        foreach (var d in check.Diagnostics.OrderBy(d => d.ErrorNumber))
        {
            output.WriteLine(
                $"{label} FS{d.ErrorNumber:D4} {d.Severity} {Path.GetFileName(d.FileName)}:{d.StartLine} {d.Message}");
        }
    }

    [Fact]
    public async Task RouteB_missing_dependency_output_yields_resolution_errors()
    {
        var host = new FcsHost();
        var (lib, app) = Two.Value;
        foreach (var (path, text) in lib.Files.Concat(app.Files))
        {
            host.Publish(path, text);
        }

        Assert.False(File.Exists(app.CompilerReferencePaths[0])); // -r:FsLib.dll intentionally never produced

        var check = await host.CheckAsync(app, host.OptionsFor(app));
        Dump(check, "routeB-missing", output);

        var errors = check.Diagnostics.Where(d => d.Severity.ToString() == "Error").ToList();
        Assert.NotEmpty(errors);
        // Unresolved FsLib.Core.* uses in Use.fs — this is what MissingDependencyOutputs explains.
        Assert.Contains(errors, d => d.ErrorNumber == 39);
    }

    [Fact]
    public async Task RouteA_missing_dll_still_checks_clean()
    {
        var host = new FcsHost();
        var (lib, app) = Two.Value;
        foreach (var (path, text) in lib.Files.Concat(app.Files))
        {
            host.Publish(path, text);
        }

        Assert.False(File.Exists(lib.ReferencedDllPath)); // the -r: key never exists on disk

        var libOptions = host.OptionsFor(lib);
        var appOptions = FcsHost.WithReferencedProjects(
            host.OptionsFor(app),
            [FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, libOptions)]);
        var check = await host.CheckAsync(app, appOptions);
        Dump(check, "routeA-missing", output);

        var errors = check.Diagnostics.Where(d => d.Severity.ToString() == "Error").ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public async Task RouteA_missing_non_fsharp_dependency_still_errors()
    {
        var host = new FcsHost();
        var (lib, app) = Two.Value;
        foreach (var (path, text) in lib.Files.Concat(app.Files))
        {
            host.Publish(path, text);
        }

        // Simulate the product case: FsApp references an F# project (covered by route A)
        // AND a C# project whose output was never built (cannot be covered by route A —
        // FSharpReference only carries F# projects; a C# dep needs PEReference + real file).
        var fakeCsDll = Path.Combine(Path.GetDirectoryName(app.ProjectFilePath)!, "SomeCsLib.dll");
        var appProject = app with { CompilerReferencePaths = [lib.ReferencedDllPath, fakeCsDll] };

        var libOptions = host.OptionsFor(lib);
        var appOptions = FcsHost.WithReferencedProjects(
            host.OptionsFor(appProject),
            [FSharpReferencedProject.NewFSharpReference(lib.ReferencedDllPath, libOptions)]);
        var check = await host.CheckAsync(appProject, appOptions);
        Dump(check, "routeA-missing-cs", output);

        var errors = check.Diagnostics.Where(d => d.Severity.ToString() == "Error").ToList();
        Assert.NotEmpty(errors);
        Assert.DoesNotContain(errors, d => d.ErrorNumber == 39); // F# uses still resolve
    }
}
