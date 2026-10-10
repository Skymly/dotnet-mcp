using DotNetMcp.Server;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotNetMcp.Tests;

public class ProjectSummaryTfmTests
{
    [Fact]
    public void single_tfm_without_name_suffix_uses_preprocessor_symbol()
    {
        var project = CreateProject("App", ["TRACE", "NET", "NET8_0", "NETCOREAPP", "NET8_0_OR_GREATER"]);

        Assert.Equal("net8.0", ProjectSummary.ExtractTfm(project));
    }

    [Fact]
    public void name_suffix_wins_over_preprocessor_symbols()
    {
        var project = CreateProject("MultiTfm (net9.0)", ["NET8_0"]);

        Assert.Equal("net9.0", ProjectSummary.ExtractTfm(project));
    }

    [Fact]
    public void missing_suffix_and_framework_symbol_returns_null()
    {
        var project = CreateProject("App", ["TRACE", "DEBUG", "NET"]);

        Assert.Null(ProjectSummary.ExtractTfm(project));
    }

    private static Project CreateProject(string name, string[] symbols)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name,
            "App",
            LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(preprocessorSymbols: symbols)));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace.");
        }

        return workspace.CurrentSolution.GetProject(projectId)
            ?? throw new InvalidOperationException("Project was not added.");
    }
}