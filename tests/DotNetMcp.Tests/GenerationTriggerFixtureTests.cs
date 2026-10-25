using DotNetMcp.Server;

namespace DotNetMcp.Tests;

/// <summary>
/// symbol_attribution generation triggers (issue #524) end-to-end through
/// MSBuildWorkspace against tests/fixtures/GenerationTriggers: real
/// CommunityToolkit.Mvvm 8.4.2, System.Text.Json, and GeneratedRegex generators.
/// </summary>
public class GenerationTriggerFixtureTests
{
    private static string ProjectPath => Path.Combine(
        MsBuildWorkspaceIntegrationTests.FixturesRoot, "GenerationTriggers", "GenerationTriggers.csproj");

    [Fact]
    public async Task toolkit_observable_property_field_and_partial_triggers()
    {
        await using var fx = await OpenAsync();

        var body = await AttributionAsync(fx, "TriggerApp.MainViewModel");
        var members = body.Members;

        // [ObservableProperty] field -> generated property: Convention attribute trigger.
        var name = AssertTrigger(members, "TriggerApp.MainViewModel.Name");
        Assert.Equal("Attribute", name.Kind);
        Assert.Equal("Convention", name.Confidence);
        Assert.Equal("ObservableProperty", name.Detail);
        Assert.Equal("ObservableProperty", SpanOf(name));
        Assert.Contains("._name", name.Handle!, StringComparison.Ordinal);

        // [ObservableProperty] partial property is handwritten itself.
        var nick = members["TriggerApp.MainViewModel.Nick"];
        Assert.Equal("Handwritten", nick.OriginKind);
        Assert.Empty(nick.Triggers);

        // Generated callback *definition* -> handwritten partial implementation: Exact.
        var callback = AssertTrigger(members, "TriggerApp.MainViewModel.OnNameChanged(string)");
        Assert.Equal("PartialDefinition", callback.Kind);
        Assert.Equal("Exact", callback.Confidence);
        Assert.Equal("partial implementation", callback.Detail);
        Assert.Equal("OnNameChanged", SpanOf(callback));

        // Callback without a handwritten implementation falls back to the attribute
        // carrier — here the partial property itself.
        var nickCallback = AssertTrigger(members, "TriggerApp.MainViewModel.OnNickChanging(string)");
        Assert.Equal("Attribute", nickCallback.Kind);
        Assert.Equal("Convention", nickCallback.Confidence);
        Assert.Equal("ObservableProperty", nickCallback.Detail);
        Assert.Contains(".Nick", nickCallback.Handle!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task toolkit_relay_command_stems_resolve_to_unique_methods()
    {
        await using var fx = await OpenAsync();

        var body = await AttributionAsync(fx, "TriggerApp.MainViewModel");
        var members = body.Members;

        foreach (var (memberKey, methodSignature) in new (string Key, string Method)[]
        {
            ("TriggerApp.MainViewModel.SaveCommand", ".Save()"),
            ("TriggerApp.MainViewModel.saveCommand", ".Save()"),
            ("TriggerApp.MainViewModel.RefreshCommand", ".OnRefresh()"),
            ("TriggerApp.MainViewModel.LoadCommand", ".LoadAsync()"),
        })
        {
            var trigger = AssertTrigger(members, memberKey);
            Assert.Equal("Attribute", trigger.Kind);
            Assert.Equal("Convention", trigger.Confidence);
            Assert.Equal("RelayCommand", trigger.Detail);
            Assert.Equal("RelayCommand", SpanOf(trigger));
            Assert.Contains(methodSignature, trigger.Handle!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task stj_generated_members_trigger_jsonserializable_attribute()
    {
        await using var fx = await OpenAsync();

        var body = await AttributionAsync(fx, "TriggerApp.AppJsonContext");

        var trigger = AssertTrigger(body.Members, "TriggerApp.AppJsonContext.Poco");
        Assert.Equal("Attribute", trigger.Kind);
        Assert.Equal("Exact", trigger.Confidence);
        Assert.Equal("JsonSerializable", trigger.Detail);
        Assert.Equal("JsonSerializable(typeof(Poco))", SpanOf(trigger));
        Assert.EndsWith("JsonContext.cs", trigger.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".AppJsonContext", trigger.Handle!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task handwritten_and_definition_side_symbols_report_empty_triggers()
    {
        await using var fx = await OpenAsync();

        // [GeneratedRegex] partial method: the surfaced symbol is the handwritten
        // *definition* — nothing else to edit, so triggers stay empty.
        var regex = await AttributionAsync(fx, "TriggerApp.Regexes.Foo");
        Assert.Equal("Handwritten", regex.OriginKind);
        Assert.Empty(regex.Triggers);
        Assert.Null(regex.TriggerMessage);

        // Plain handwritten property.
        var note = await AttributionAsync(fx, "TriggerApp.MainViewModel.HandwrittenNote");
        Assert.Equal("Handwritten", note.OriginKind);
        Assert.Empty(note.Triggers);
    }

    [Fact]
    public async Task runtime_generator_identities_match_the_rule_table()
    {
        await using var fx = await OpenAsync();

        var projects = InProcessMcpFixture.Deserialize<WorkspaceListProjectsResultDto>(
            await fx.Client.CallToolAsync("workspace_list_projects", new Dictionary<string, object?>()));
        var projectId = Assert.Single(projects.Projects).ProjectId;

        var result = await fx.Client.CallToolAsync(
            "project_list_generators",
            new Dictionary<string, object?> { ["projectId"] = projectId });
        Assert.True(result.IsError is not true, InProcessMcpFixture.TextOf(result));
        var generators = InProcessMcpFixture.Deserialize<ProjectListGeneratorsResultDto>(result).Generators;

        Assert.Contains(generators, g =>
            g.AssemblyName == "CommunityToolkit.Mvvm.SourceGenerators" &&
            g.TypeFullName == "CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator");
        Assert.Contains(generators, g =>
            g.AssemblyName == "CommunityToolkit.Mvvm.SourceGenerators" &&
            g.TypeFullName == "CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator");
        Assert.Contains(generators, g =>
            g.AssemblyName == "System.Text.Json.SourceGeneration" &&
            g.TypeFullName == "System.Text.Json.SourceGeneration.JsonSourceGenerator");
        Assert.Contains(generators, g =>
            g.AssemblyName == "System.Text.RegularExpressions.Generator" &&
            g.TypeFullName == "System.Text.RegularExpressions.Generator.RegexGenerator");
    }

    [Fact]
    public async Task code_trigger_handles_round_trip_through_symbol_summary()
    {
        await using var fx = await OpenAsync();

        var body = await AttributionAsync(fx, "TriggerApp.MainViewModel");
        var name = AssertTrigger(body.Members, "TriggerApp.MainViewModel.Name");
        await AssertSymbolSummaryRoundTrips(fx, name.Handle!);
    }

    private static async Task<InProcessMcpFixture> OpenAsync()
    {
        Assert.True(File.Exists(ProjectPath), ProjectPath);
        var root = Path.GetDirectoryName(ProjectPath)!;
        var fx = new InProcessMcpFixture(TrustedRoots.Create([root]));
        var open = await fx.Client.CallToolAsync(
            "workspace_open", new Dictionary<string, object?> { ["path"] = ProjectPath });
        Assert.True(open.IsError is not true, InProcessMcpFixture.TextOf(open));
        var status = await WorkspaceReady.WaitUntilReadyAsync(fx, WorkspaceReady.MsBuildTimeout);
        Assert.Equal("ready", status.Phase);
        return fx;
    }

    private static async Task<SymbolAttributionResultDto> AttributionAsync(
        InProcessMcpFixture fx, string name)
    {
        var resolved = await fx.Client.CallToolAsync(
            "symbol_resolve", new Dictionary<string, object?> { ["name"] = name });
        Assert.True(resolved.IsError is not true, InProcessMcpFixture.TextOf(resolved));
        var handle = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved).Handle;
        var attr = await fx.Client.CallToolAsync(
            "symbol_attribution", new Dictionary<string, object?> { ["handle"] = handle });
        Assert.True(attr.IsError is not true, InProcessMcpFixture.TextOf(attr));
        return InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attr);
    }

    private static GenerationTriggerDto AssertTrigger(
        IReadOnlyDictionary<string, SymbolAttributionDto> members, string key)
    {
        var member = members[key];
        Assert.Equal("SourceGenerator", member.OriginKind);
        Assert.Null(member.TriggerMessage);
        return Assert.Single(member.Triggers);
    }

    private static string SpanOf(GenerationTriggerDto trigger) =>
        File.ReadAllText(trigger.Path!).Substring(trigger.Start!.Value, trigger.Length!.Value);

    private static async Task AssertSymbolSummaryRoundTrips(InProcessMcpFixture fx, string handle)
    {
        var summary = await fx.Client.CallToolAsync(
            "symbol_summary", new Dictionary<string, object?> { ["handle"] = handle });
        Assert.True(summary.IsError is not true, InProcessMcpFixture.TextOf(summary));
    }
}
