using DotNetMcp.Core;
using DotNetMcp.Server;
using DotNetMcp.Xaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace DotNetMcp.Tests;

/// <summary>
/// Seam coverage for issue #562: symbol_preview_rename reports name-matched
/// source hits outside the write set under unverifiedMentions — metadata-name
/// string literals, unbound attribute spellings, and (opt-in) documentation
/// hits. Mentions are never written.
/// </summary>
public class RenameUnverifiedMentionSeamTests
{
    private const string AttributeSource = """
        namespace Ns
        {
            public class HubSendAttribute : System.Attribute { }
        }
        namespace OtherNs
        {
            public class HubSendAttribute : System.Attribute { }
        }
        """;

    private const string PipelineSource = """
        namespace Ns
        {
            public class Pipeline
            {
                public const string A = "Ns.HubSendAttribute";
                public const string B = "Ns.HubSendAttribute, Lib";
                public const string S = "Ns.Pipeline.M";
                public static readonly System.Type T = typeof(Ns.HubSendAttribute);
                public void M() { }
            }
        }
        """;

    private const string BoundUseSource = """
        using Ns;
        [HubSend]
        public class Bound { }
        """;

    private const string ElsewhereUseSource = """
        using OtherNs;
        [HubSend]
        public class Elsewhere { }
        """;

    private const string UnboundUseSource = """
        [HubSend]
        public class Unbound { }
        """;

    private const string AmbiguousUseSource = """
        using Ns;
        using OtherNs;
        [HubSend]
        public class Ambiguous { }
        """;

    private const string AnticipateUseSource = """
        [HubPublish]
        public class Anticipate { }
        """;

    private const string DocsSource = """
        // Ns.HubSendAttribute drives the generator table.
        namespace Ns
        {
            /// <summary>See <see cref="Ns.HubSendAttribute"/>.</summary>
            /// <remarks>Configured by Ns.HubSendAttribute at startup.</remarks>
            public class Docs { }
        }
        """;

    private const string InactiveSource = """
        namespace Ns
        {
        #if false
            Ns.HubSendAttribute Inactive;
        #endif
        }
        """;

    private const string PublicApiText = "Ns.HubSendAttribute";

    private const string AxamlText = """
        <Window xmlns="https://github.com/avaloniaui" Tag="Ns.HubSendAttribute" />
        """;

    private static RoslynLanguageAdapter Adapter() => new(new GeneratorQueryService());

    private static async Task<string> ResolveAsync(
        RoslynLanguageAdapter adapter, IWorkspaceSession session, string name)
    {
        var (resolved, error) = await adapter.ResolveByNameAsync(session, name);
        Assert.Null(error);
        Assert.NotNull(resolved);
        return resolved!.Handle;
    }

    private static UnverifiedMention SingleOfKind(RenamePreviewDraft draft, string kind, string pathSuffix) =>
        Assert.Single(
            draft.UnverifiedMentions!,
            m => m.Kind == kind && m.Path.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task metadata_name_strings_and_unbound_attributes_are_reported_not_written()
    {
        using var workspace = CreateCSharpWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var adapter = Adapter();
        var handle = await ResolveAsync(adapter, session, "Ns.HubSendAttribute");

        var (draft, error) = await adapter.BuildRenamePreviewAsync(session, handle, "HubPublishAttribute");
        Assert.Null(error);
        Assert.NotNull(draft);

        // metadataName: the plain literal and the assembly-qualified literal —
        // same Text, different lines, never written.
        var mentions = draft!.UnverifiedMentions!;
        var metadata = mentions.Where(m => m.Kind == UnverifiedMentionKind.MetadataName).ToList();
        Assert.Equal(2, metadata.Count);
        Assert.All(metadata, m =>
        {
            Assert.Equal("Ns.HubSendAttribute", m.Text);
            Assert.EndsWith("Pipeline.cs", m.Path, StringComparison.OrdinalIgnoreCase);
        });

        // unboundAttribute: bound-elsewhere, unbound, ambiguous, and the new-spelling hit.
        var elsewhere = SingleOfKind(draft, UnverifiedMentionKind.UnboundAttribute, "ElsewhereUse.cs");
        Assert.Equal("HubSend", elsewhere.Text);
        Assert.Contains("OtherNs.HubSendAttribute", elsewhere.Reason, StringComparison.Ordinal);
        var unbound = SingleOfKind(draft, UnverifiedMentionKind.UnboundAttribute, "UnboundUse.cs");
        Assert.Contains("unbound", unbound.Reason, StringComparison.OrdinalIgnoreCase);
        var ambiguous = SingleOfKind(draft, UnverifiedMentionKind.UnboundAttribute, "AmbiguousUse.cs");
        Assert.Contains("ambiguous", ambiguous.Reason, StringComparison.OrdinalIgnoreCase);
        var anticipate = SingleOfKind(draft, UnverifiedMentionKind.UnboundAttribute, "AnticipateUse.cs");
        Assert.Equal("HubPublish", anticipate.Text);
        Assert.Contains("new name", anticipate.Reason, StringComparison.OrdinalIgnoreCase);

        // No documentation mentions without includeDocMentions.
        Assert.DoesNotContain(mentions, m => m.Kind == UnverifiedMentionKind.Documentation);

        // The write set covers bound references only: the bound [HubSend] becomes
        // [HubPublish...], the typeof argument and the bound cref are rewritten,
        // and no mention path is written for its mention span.
        var boundUse = Assert.Single(draft.Documents, d =>
            d.Path.EndsWith("BoundUse.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("[HubPublish", boundUse.NewText, StringComparison.Ordinal);
        var pipeline = Assert.Single(draft.Documents, d =>
            d.Path.EndsWith("Pipeline.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("typeof(Ns.HubPublishAttribute)", pipeline.NewText, StringComparison.Ordinal);
        Assert.Contains("\"Ns.HubSendAttribute\"", pipeline.NewText, StringComparison.Ordinal);
        Assert.DoesNotContain(draft.Documents, d =>
            d.Path.EndsWith("ElsewhereUse.cs", StringComparison.OrdinalIgnoreCase) ||
            d.Path.EndsWith("UnboundUse.cs", StringComparison.OrdinalIgnoreCase) ||
            d.Path.EndsWith("AmbiguousUse.cs", StringComparison.OrdinalIgnoreCase) ||
            d.Path.EndsWith("AnticipateUse.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task doc_mentions_are_only_reported_with_include_doc_mentions()
    {
        using var workspace = CreateCSharpWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var adapter = Adapter();
        var handle = await ResolveAsync(adapter, session, "Ns.HubSendAttribute");

        var (draft, error) = await adapter.BuildRenamePreviewAsync(
            session, handle, "HubPublishAttribute", includeDocMentions: true);
        Assert.Null(error);
        Assert.NotNull(draft);
        var mentions = draft!.UnverifiedMentions!;

        // Comment, doc-comment, and disabled-text hits.
        var docHits = mentions
            .Where(m => m.Kind == UnverifiedMentionKind.Documentation)
            .ToList();
        Assert.True(docHits.Count >= 4, $"expected >= 4 documentation mentions, got {docHits.Count}");
        Assert.Contains(docHits, m => m.Path.EndsWith("DocsSource.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(docHits, m => m.Path.EndsWith("Inactive.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(docHits, m => m.Path.EndsWith("PublicAPI.Shipped.txt", StringComparison.OrdinalIgnoreCase));

        // The cref that binds to the renamed type is a written reference, not a mention.
        Assert.DoesNotContain(docHits, m =>
            m.Line == DocsSourceCrefLine &&
            m.Path.EndsWith("DocsSource.cs", StringComparison.OrdinalIgnoreCase));
        var docs = Assert.Single(draft.Documents, d =>
            d.Path.EndsWith("DocsSource.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("cref=\"Ns.HubPublishAttribute\"", docs.NewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task registered_xaml_document_is_not_a_doc_mention()
    {
        using var workspace = CreateCSharpWorkspace();
        var loaded = new LoadedSolution(workspace, workspace.CurrentSolution, warnings: []);
        await using var _ = loaded;
        var roslyn = Adapter();
        var languages = new LanguageAdapters([roslyn]);
        var index = new XamlUsageIndex(new XamlDocumentService(languages, roslyn), languages, roslyn);
        using var session = new WorkspaceSession(loaded, epoch: 1, xamlUsageSource: index);
        var handle = await ResolveAsync(roslyn, session, "Ns.HubSendAttribute");

        var (draft, error) = await roslyn.BuildRenamePreviewAsync(
            session, handle, "HubPublishAttribute", includeDocMentions: true);
        Assert.Null(error);
        Assert.DoesNotContain(draft!.UnverifiedMentions!, m =>
            m.Path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task member_rename_reports_member_qualified_string()
    {
        using var workspace = CreateCSharpWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var adapter = Adapter();
        var typeHandle = await ResolveAsync(adapter, session, "Ns.Pipeline");
        var (members, membersError) = await adapter.GetMembersAsync(session, typeHandle);
        Assert.Null(membersError);
        var mHandle = Assert.Single(members!.Items, i =>
            string.Equals(i.Summary.DisplayName, "M", StringComparison.Ordinal)).Handle;

        var (draft, error) = await adapter.BuildRenamePreviewAsync(session, mHandle, "Reset");
        Assert.Null(error);
        var mention = SingleOfKind(draft!, UnverifiedMentionKind.MetadataName, "Pipeline.cs");
        Assert.Equal("Ns.Pipeline.M", mention.Text);
    }

    [Fact]
    public async Task vb_metadata_string_and_unbound_attribute_are_reported()
    {
        using var workspace = CreateVbWorkspace();
        using var session = new FakeSession(workspace.CurrentSolution);
        var adapter = Adapter();
        var handle = await ResolveAsync(adapter, session, "Ns.HubSendAttribute");

        var (draft, error) = await adapter.BuildRenamePreviewAsync(session, handle, "HubPublishAttribute");
        Assert.Null(error);
        var mentions = draft!.UnverifiedMentions!;
        var literal = Assert.Single(mentions, m => m.Kind == UnverifiedMentionKind.MetadataName);
        Assert.Equal("Ns.HubSendAttribute", literal.Text);
        Assert.EndsWith("Uses.vb", literal.Path, StringComparison.OrdinalIgnoreCase);
        var attr = Assert.Single(mentions, m => m.Kind == UnverifiedMentionKind.UnboundAttribute);
        Assert.Equal("HubSend", attr.Text);
    }

    private static int DocsSourceCrefLine
    {
        get
        {
            var lines = SourceText.From(DocsSource).Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].ToString().Contains("see cref", StringComparison.Ordinal))
                {
                    return i + 1;
                }
            }

            throw new InvalidOperationException("DocsSource has no cref line.");
        }
    }

    private static AdhocWorkspace CreateCSharpWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "Lib",
            "Lib",
            LanguageNames.CSharp,
            filePath: @"C:\fake-mentions\Lib.csproj"));
        var docs = new (string Name, string Source)[]
        {
            ("Attributes.cs", AttributeSource),
            ("Pipeline.cs", PipelineSource),
            ("BoundUse.cs", BoundUseSource),
            ("ElsewhereUse.cs", ElsewhereUseSource),
            ("UnboundUse.cs", UnboundUseSource),
            ("AmbiguousUse.cs", AmbiguousUseSource),
            ("AnticipateUse.cs", AnticipateUseSource),
            ("DocsSource.cs", DocsSource),
            ("Inactive.cs", InactiveSource),
        };
        foreach (var (name, source) in docs)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId),
                name,
                SourceText.From(source),
                filePath: $@"C:\fake-mentions\{name}");
        }

        solution = solution.AddAdditionalDocument(
            DocumentId.CreateNewId(projectId),
            "PublicAPI.Shipped.txt",
            SourceText.From(PublicApiText),
            filePath: @"C:\fake-mentions\PublicAPI.Shipped.txt");
        solution = solution.AddAdditionalDocument(
            DocumentId.CreateNewId(projectId),
            "MainWindow.axaml",
            SourceText.From(AxamlText),
            filePath: @"C:\fake-mentions\MainWindow.axaml");
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace.");
        }

        return workspace;
    }

    private static AdhocWorkspace CreateVbWorkspace()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "VbLib",
            "VbLib",
            LanguageNames.VisualBasic,
            filePath: @"C:\fake-mentions\VbLib.vbproj"));
        const string attributeSource = """
            Namespace Ns
                Public Class HubSendAttribute
                    Inherits System.Attribute
                End Class
            End Namespace
            """;
        const string usesSource = """
            <HubSend>
            Public Class Uses
                Private Const S As String = "Ns.HubSendAttribute"
            End Class
            """;
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId), "Attr.vb", SourceText.From(attributeSource),
            filePath: @"C:\fake-mentions\Attr.vb");
        solution = solution.AddDocument(
            DocumentId.CreateNewId(projectId), "Uses.vb", SourceText.From(usesSource),
            filePath: @"C:\fake-mentions\Uses.vb");
        solution = solution.WithProjectCompilationOptions(
            projectId,
            new Microsoft.CodeAnalysis.VisualBasic.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        solution = solution.AddMetadataReference(
            projectId,
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        if (!workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Failed to apply AdhocWorkspace.");
        }

        return workspace;
    }

    private sealed class FakeSession : IWorkspaceSession
    {
        private readonly GeneratorRunCache _cache = new();

        public FakeSession(Solution solution, long epoch = 1)
        {
            Solution = solution;
            Epoch = epoch;
            FSharpSnapshot = new FSharpWorkspaceSnapshot(epoch, []);
        }

        public long Epoch { get; }

        public Solution Solution { get; }

        public FSharpWorkspaceSnapshot FSharpSnapshot { get; }

        public async Task<Compilation> GetCompilationAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default)
        {
            var project = Solution.GetProject(projectId)
                ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
            return await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Compilation was null for project '{project.Name}'.");
        }

        public async Task<Compilation> GetCompilationWithoutGeneratedTreesAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default)
        {
            var project = Solution.GetProject(projectId)
                ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
            var full = await GetCompilationAsync(projectId, cancellationToken).ConfigureAwait(false);
            return await GeneratorDriverRunner
                .StripGeneratedTreesFromProjectAsync(project, full, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<DriverRunSnapshot> GetGeneratorRunResultAsync(
            ProjectId projectId,
            CancellationToken cancellationToken = default)
        {
            var key = projectId.Id.ToString("D");
            if (_cache.TryGet(key, Epoch, out var cached))
            {
                return cached;
            }

            var project = Solution.GetProject(projectId)
                ?? throw new InvalidOperationException($"Project '{projectId.Id}' is not in the session solution.");
            var baseCompilation = await GetCompilationWithoutGeneratedTreesAsync(projectId, cancellationToken)
                .ConfigureAwait(false);
            var snapshot = GeneratorDriverRunner.RunDriver(project, baseCompilation, cancellationToken);
            _cache.Set(key, Epoch, snapshot);
            return snapshot;
        }

        public void Dispose()
        {
        }
    }
}
