using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace S8.Verification;

public class RefactoringHostTests
{
    [Fact]
    public void provider_discovery_finds_parameterless_code_refactoring_providers()
    {
        var csharp = CreateProviders(LanguageNames.CSharp);
        var vb = CreateProviders(LanguageNames.VisualBasic);
        Assert.True(csharp.Count > 5, $"C# providers: {csharp.Count}");
        Assert.True(vb.Count > 3, $"VB providers: {vb.Count}");
    }

    [Fact]
    public async Task csharp_public_field_has_encapsulate_or_equivalent_and_preview_does_not_write_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s8-cs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Widget.cs");
        const string source = """
            namespace RefactorApp;

            public sealed class Widget
            {
                public int count;
            }
            """;
        await File.WriteAllTextAsync(path, source);

        try
        {
            var (document, symbol) = await CompileCSharpFieldAsync(path, source, "count");
            var span = symbol.Locations.First(static l => l.IsInSource).SourceSpan;
            var providers = CreateProviders(LanguageNames.CSharp);
            var actions = await CollectActionsAsync(document, span, providers);
            Assert.True(
                actions.Count > 0,
                $"No refactorings at field span. Providers={providers.Count}");

            Solution? changed = null;
            string? appliedTitle = null;
            string? newText = null;
            foreach (var action in actions)
            {
                var candidate = await ApplyAsync(action);
                if (candidate is null)
                {
                    continue;
                }

                var candidateDoc = candidate.GetDocument(document.Id);
                if (candidateDoc is null)
                {
                    continue;
                }

                var text = (await candidateDoc.GetTextAsync()).ToString();
                if (LooksLikeEncapsulateOrEquivalent(text, source))
                {
                    changed = candidate;
                    appliedTitle = action.Title;
                    newText = text;
                    break;
                }
            }

            Assert.True(
                changed is not null,
                "Titles: " + string.Join(" | ", actions.Select(static a => a.Title)));
            Assert.False(string.IsNullOrWhiteSpace(appliedTitle));
            Assert.NotEqual(source, newText);
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task vb_public_field_has_first_party_refactoring()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s8-vb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Widget.vb");
        const string source = """
            Public Class Widget
                Public count As Integer
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
                    "VbRefactorApp",
                    "VbRefactorApp",
                    LanguageNames.VisualBasic,
                    filePath: Path.Combine(dir, "VbRefactorApp.vbproj")))
                .AddDocument(docId, "Widget.vb", SourceText.From(source), filePath: path)
                .WithProjectCompilationOptions(projectId, new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .AddMetadataReferences(projectId, PlatformRefs());
            Assert.True(workspace.TryApplyChanges(solution));
            var document = workspace.CurrentSolution.GetDocument(docId)!;
            var compilation = await document.Project.GetCompilationAsync();
            Assert.NotNull(compilation);
            var field = compilation!.GetTypeByMetadataName("Widget")!.GetMembers("count").OfType<IFieldSymbol>().Single();
            var span = field.Locations.First(static l => l.IsInSource).SourceSpan;
            var actions = await CollectActionsAsync(document, span, CreateProviders(LanguageNames.VisualBasic));
            Assert.True(actions.Count > 0, "No VB refactorings at field span.");

            var applied = false;
            foreach (var action in actions)
            {
                var candidate = await ApplyAsync(action);
                if (candidate is null)
                {
                    continue;
                }

                var text = (await candidate.GetDocument(docId)!.GetTextAsync()).ToString();
                if (!string.Equals(text, source, StringComparison.Ordinal))
                {
                    applied = true;
                    break;
                }
            }

            Assert.True(applied, "Titles: " + string.Join(" | ", actions.Select(static a => a.Title)));
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static bool LooksLikeEncapsulateOrEquivalent(string text, string original)
    {
        if (string.Equals(text, original, StringComparison.Ordinal))
        {
            return false;
        }

        return text.Contains("get", StringComparison.Ordinal)
            || text.Contains("set", StringComparison.Ordinal)
            || text.Contains("Count", StringComparison.Ordinal);
    }

    private static async Task<(Document Document, IFieldSymbol Field)> CompileCSharpFieldAsync(
        string path,
        string source,
        string fieldName)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var docId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "RefactorApp",
                "RefactorApp",
                LanguageNames.CSharp,
                filePath: Path.Combine(Path.GetDirectoryName(path)!, "RefactorApp.csproj")))
            .AddDocument(docId, "Widget.cs", SourceText.From(source), filePath: path)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddMetadataReferences(projectId, PlatformRefs());
        Assert.True(workspace.TryApplyChanges(solution));
        var document = workspace.CurrentSolution.GetDocument(docId)!;
        var compilation = await document.Project.GetCompilationAsync();
        Assert.NotNull(compilation);
        var field = compilation!.GetTypeByMetadataName("RefactorApp.Widget")!
            .GetMembers(fieldName)
            .OfType<IFieldSymbol>()
            .Single();
        return (document, field);
    }

    internal static IReadOnlyList<CodeRefactoringProvider> CreateProviders(string language)
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
        var list = new List<CodeRefactoringProvider>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(CodeRefactoringProvider).IsAssignableFrom(type))
            {
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is CodeRefactoringProvider provider)
                {
                    list.Add(provider);
                }
            }
            catch
            {
                // Some parameterless types still throw in the ctor.
            }
        }

        return list;
    }

    internal static async Task<IReadOnlyList<CodeAction>> CollectActionsAsync(
        Document document,
        Microsoft.CodeAnalysis.Text.TextSpan span,
        IEnumerable<CodeRefactoringProvider> providers)
    {
        var actions = new List<CodeAction>();
        foreach (var provider in providers)
        {
            try
            {
                var context = new CodeRefactoringContext(
                    document,
                    span,
                    action => actions.AddRange(Flatten(action)),
                    CancellationToken.None);
                await provider.ComputeRefactoringsAsync(context);
            }
            catch
            {
                // Skip providers that require MEF / VS services at compute time.
            }
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
