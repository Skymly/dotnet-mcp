using DotNetMcp.Server;

namespace DotNetMcp.Tests;

public class SymbolAttributionSeamTests
{
    [Fact]
    public async Task symbol_attribution_labels_handwritten_host()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithGenerators());

            await OpenUntilReadyAsync(fx, solution);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "GeneratorHost.Host" });
            Assert.True(resolved.IsError is not true);
            var ok = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);

            var attr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?> { ["handle"] = ok.Handle });
            Assert.True(attr.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attr);

            Assert.Equal("InSource", body.DeclarationAvailability);
            Assert.Equal("Handwritten", body.OriginKind);
            Assert.Null(body.Generator);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_attribution_labels_generated_marker_with_identity()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithGenerators());

            await OpenUntilReadyAsync(fx, solution);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleApp.Generated.CustomMarker" });
            Assert.True(resolved.IsError is not true);
            var ok = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);

            var attr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?> { ["handle"] = ok.Handle });
            Assert.True(attr.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attr);

            Assert.Equal("InSource", body.DeclarationAvailability);
            Assert.Equal("SourceGenerator", body.OriginKind);
            Assert.NotNull(body.Generator);
            Assert.Equal("CustomGenerator", body.Generator!.AssemblyName);
            Assert.Equal("CustomGenerator.MarkerGenerator", body.Generator.TypeFullName);
            Assert.Equal("1.2.3.0", body.Generator.Version);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_attribution_partial_members_and_overloads_by_signature()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithGenerators());

            await OpenUntilReadyAsync(fx, solution);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "GeneratorHost.PartialThing" });
            Assert.True(resolved.IsError is not true);
            var ok = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);

            var attr = await fx.Client.CallToolAsync(
                "symbol_attribution",
                new Dictionary<string, object?> { ["handle"] = ok.Handle });
            Assert.True(attr.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(attr);

            Assert.NotEmpty(body.Members);

            var formatKeys = body.Members.Keys
                .Where(k => k.Contains("Format", StringComparison.Ordinal))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToArray();
            Assert.True(formatKeys.Length >= 2, $"expected Format overloads, got: {string.Join(", ", body.Members.Keys)}");

            foreach (var key in formatKeys)
            {
                Assert.Equal("Handwritten", body.Members[key].OriginKind);
                Assert.Null(body.Members[key].Generator);
            }

            var generated = Assert.Single(
                body.Members,
                kv => kv.Key == "GeneratorHost.PartialThing.GeneratedValue");
            Assert.Equal("SourceGenerator", generated.Value.OriginKind);
            Assert.Equal("CustomGenerator.PartialAugmentGenerator", generated.Value.Generator!.TypeFullName);
            Assert.DoesNotContain(body.Members.Keys, k => k.Contains(".get", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_attribution_collision_types_bind_to_distinct_generators()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithGenerators());

            await OpenUntilReadyAsync(fx, solution);

            var resolvedA = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "GeneratorHost.Collision.FromA" });
            var resolvedB = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "GeneratorHost.Collision.FromB" });
            Assert.True(resolvedA.IsError is not true);
            Assert.True(resolvedB.IsError is not true);

            var handleA = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolvedA).Handle;
            var handleB = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolvedB).Handle;

            var attrA = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(
                await fx.Client.CallToolAsync(
                    "symbol_attribution",
                    new Dictionary<string, object?> { ["handle"] = handleA }));
            var attrB = InProcessMcpFixture.Deserialize<SymbolAttributionResultDto>(
                await fx.Client.CallToolAsync(
                    "symbol_attribution",
                    new Dictionary<string, object?> { ["handle"] = handleB }));

            Assert.Equal("SourceGenerator", attrA.OriginKind);
            Assert.Equal("SourceGenerator", attrB.OriginKind);
            Assert.Equal("CustomGenerator.CollisionGeneratorA", attrA.Generator!.TypeFullName);
            Assert.Equal("CustomGenerator.CollisionGeneratorB", attrB.Generator!.TypeFullName);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task symbol_goto_definition_origin_includes_generator_identity()
    {
        var root = CreateTempDir("root");
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution></Solution>");

        try
        {
            await using var fx = new InProcessMcpFixture(
                TrustedRoots.Create([root]),
                FakeSolutionLoader.ImmediateWithGenerators());

            await OpenUntilReadyAsync(fx, solution);

            var resolved = await fx.Client.CallToolAsync(
                "symbol_resolve",
                new Dictionary<string, object?> { ["name"] = "SampleApp.Generated.CustomMarker" });
            Assert.True(resolved.IsError is not true);
            var ok = InProcessMcpFixture.Deserialize<SymbolResolveResultDto>(resolved);

            var gotoDef = await fx.Client.CallToolAsync(
                "symbol_goto_definition",
                new Dictionary<string, object?> { ["handle"] = ok.Handle });
            Assert.True(gotoDef.IsError is not true);
            var body = InProcessMcpFixture.Deserialize<SymbolDefinitionResultDto>(gotoDef);
            var loc = Assert.Single(body.Locations);
            Assert.Equal("InSource", loc.DeclarationAvailability);
            Assert.StartsWith(
                "SourceGenerator(CustomGenerator::CustomGenerator.MarkerGenerator@",
                loc.Origin,
                StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task OpenUntilReadyAsync(InProcessMcpFixture fx, string solution)
    {
        var open = await fx.Client.CallToolAsync(
            "workspace_open",
            new Dictionary<string, object?> { ["path"] = solution });
        Assert.True(open.IsError is not true);

        for (var i = 0; i < 40; i++)
        {
            var poll = await fx.Client.CallToolAsync("workspace_status", new Dictionary<string, object?>());
            var status = InProcessMcpFixture.Deserialize<WorkspaceStatusDto>(poll);
            if (status.Phase == "ready")
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("workspace did not become ready");
    }

    private static string CreateTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotnet-mcp-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
