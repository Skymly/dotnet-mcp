namespace DotNetMcp.Tests;

public class BenchGuessSymbolSeamTests
{
    [Fact]
    public void guess_symbol_does_not_call_workspace_list_projects()
    {
        var path = Path.Combine(FindRepoRoot(), "benches", "DotNetMcp.Bench", "Suites.cs");
        var source = File.ReadAllText(path);
        var body = MethodBody(source, "GuessSymbolAsync");
        Assert.DoesNotContain("workspace_list_projects", body, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string name)
    {
        var signature = "Task<string?> " + name + "(";
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException("Method body was not closed.");
    }

    private static string FindRepoRoot()
    {
        var candidates = new[]
        {
            Environment.CurrentDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")),
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "DotNetMcp.slnx")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root from the test host.");
    }
}