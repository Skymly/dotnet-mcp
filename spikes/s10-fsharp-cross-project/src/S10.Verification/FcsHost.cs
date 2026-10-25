// Spike S10 — minimal FCS host replicating the product's checking pipeline.
//
// COPIED FROM PRODUCT (src/DotNetMcp.FSharp/FSharpSymbolQueryService.cs), adapted only
// where the product types (FSharpCatalogItem, epoch maps) are stubbed by simpler spike
// equivalents:
//   - checker creation with DocumentSource.Custom serving snapshot texts
//   - BuildCompilerArgs / CompilerReferences (TPA + FSharp.Core)
//   - NotifyFileChanged per source file before ParseAndCheckProject
//   - CompileLibrary (used as the in-process fallback that produces a dependency dll)
//
// Everything marked [COPIED] is lifted verbatim so the spike measures the same code path
// the product would take; nothing here is meant to ship.

using System.Collections.Concurrent;
using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Symbols;
using FSharp.Compiler.Text;
using Microsoft.FSharp.Control;
using Microsoft.FSharp.Core;

namespace S10.Verification;

/// <summary>One FCS checker + the snapshot-text table the custom DocumentSource reads.</summary>
public sealed class FcsHost
{
    private readonly ConcurrentDictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);

    public FcsHost()
    {
        var documentSource = FuncConvert.FromFunc<string, FSharpAsync<FSharpOption<ISourceText>?>>(
            fileName => FSharpAsync.AwaitTask(Task.FromResult(TryReadSnapshot(fileName))));
        Checker = FSharpChecker.Create(
            null,
            FSharpOption<bool>.Some(true),
            null, null, null, null, null, null, null, null, null,
            FSharpOption<DocumentSource>.Some(DocumentSource.NewCustom(documentSource)),
            null, null);
    }

    public FSharpChecker Checker { get; }

    /// <summary>Publish the text the DocumentSource will serve for a path (snapshot semantics).</summary>
    public void Publish(string path, string text) => _texts[path] = text;

    public bool TryReadText(string path, out string text)
    {
        if (_texts.TryGetValue(path, out text!))
        {
            return true;
        }

        try
        {
            return _texts.TryGetValue(Path.GetFullPath(path), out text!);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private FSharpOption<ISourceText>? TryReadSnapshot(string fileName)
    {
        if (TryReadText(fileName, out var text))
        {
            return FSharpOption<ISourceText>.Some(SourceText.ofString(text));
        }

        return null; // not handled: FCS falls back to disk
    }

    /// <summary>[COPIED] product CheckProjectCoreAsync: notify changed files, then ParseAndCheckProject.</summary>
    public async Task<FSharpCheckProjectResults> CheckAsync(
        FixtureProject project,
        FSharpProjectOptions options,
        CancellationToken cancellationToken = default)
    {
        foreach (var (path, _) in project.Files)
        {
            await FSharpAsync.StartAsTask(
                    Checker.NotifyFileChanged(path, options, userOpName: null),
                    taskCreationOptions: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        return await FSharpAsync.StartAsTask(
                Checker.ParseAndCheckProject(options, userOpName: null),
                taskCreationOptions: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>[COPIED] product GetProjectOptionsFromCommandLineArgs pipeline.</summary>
    public FSharpProjectOptions OptionsFor(FixtureProject project)
    {
        var projectFile = project.ProjectFilePath;
        var dllName = Path.ChangeExtension(projectFile, ".dll");
        var argv = ProductLogic.BuildCompilerArgs(
            dllName,
            project.Files.Select(f => f.Path),
            defines: null,
            extraReferences: project.CompilerReferencePaths);
        return Checker.GetProjectOptionsFromCommandLineArgs(projectFile, argv, null, null, null);
    }

    /// <summary>Route A: copy the record with ReferencedProjects filled (the -r: args stay as map keys).</summary>
    public static FSharpProjectOptions WithReferencedProjects(
        FSharpProjectOptions options,
        FSharpReferencedProject[] referencedProjects) =>
        new(
            options.ProjectFileName,
            options.ProjectId,
            options.SourceFiles,
            options.OtherOptions,
            referencedProjects,
            options.IsIncompleteTypeCheckEnvironment,
            options.UseScriptResolutionRules,
            options.LoadTime,
            options.UnresolvedReferences,
            options.OriginalLoadReferences,
            options.Stamp);

    /// <summary>[COPIED] product CompileLibrary: FCS in-process compile that emits a dependency dll.</summary>
    public string CompileLibrary(string outputDll, IReadOnlyList<string> sourceFiles)
    {
        var dir = Path.GetDirectoryName(outputDll);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var argv = ProductLogic.BuildCompilerArgs(outputDll, sourceFiles).ToList();
        argv.Insert(0, "fsc.dll");
        var result = FSharpAsync.RunSynchronously(
            Checker.Compile(argv.ToArray(), userOpName: null),
            timeout: null,
            cancellationToken: null);
        if (result.Item2 != null && OptionModule.IsSome(result.Item2))
        {
            throw new InvalidOperationException(result.Item2.Value.ToString());
        }

        if (!File.Exists(outputDll))
        {
            var errors = string.Join(" | ", result.Item1.Select(d => d.Message));
            throw new InvalidOperationException("F# compile produced no DLL. " + errors);
        }

        return outputDll;
    }
}
