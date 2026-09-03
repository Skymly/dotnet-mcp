using DotNetMcp.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DotNetMcp.Tests;

public class GeneratorDriverRunnerTests
{
    private static readonly GeneratorIdentity Alpha = new("Alpha", "Alpha.Gen", "1.0.0.0");
    private static readonly GeneratorIdentity Beta = new("Beta", "Beta.Gen", "1.0.0.0");
    private const string SharedContent = "class Shared {}";

    [Fact]
    public void match_tree_reference_equals_wins_over_content_collision()
    {
        var treeA = Parse(SharedContent, "A.g.cs");
        var treeB = Parse(SharedContent, "B.g.cs");
        var snapshot = Snapshot(
            new GeneratedSourceMatch(Alpha, "A.g.cs", SharedContent, treeA),
            new GeneratedSourceMatch(Beta, "B.g.cs", SharedContent, treeB));

        var match = GeneratorDriverRunner.MatchTree(snapshot, treeB);

        Assert.False(match.Ambiguous);
        Assert.Equal(Beta, match.Identity);
    }

    [Fact]
    public void match_tree_unique_content_binds_identity()
    {
        var driverTree = Parse("class Unique {}", "U.g.cs");
        var queryTree = Parse("class Unique {}", "other.cs");
        var snapshot = Snapshot(
            new GeneratedSourceMatch(Alpha, "U.g.cs", "class Unique {}", driverTree));

        var match = GeneratorDriverRunner.MatchTree(snapshot, queryTree);

        Assert.False(match.Ambiguous);
        Assert.Equal(Alpha, match.Identity);
    }

    [Fact]
    public void match_tree_same_identity_repeated_content_is_unique()
    {
        var snapshot = Snapshot(
            new GeneratedSourceMatch(Alpha, "One.g.cs", SharedContent, Parse(SharedContent, "One.g.cs")),
            new GeneratedSourceMatch(Alpha, "Two.g.cs", SharedContent, Parse(SharedContent, "Two.g.cs")));

        var match = GeneratorDriverRunner.MatchTree(snapshot, Parse(SharedContent, "query.cs"));

        Assert.False(match.Ambiguous);
        Assert.Equal(Alpha, match.Identity);
    }

    [Fact]
    public void match_tree_distinct_identities_same_content_is_ambiguous()
    {
        var snapshot = Snapshot(
            new GeneratedSourceMatch(Alpha, "A.g.cs", SharedContent, Parse(SharedContent, "A.g.cs")),
            new GeneratedSourceMatch(Beta, "B.g.cs", SharedContent, Parse(SharedContent, "B.g.cs")));

        var match = GeneratorDriverRunner.MatchTree(snapshot, Parse(SharedContent, "query.cs"));

        Assert.True(match.Ambiguous);
        Assert.Null(match.Identity);
    }

    [Fact]
    public void match_tree_no_content_match_is_none()
    {
        var snapshot = Snapshot(
            new GeneratedSourceMatch(Alpha, "A.g.cs", "class A {}", Parse("class A {}", "A.g.cs")));

        var match = GeneratorDriverRunner.MatchTree(snapshot, Parse("class Other {}", "other.cs"));

        Assert.False(match.Ambiguous);
        Assert.Null(match.Identity);
    }

    private static SyntaxTree Parse(string text, string path) =>
        CSharpSyntaxTree.ParseText(text, path: path);

    private static DriverRunSnapshot Snapshot(params GeneratedSourceMatch[] sources) =>
        new([], sources);
}
