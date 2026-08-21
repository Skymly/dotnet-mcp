namespace DotNetMcp.Tests;

public class CodeActionDocumentsSeamTests
{
    [Fact]
    public void code_action_documents_is_the_single_get_providers_flatten_apply_action()
    {
        var coreDir = FindCoreDir();
        var helper = File.ReadAllText(Path.Combine(coreDir, "CodeActionDocuments.cs"));
        Assert.Contains("GetProviders", helper, StringComparison.Ordinal);
        Assert.Contains("Flatten", helper, StringComparison.Ordinal);
        Assert.Contains("ApplyActionAsync", helper, StringComparison.Ordinal);

        foreach (var name in new[] { "DiagnosticFixService.cs", "CodeRefactoringService.cs" })
        {
            var text = File.ReadAllText(Path.Combine(coreDir, name));
            Assert.Contains("CodeActionDocuments.GetProviders", text, StringComparison.Ordinal);
            Assert.Contains("CodeActionDocuments.Flatten", text, StringComparison.Ordinal);
            Assert.Contains("CodeActionDocuments.ApplyActionAsync", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private static IReadOnlyList<CodeFixProvider> GetProviders", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private static IReadOnlyList<CodeRefactoringProvider> GetProviders", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private static IEnumerable<CodeAction> Flatten", text, StringComparison.Ordinal);
            Assert.DoesNotContain("private static async Task<Solution?> ApplyActionAsync", text, StringComparison.Ordinal);
        }
    }

    private static string FindCoreDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DotNetMcp.Core");
            if (File.Exists(Path.Combine(candidate, "CodeActionDocuments.cs")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate src/DotNetMcp.Core from the test assembly.");
    }
}
