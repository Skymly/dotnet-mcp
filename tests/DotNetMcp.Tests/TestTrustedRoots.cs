using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// Trusted roots plus the test output directory so FakeSolutionLoader
/// <c>AnalyzerFileReference</c> DLLs (CustomGenerator / Avalonia.NameGenerator)
/// pass the graph gate without widening production toolchain roots.
/// </summary>
internal static class TestTrustedRoots
{
    public static TrustedRoots Create(params string[] roots) =>
        TrustedRoots.Create(WithTestOutput(roots));

    private static IEnumerable<string> WithTestOutput(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
        }

        yield return AppContext.BaseDirectory;
    }
}
