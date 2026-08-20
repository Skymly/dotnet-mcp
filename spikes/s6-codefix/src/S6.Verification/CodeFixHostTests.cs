using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace S6.Verification;

public class CodeFixHostTests
{
    [Fact]
    public async Task csharp_missing_using_has_first_party_fix_and_preview_does_not_write_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s6-cs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Broken.cs");
        const string source = """
            namespace FixApp;

            public sealed class Broken
            {
                public int Count()
                {
                    var items = new List<int>();
                    return items.Count;
                }
            }
            """;
        await File.WriteAllTextAsync(path, source);

        try
        {
            var (document, diagnostic) = await CompileCSharpAsync(path, source);
            Assert.Contains(diagnostic.Id, new[] { "CS0246", "CS0103" }, StringComparer.Ordinal);

            var providers = CreateProviders(LanguageNames.CSharp);
            Assert.NotEmpty(providers);

            var actions = await CollectActionsAsync(document, diagnostic, providers);
            Assert.True(actions.Count > 0, $"No actions for {diagnostic.Id}. Providers={providers.Count} ids={string.Join(",", providers.SelectMany(p => p.FixableDiagnosticIds).Distinct().Take(20))}");

                        Solution? changed = null;
            string? appliedTitle = null;
            string? newText = null;
            foreach (var action in actions)
            {
                var candidate = await ApplyAsync(action);
                if (candidate is null) continue;
                var candidateDoc = candidate.GetDocument(document.Id);
                if (candidateDoc is null) continue;
                var text = (await candidateDoc.GetTextAsync()).ToString();
                var compilation = await candidateDoc.Project.GetCompilationAsync();
                var remaining = compilation!.GetDiagnostics()
                    .Any(d => d.Id == diagnostic.Id && d.Location.SourceTree?.FilePath == path);
                if (!remaining && (text.Contains("System.Collections.Generic", StringComparison.Ordinal)))
                {
                    changed = candidate;
                    appliedTitle = action.Title;
                    newText = text;
                    break;
                }
            }

            Assert.True(changed is not null, "Titles: " + string.Join(" | ", actions.Select(a => a.Title)));
            Assert.DoesNotContain("CS0246", (await changed!.Projects.Single().GetCompilationAsync())!.GetDiagnostics().Select(d => d.Id));
            Assert.Contains("System.Collections.Generic", newText, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(appliedTitle));
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task vb_missing_import_has_first_party_fix()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s6-vb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Broken.vb");
        const string source = """
            Public Class Broken
                Public Function Count() As Integer
                    Dim items As New List(Of Integer)()
                    Return items.Count
                End Function
            End Class
            """;
        await File.WriteAllTextAsync(path, source);

        try
        {
            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();
            var docId = DocumentId.CreateNewId(projectId);
            var solution = workspace.CurrentSolution
                .AddProject(ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "VbFixApp",
                    "VbFixApp",
                    LanguageNames.VisualBasic,
                    filePath: Path.Combine(dir, "VbFixApp.vbproj")))
                .AddDocument(docId, "Broken.vb", SourceText.From(source), filePath: path)
                .WithProjectCompilationOptions(projectId, new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddMetadataReferences(projectId, PlatformRefs());
            Assert.True(workspace.TryApplyChanges(solution));
            var document = workspace.CurrentSolution.GetDocument(docId)!;
            var compilation = await document.Project.GetCompilationAsync();
            Assert.NotNull(compilation);
            var tree = await document.GetSyntaxTreeAsync();
            var diagnostic = compilation!.GetDiagnostics()
                .First(d => d.Location.SourceTree == tree && d.Severity == DiagnosticSeverity.Error);

            var actions = await CollectActionsAsync(document, diagnostic, CreateProviders(LanguageNames.VisualBasic));
            Assert.True(actions.Count > 0, $"No VB actions for {diagnostic.Id}: {diagnostic.GetMessage()}");
            Assert.Contains(actions, a =>
                a.Title.Contains("System.Collections.Generic", StringComparison.Ordinal) ||
                a.Title.Contains("Imports", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void provider_discovery_finds_parameterless_code_fix_providers()
    {
        var csharp = CreateProviders(LanguageNames.CSharp);
        var vb = CreateProviders(LanguageNames.VisualBasic);
        Assert.True(csharp.Count > 10, $"C# providers: {csharp.Count}");
        Assert.True(vb.Count > 5, $"VB providers: {vb.Count}");
        Assert.Contains(csharp, p => p.FixableDiagnosticIds.Contains("CS0246"));
    }

    private static async Task<(Document Document, Diagnostic Diagnostic)> CompileCSharpAsync(string path, string source)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "FixApp",
                "FixApp",
                LanguageNames.CSharp,
                filePath: Path.Combine(Path.GetDirectoryName(path)!, "FixApp.csproj")))
            .AddDocument(docId, "Broken.cs", SourceText.From(source), filePath: path)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(projectId, PlatformRefs());
        Assert.True(workspace.TryApplyChanges(solution));
        var document = workspace.CurrentSolution.GetDocument(docId)!;
        var compilation = await document.Project.GetCompilationAsync();
        Assert.NotNull(compilation);
        var tree = await document.GetSyntaxTreeAsync();
        var diagnostic = compilation!.GetDiagnostics()
            .First(d => d.Location.SourceTree == tree && d.Id is "CS0246" or "CS0103");
        return (document, diagnostic);
    }

    internal static IReadOnlyList<CodeFixProvider> CreateProviders(string language)
    {
        var assemblyName = language switch
        {
            LanguageNames.CSharp => "Microsoft.CodeAnalysis.CSharp.Features",
            LanguageNames.VisualBasic => "Microsoft.CodeAnalysis.VisualBasic.Features",
            _ => null
        };
        if (assemblyName is null)
        {
            return [];
        }

        var assembly = Assembly.Load(assemblyName);
        var list = new List<CodeFixProvider>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            if (Activator.CreateInstance(type) is CodeFixProvider provider)
            {
                list.Add(provider);
            }
        }

        return list;
    }

    internal static async Task<IReadOnlyList<CodeAction>> CollectActionsAsync(
        Document document,
        Diagnostic diagnostic,
        IEnumerable<CodeFixProvider> providers)
    {
        var actions = new List<CodeAction>();
        foreach (var provider in providers)
        {
            if (!provider.FixableDiagnosticIds.Contains(diagnostic.Id))
            {
                continue;
            }

            var context = new CodeFixContext(
                document,
                diagnostic,
                (action, _) => actions.AddRange(Flatten(action)),
                CancellationToken.None);
            await provider.RegisterCodeFixesAsync(context);
        }

        return actions;
    }

    private static IEnumerable<CodeAction> Flatten(CodeAction action)
    {
        var nested = action.NestedActions;
        return nested.Length == 0 ? [action] : nested.SelectMany(Flatten);
    }

    private static async Task<Solution?> ApplyAsync(CodeAction action)
    {
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        return operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
    }

    private static IEnumerable<MetadataReference> PlatformRefs()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrWhiteSpace(tpa))
        {
            yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
            yield break;
        }

        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            var name = Path.GetFileName(path);
            if (name is "System.Runtime.dll" or "System.Private.CoreLib.dll" or "System.Collections.dll"
                or "netstandard.dll" or "System.Console.dll")
            {
                yield return MetadataReference.CreateFromFile(path);
            }
        }
    }
}

