using System.Collections.Immutable;
using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

public class SameTitleFixOrderTests
{
    [Fact]
    public async Task same_title_empty_key_fixes_from_two_providers_both_remain_in_stable_order()
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "App",
            "App",
            LanguageNames.CSharp));
        solution = solution.AddDocument(documentId, "A.cs", SourceText.From("class A {}"));
        Assert.True(workspace.TryApplyChanges(solution));
        var document = workspace.CurrentSolution.GetDocument(documentId)!;
        var diagnostic = Diagnostic.Create(
            "X0001",
            "Test",
            "msg",
            DiagnosticSeverity.Warning,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            warningLevel: 1);

        var forward = await DiagnosticFixService.CollectActionsAsync(
            document,
            diagnostic,
            [new AlphaSameTitleFix(), new BetaSameTitleFix()],
            CancellationToken.None);
        var reversed = await DiagnosticFixService.CollectActionsAsync(
            document,
            diagnostic,
            [new BetaSameTitleFix(), new AlphaSameTitleFix()],
            CancellationToken.None);

        Assert.Equal(2, forward.Count);
        Assert.Equal(["Alpha", "Beta"], forward.Select(a => ((MarkedFix)a).Mark));
        Assert.Equal(forward.Select(a => ((MarkedFix)a).Mark), reversed.Select(a => ((MarkedFix)a).Mark));
    }

    private sealed class MarkedFix : Microsoft.CodeAnalysis.CodeActions.CodeAction
    {
        public MarkedFix(string mark) => Mark = mark;

        public string Mark { get; }

        public override string Title => "Same fix";

        public override string? EquivalenceKey => null;
    }

    private sealed class AlphaSameTitleFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ["X0001"];

        public override FixAllProvider? GetFixAllProvider() => null;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            context.RegisterCodeFix(new MarkedFix("Alpha"), context.Diagnostics[0]);
            return Task.CompletedTask;
        }
    }

    private sealed class BetaSameTitleFix : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds => ["X0001"];

        public override FixAllProvider? GetFixAllProvider() => null;

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            context.RegisterCodeFix(new MarkedFix("Beta"), context.Diagnostics[0]);
            return Task.CompletedTask;
        }
    }
}