# Spike S9 — Generation triggers

Time-boxed verification for issue #521: given a member with `Origin = SourceGenerator(...)`, which mechanisms reliably locate the handwritten declaration that triggered the generator, and how is confidence graded? Covers Roslyn partial-pairing APIs, CommunityToolkit.Mvvm, System.Text.Json source generation, built-in SDK generators, XAML generators (analysis only), and the `Assembly::Type` rule-table key. See [CONCLUSIONS.md](CONCLUSIONS.md).

Layout: `fixtures/ToolkitApp/` is the generator-host project opened via MSBuildWorkspace; `src/` is the xunit verification project. `dotnet test src` restores the fixture and runs everything.
