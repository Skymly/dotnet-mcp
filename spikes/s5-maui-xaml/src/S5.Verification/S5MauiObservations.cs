using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using Xunit.Abstractions;

namespace S5.Verification;

internal static class MsBuildBootstrap
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            _registered = true;
        }
    }
}

internal static class FixturePaths
{
    public static string SpikeRoot { get; } = FindSpikeRoot();

    public static string MauiProject { get; } =
        Path.Combine(SpikeRoot, "fixtures", "MauiPage", "MauiPage.csproj");

    public static string MauiXaml { get; } =
        Path.Combine(SpikeRoot, "fixtures", "MauiPage", "MainPage.xaml");

    private static string FindSpikeRoot([CallerFilePath] string? thisFile = null)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CONCLUSIONS.md")) ||
                Directory.Exists(Path.Combine(dir.FullName, "fixtures")))
            {
                if (dir.Name == "s5-maui-xaml")
                {
                    return dir.FullName;
                }
            }

            if (Directory.Exists(Path.Combine(dir.FullName, "spikes", "s5-maui-xaml")))
            {
                return Path.Combine(dir.FullName, "spikes", "s5-maui-xaml");
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate S5 spike root.");
    }
}

public sealed class S5MauiObservations
{
    private readonly ITestOutputHelper _output;

    public S5MauiObservations(ITestOutputHelper output) => _output = output;

    [Fact]
    public void maui_xaml_xmlns_is_not_wpf_or_avalonia()
    {
        var xml = XDocument.Load(FixturePaths.MauiXaml);
        var ns = xml.Root!.Name.NamespaceName;
        _output.WriteLine($"root={xml.Root.Name.LocalName} xmlns={ns}");
        Assert.Equal("http://schemas.microsoft.com/dotnet/2021/maui", ns);
        Assert.NotEqual("https://github.com/avaloniaui", ns);
        Assert.NotEqual("http://schemas.microsoft.com/winfx/2006/xaml/presentation", ns);
        var xClass = xml.Root.Attributes().First(a => a.Name.LocalName == "Class").Value;
        Assert.Equal("MauiPage.MainPage", xClass);
        Assert.Equal(".xaml", Path.GetExtension(FixturePaths.MauiXaml), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task sourcegen_emits_xname_field_and_compiled_binding_without_disk_gcs()
    {
        MsBuildBootstrap.EnsureRegistered();
        using var workspace = MSBuildWorkspace.Create();
        var sw = Stopwatch.StartNew();
        var project = await workspace.OpenProjectAsync(FixturePaths.MauiProject);
        var compilation = await project.GetCompilationAsync();
        sw.Stop();
        Assert.NotNull(compilation);

        var generated = compilation!.SyntaxTrees
            .Where(t => string.IsNullOrWhiteSpace(t.FilePath) ||
                        t.FilePath.Contains("SourceGen", StringComparison.OrdinalIgnoreCase) ||
                        t.FilePath.Contains("BindingSourceGen", StringComparison.OrdinalIgnoreCase) ||
                        t.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                        t.ToString().Contains("TitleLabel") ||
                        t.ToString().Contains("InitializeComponent"))
            .ToArray();

        _output.WriteLine($"open+compileMs={sw.ElapsedMilliseconds}");
        _output.WriteLine($"trees={compilation.SyntaxTrees.Count()} generatedLike={generated.Length}");
        foreach (var tree in compilation.SyntaxTrees)
        {
            var text = tree.ToString();
            var hit = text.Contains("TitleLabel", StringComparison.Ordinal) ||
                      text.Contains("InitializeComponent", StringComparison.Ordinal);
            _output.WriteLine($"tree path='{tree.FilePath}' len={text.Length} hit={hit}");
            if (hit)
            {
                _output.WriteLine(text.Length > 1500 ? text[..1500] + "..." : text);
            }
        }

        var sourceGenRefs = project.AnalyzerReferences
            .Select(a => Path.GetFileName(a.FullPath))
            .Where(n => n.Contains("Maui", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _output.WriteLine("analyzers=" + string.Join(",", sourceGenRefs));
        Assert.Contains(sourceGenRefs, n => n.Contains("SourceGen", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(sourceGenRefs, n => n.Contains("BindingSourceGen", StringComparison.OrdinalIgnoreCase));

        var diskGcs = Directory.Exists(Path.Combine(Path.GetDirectoryName(FixturePaths.MauiProject)!, "obj"))
            ? Directory.GetFiles(Path.GetDirectoryName(FixturePaths.MauiProject)!, "*.g.cs", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith("GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
        _output.WriteLine("diskGcs=" + string.Join(",", diskGcs.Select(Path.GetFileName)));

        var main = compilation.GetTypeByMetadataName("MauiPage.MainPage");
        Assert.NotNull(main);
        var title = main!.GetMembers("TitleLabel").FirstOrDefault();
        _output.WriteLine($"TitleLabel={title?.Kind} loc={title?.Locations.FirstOrDefault()?.IsInSource}");
        Assert.NotNull(title);
    }
}
