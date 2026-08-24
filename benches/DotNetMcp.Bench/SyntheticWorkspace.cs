namespace DotNetMcp.Bench;

internal static class SyntheticWorkspace
{
    public static string Create(string root, int projectCount, int filesPerProject)
    {
        Directory.CreateDirectory(root);
        var slnx = Path.Combine(root, "Synth.slnx");
        var projectsXml = new System.Text.StringBuilder();
        projectsXml.AppendLine("<Solution>");

        for (var i = 0; i < projectCount; i++)
        {
            var name = $"P{i:000}";
            var dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            var refs = i == 0
                ? ""
                : """
                    <ItemGroup>
                      <ProjectReference Include="..\P000\P000.csproj" />
                    </ItemGroup>
                  """;
            File.WriteAllText(
                Path.Combine(dir, $"{name}.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  {refs}
                </Project>
                """);

            if (i == 0)
            {
                File.WriteAllText(
                    Path.Combine(dir, "IShared.cs"),
                    """
                    namespace P000;

                    public interface IShared
                    {
                        string Ping();
                    }
                    """);
            }

            for (var f = 0; f < filesPerProject; f++)
            {
                var typeName = $"Type{i:000}_{f:00}";
                var implements = i == 0 && f == 0 ? ": IShared" : "";
                var ping = i == 0 && f == 0
                    ? """public string Ping() => "p000";"""
                    : $"""public static string Id => "{typeName}";""";
                var useShared = i == 0
                    ? ""
                    : """
                      public static string Touch(P000.IShared shared) => shared.Ping();
                      """;
                File.WriteAllText(
                    Path.Combine(dir, $"{typeName}.cs"),
                    $$"""
                    namespace {{name}};

                    public sealed class {{typeName}} {{implements}}
                    {
                        {{ping}}
                        {{useShared}}
                    }
                    """);
            }

            projectsXml.AppendLine($"  <Project Path=\"{name}/{name}.csproj\" />");
        }

        projectsXml.AppendLine("</Solution>");
        File.WriteAllText(slnx, projectsXml.ToString());
        return slnx;
    }

    public static string CreateXamlApp(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "XamlApp.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <RootNamespace>SampleApp</RootNamespace>
              </PropertyGroup>
              <ItemGroup>
                <AdditionalFiles Include="MainWindow.axaml" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(root, "MainWindow.axaml"),
            """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    x:Class="SampleApp.MainWindow"
                    Title="Sample">
                <TextBlock x:Name="TitleText" Text="{Binding Name}" />
            </Window>
            """);
        File.WriteAllText(
            Path.Combine(root, "MainWindow.axaml.cs"),
            """
            namespace SampleApp;

            public partial class MainWindow
            {
                public string Name { get; set; } = "sample";
            }
            """);
        return Path.Combine(root, "XamlApp.csproj");
    }
}
