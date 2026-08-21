using System.Collections.Concurrent;
using DotNetMcp.Core;
using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Symbols;
using FSharp.Compiler.Text;
using FcsRange = global::FSharp.Compiler.Text.Range;
using Microsoft.CodeAnalysis;
using Microsoft.FSharp.Control;
using Microsoft.FSharp.Core;
using RoslynProject = Microsoft.CodeAnalysis.Project;

namespace DotNetMcp.FSharp;

public sealed partial class FSharpSymbolQueryService : ILanguageAdapter
{
    private readonly SoftBudgetOptions _softBudgets;
    private readonly ConcurrentDictionary<string, string> _snapshotTexts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly FSharpChecker _checker;

    public bool OwnsLanguage(string languageToken) =>
        string.Equals(languageToken, SymbolQueryService.FSharpLanguage, StringComparison.Ordinal);

    public bool OwnsProject(RoslynProject project) =>
        project.Language == LanguageNames.FSharp;

    public bool SupportsCodeRefactoring => false;

    public FSharpSymbolQueryService(SoftBudgetOptions? softBudgets = null)
    {
        _softBudgets = softBudgets ?? SoftBudgetOptions.Default;
        var documentSource = FuncConvert.FromFunc<string, FSharpAsync<FSharpOption<ISourceText>?>>(TryReadSnapshot);
        _checker = FSharpChecker.Create(
            null,
            FSharpOption<bool>.Some(true),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            FSharpOption<DocumentSource>.Some(DocumentSource.NewCustom(documentSource)),
            null,
            null);
    }

    public async Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> ResolveByNameAsync(
        IWorkspaceSession session,
        string name,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, new SymbolNotFoundError(
                "Symbol name is empty.",
                "Pass a type or member name / FQN to symbol_resolve."));
        }

        var query = name.Trim();
        var matches = new List<FSharpCatalogItem>();
        foreach (var project in FSharpProjects(session.Solution, projectId, out var filterError))
        {
            if (filterError is not null)
            {
                return (null, filterError);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var catalog = FlattenCatalog(await CatalogAsync(project, cancellationToken).ConfigureAwait(false));
            matches.AddRange(catalog.Where(item => Matches(item, query)));
        }

        matches = matches
            .DistinctBy(m => (m.ProjectId, m.SignatureQualifiedName))
            .ToList();

        if (matches.Count == 0)
        {
            return (null, new SymbolNotFoundError(
                $"No symbol named '{name}' was found in the ready workspace.",
                "Confirm the name/FQN (and optional projectId), then call symbol_resolve again."));
        }

        if (matches.Count > 1)
        {
            var ids = string.Join(", ", matches.Select(m => m.ProjectId).Distinct());
            return (null, new SymbolAmbiguousError(
                $"Symbol '{name}' matched {matches.Count} candidates across projectId(s): {ids}.",
                "Pass projectId (and a more specific FQN if needed) to symbol_resolve to disambiguate."));
        }

        return (ToSuccess(matches[0]), null);
    }

    public async Task<(SymbolResolveSuccess? Success, SymbolQueryError? Error)> GetSummaryAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default)
    {
        var (item, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        return error is not null ? (null, error) : (ToSuccess(item!), null);
    }

    public async Task<(SymbolDefinitionSuccess? Success, SymbolQueryError? Error)> GetDefinitionAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default)
    {
        var (item, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (item!.Locations.Count == 0)
        {
            return (null, new DefinitionNotFoundError(
                $"No definition locations were found for '{item.SignatureQualifiedName}'.",
                "Confirm the handle with symbol_summary, or call symbol_resolve for a source symbol."));
        }

        return (new SymbolDefinitionSuccess(item.Locations), null);
    }

    public async Task<(PagedResult<MemberListItem>? Success, SymbolQueryError? Error)> GetMembersAsync(
        IWorkspaceSession session,
        string handle,
        int? limit = null,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var epoch = session.Epoch;
        var (item, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        if (!item!.IsContainer)
        {
            return (null, new SymbolNotFoundError(
                "Handle does not refer to a named type; member lists require a type SymbolHandle.",
                "Call symbol_resolve for a type name/FQN, then call symbol_members with that handle."));
        }

        var pageLimit = limit is null or < 1
            ? SymbolQueryService.DefaultMemberPageLimit
            : Math.Min(limit.Value, SymbolQueryService.MaxMemberPageLimit);
        var items = item.Members
            .OrderBy(m => m.SignatureQualifiedName, StringComparer.Ordinal)
            .Select(m =>
            {
                var success = ToSuccess(m);
                return new MemberListItem(success.Handle, success.Summary);
            })
            .ToList();

        return SoftBudgetPage.Page(
            items,
            epoch,
            budgetHit: false,
            cursor,
            pageLimit,
            "symbol_members",
            "Type has no members.",
            "Member page complete.",
            "the member list");
    }

    private async Task<(FSharpCatalogItem? Item, SymbolQueryError? Error)> TryResolveHandleAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken)
    {
        if (!SymbolHandle.TryParse(handle, out var parsed, out var parseError) || parsed is null)
        {
            return (null, new InvalidSymbolHandleError(
                parseError ?? "Handle format or checksum is invalid.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle; do not invent handles."));
        }

        if (!string.Equals(parsed.Language, SymbolQueryService.FSharpLanguage, StringComparison.Ordinal))
        {
            return (null, new InvalidSymbolHandleError(
                $"Unsupported language '{parsed.Language}'.",
                "Call symbol_resolve for an F# symbol to obtain a fsharp handle."));
        }

        var project = session.Solution.Projects.FirstOrDefault(p =>
            string.Equals(p.Id.Id.ToString("D"), parsed.ProjectId, StringComparison.OrdinalIgnoreCase));
        if (project is null || project.Language != LanguageNames.FSharp)
        {
            return (null, new SymbolNotFoundError(
                $"No F# project '{parsed.ProjectId}' is in the ready workspace.",
                "Call workspace_list_projects, then symbol_resolve for an F# symbol."));
        }

        var catalog = FlattenCatalog(await CatalogAsync(project, cancellationToken).ConfigureAwait(false));
        var hit = catalog.FirstOrDefault(item =>
            string.Equals(item.SignatureQualifiedName, parsed.SignatureQualifiedName, StringComparison.Ordinal) ||
            string.Equals(item.DisplayName, parsed.SignatureQualifiedName, StringComparison.Ordinal) ||
            item.SignatureQualifiedName.EndsWith("." + parsed.SignatureQualifiedName, StringComparison.Ordinal));
        if (hit is null)
        {
            return (null, new SymbolNotFoundError(
                $"Symbol '{parsed.SignatureQualifiedName}' was not found in project '{parsed.ProjectId}'.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle."));
        }

        return (hit, null);
    }

    public static string CompileLibrary(string outputDll, IReadOnlyList<string> sourceFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDll);
        var dir = Path.GetDirectoryName(outputDll);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var checker = FSharpChecker.Create(
            null, FSharpOption<bool>.Some(true),
            null, null, null, null, null, null, null, null, null, null, null, null);
        var argv = BuildCompilerArgs(outputDll, sourceFiles).ToList();
        argv.Insert(0, "fsc.dll");
        var result = FSharpAsync.RunSynchronously(
            checker.Compile(argv.ToArray(), userOpName: null),
            timeout: null,
            cancellationToken: null);
        if (result.Item2 != null && OptionModule.IsSome(result.Item2))
        {
            throw new InvalidOperationException(result.Item2.Value.ToString());
        }

        if (!File.Exists(outputDll))
        {
            var errors = string.Join(" | ", result.Item1.Select(d => d.Message));
            throw new InvalidOperationException("F# compile produced no DLL. " + errors);
        }

        return outputDll;
    }

    private async Task<IReadOnlyList<FSharpCatalogItem>> CatalogAsync(
        RoslynProject project,
        CancellationToken cancellationToken)
    {
        var (items, _, _) = await CheckProjectAsync(project, cancellationToken).ConfigureAwait(false);
        return items;
    }

    private async Task<(IReadOnlyList<FSharpCatalogItem> Items, FSharpCheckProjectResults? Check, IReadOnlyList<(string Path, string Text)> Sources)> CheckProjectAsync(
        RoslynProject project,
        CancellationToken cancellationToken)
    {
        var sources = new List<(string Path, string Text)>();
        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.FilePath is null ||
                !document.FilePath.EndsWith(".fs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            sources.Add((Path.GetFullPath(document.FilePath), text));
        }

        if (sources.Count == 0)
        {
            return ([], null, sources);
        }

        PublishSnapshots(sources);

        var projectFile = project.FilePath ?? Path.Combine(
            Path.GetDirectoryName(sources[0].Path) ?? Path.GetTempPath(),
            project.Name + ".fsproj");
        var dllName = Path.ChangeExtension(projectFile, ".dll");
        var argv = BuildCompilerArgs(dllName, sources.Select(s => s.Path));
        var options = _checker.GetProjectOptionsFromCommandLineArgs(projectFile, argv, null, null, null);
        foreach (var (path, _) in sources)
        {
            await FSharpAsync.StartAsTask(
                    _checker.NotifyFileChanged(path, options, userOpName: null),
                    taskCreationOptions: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var check = await FSharpAsync.StartAsTask(
                _checker.ParseAndCheckProject(options, userOpName: null),
                taskCreationOptions: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var projectId = project.Id.Id.ToString("D");
        var items = new List<FSharpCatalogItem>();
        WalkEntities(projectId, check.AssemblySignature.Entities, sources, items);
        return (items, check, sources);
    }

    private static void WalkEntities(
        string projectId,
        IEnumerable<FSharpEntity> entities,
        IReadOnlyList<(string Path, string Text)> sources,
        List<FSharpCatalogItem> sink)
    {
        foreach (var entity in entities)
        {
            if (entity.IsFSharpAbbreviation || entity.IsArrayType || entity.IsProvided)
            {
                continue;
            }

            var fullName = EntityFullName(entity);
            if (string.IsNullOrWhiteSpace(fullName))
            {
                continue;
            }

            var members = new List<FSharpCatalogItem>();
            foreach (var member in entity.MembersFunctionsAndValues)
            {
                if (member.IsCompilerGenerated || member.IsPropertyGetterMethod || member.IsPropertySetterMethod)
                {
                    continue;
                }

                var memberName = MemberFullName(entity, member);
                members.Add(new FSharpCatalogItem(
                    projectId,
                    memberName,
                    MemberKind(member),
                    member.DisplayName,
                    fullName,
                    IsContainer: false,
                    Locations: LocationsOf(member.DeclarationLocation, sources),
                    Members: []));
            }

            var baseName = TryBaseTypeName(entity);
            var interfaces = TryInterfaceNames(entity);
            sink.Add(new FSharpCatalogItem(
                projectId,
                fullName,
                "NamedType",
                entity.DisplayName,
                entity.AccessPath is "." or "" ? null : entity.AccessPath,
                IsContainer: true,
                Locations: LocationsOf(entity.DeclarationLocation, sources),
                Members: members,
                BaseTypeName: baseName,
                InterfaceNames: interfaces,
                IsInterface: entity.IsInterface));

            WalkEntities(projectId, entity.NestedEntities, sources, sink);
        }
    }

    private static IReadOnlyList<RoslynProject> FSharpProjects(
        Solution solution,
        string? projectId,
        out SymbolQueryError? error)
    {
        error = null;
        var all = solution.Projects.Where(p => p.Language == LanguageNames.FSharp).ToArray();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return all;
        }

        var match = all
            .Where(p => string.Equals(p.Id.Id.ToString("D"), projectId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (match.Length == 0)
        {
            error = new SymbolNotFoundError(
                $"F# project '{projectId}' was not found in the ready workspace.",
                "Call workspace_list_projects to obtain a fsharp projectId.");
        }

        return match;
    }

    private static bool Matches(FSharpCatalogItem item, string query) =>
        string.Equals(item.SignatureQualifiedName, query, StringComparison.Ordinal) ||
        string.Equals(item.DisplayName, query, StringComparison.Ordinal) ||
        item.SignatureQualifiedName.EndsWith("." + query, StringComparison.Ordinal);

    private static SymbolResolveSuccess ToSuccess(FSharpCatalogItem item)
    {
        var handle = SymbolHandle.Create(
            SymbolQueryService.FSharpLanguage,
            item.ProjectId,
            item.SignatureQualifiedName);
        var summary = new SymbolSummary(
            Kind: item.Kind,
            DisplayName: item.DisplayName,
            ContainingSymbol: item.ContainingSymbol,
            Accessibility: "Public",
            ProjectId: item.ProjectId,
            Language: SymbolQueryService.FSharpLanguage);
        return new SymbolResolveSuccess(handle.Format(), summary);
    }

    private static string EntityFullName(FSharpEntity entity)
    {
        var name = entity.TryFullName;
        if (OptionModule.IsSome(name) && !string.IsNullOrWhiteSpace(name.Value))
        {
            return name.Value;
        }

        return string.IsNullOrWhiteSpace(entity.AccessPath) || entity.AccessPath is "."
            ? entity.DisplayName
            : entity.AccessPath + "." + entity.DisplayName;
    }

    private static string MemberFullName(FSharpEntity owner, FSharpMemberOrFunctionOrValue member)
    {
        if (!string.IsNullOrWhiteSpace(member.FullName) && member.FullName.Contains('.', StringComparison.Ordinal))
        {
            return member.FullName;
        }

        var ownerName = EntityFullName(owner);
        var leaf = string.IsNullOrWhiteSpace(member.FullName) ? member.DisplayName : member.FullName;
        return string.IsNullOrWhiteSpace(ownerName) ? leaf : ownerName + "." + leaf;
    }

    private static string MemberKind(FSharpMemberOrFunctionOrValue member)
    {
        if (member.IsProperty)
        {
            return "Property";
        }

        if (member.IsMember || member.IsFunction || member.IsConstructor)
        {
            return "Method";
        }

        return "Field";
    }

    private static IReadOnlyList<SymbolLocation> LocationsOf(
        FcsRange range,
        IReadOnlyList<(string Path, string Text)> sources)
    {
        var file = range.FileName;
        var source = sources.FirstOrDefault(s => SameDocumentPath(s.Path, file));
        if (string.IsNullOrWhiteSpace(source.Path))
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                return [new SymbolLocation(DeclarationAvailability.None, SymbolOrigin.Handwritten, null, null, null)];
            }

            return
            [
                new SymbolLocation(
                    DeclarationAvailability.InSource,
                    SymbolOrigin.Handwritten,
                    file,
                    Start: null,
                    Length: null)
            ];
        }

        var (start, length) = ToSpan(source.Text, range);
        return
        [
            new SymbolLocation(
                DeclarationAvailability.InSource,
                SymbolOrigin.Handwritten,
                source.Path,
                start,
                length)
        ];
    }

    private static (int Start, int Length) ToSpan(string text, FcsRange range)
    {
        var start = OffsetOf(text, range.StartLine, range.StartColumn);
        var end = OffsetOf(text, range.EndLine, range.EndColumn);
        if (end < start)
        {
            end = start;
        }

        return (start, end - start);
    }

    private static int OffsetOf(string text, int line1Based, int column0Based)
    {
        var line = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (line == line1Based)
            {
                return Math.Min(text.Length, i + Math.Max(0, column0Based));
            }

            if (text[i] == '\n')
            {
                line++;
            }
        }

        return text.Length;
    }

    private FSharpAsync<FSharpOption<ISourceText>?> TryReadSnapshot(string fileName)
    {
        if (TryGetSnapshot(fileName, out _, out var text))
        {
            return FSharpAsync.AwaitTask(
                Task.FromResult<FSharpOption<ISourceText>?>(FSharpOption<ISourceText>.Some(SourceText.ofString(text))));
        }

        return FSharpAsync.AwaitTask(Task.FromResult<FSharpOption<ISourceText>?>(null));
    }

    private void PublishSnapshots(IReadOnlyList<(string Path, string Text)> sources)
    {
        foreach (var (path, text) in sources)
        {
            _snapshotTexts[path] = text;
            var full = TryFullPath(path);
            if (full is not null)
            {
                _snapshotTexts[full] = text;
            }
        }
    }

    private bool TryGetSnapshot(string? file, out string path, out string text)
    {
        path = file ?? string.Empty;
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(file))
        {
            return false;
        }

        if (_snapshotTexts.TryGetValue(file, out text!))
        {
            path = TryFullPath(file) ?? file;
            return true;
        }

        var full = TryFullPath(file);
        if (full is not null && _snapshotTexts.TryGetValue(full, out text!))
        {
            path = full;
            return true;
        }

        return false;
    }

    private static bool SameDocumentPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftFull = TryFullPath(left);
        var rightFull = TryFullPath(right);
        return leftFull is not null &&
               rightFull is not null &&
               string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string[] BuildCompilerArgs(string dllName, IEnumerable<string> sourceFiles)
    {
        var args = new List<string>
        {
            "--simpleresolution",
            "--targetprofile:netcore",
            "--target:library",
            "--nowin32manifest",
            "--nocopyfsharpcore",
            "--out:" + dllName,
        };

        foreach (var reference in CompilerReferences())
        {
            args.Add("-r:" + reference);
        }

        args.AddRange(sourceFiles);
        return args.ToArray();
    }

    private static IReadOnlyList<string> CompilerReferences()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
            "System.Runtime.Extensions.dll",
            "System.Console.dll",
            "netstandard.dll",
            "FSharp.Core.dll",
        };

        var refs = tpa
            .Where(path => wanted.Contains(Path.GetFileName(path)))
            .ToList();

        var fsharpCore = typeof(FSharpOption<>).Assembly.Location;
        if (refs.TrueForAll(path => !path.Equals(fsharpCore, StringComparison.OrdinalIgnoreCase)))
        {
            refs.Add(fsharpCore);
        }

        return refs;
    }

    private static string? TryBaseTypeName(FSharpEntity entity)
    {
        try
        {
            if (entity.IsInterface)
            {
                return null;
            }

            var baseType = entity.BaseType;
            if (!OptionModule.IsSome(baseType) || !baseType.Value.HasTypeDefinition)
            {
                return null;
            }

            var name = EntityFullName(baseType.Value.TypeDefinition);
            return name is "System.Object" or "obj" ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> TryInterfaceNames(FSharpEntity entity)
    {
        try
        {
            return entity.DeclaredInterfaces
                .Where(static t => t.HasTypeDefinition)
                .Select(static t => EntityFullName(t.TypeDefinition))
                .Where(static n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private sealed record FSharpCatalogItem(
        string ProjectId,
        string SignatureQualifiedName,
        string Kind,
        string DisplayName,
        string? ContainingSymbol,
        bool IsContainer,
        IReadOnlyList<SymbolLocation> Locations,
        IReadOnlyList<FSharpCatalogItem> Members,
        string? BaseTypeName = null,
        IReadOnlyList<string>? InterfaceNames = null,
        bool IsInterface = false);

    private static IEnumerable<FSharpCatalogItem> FlattenCatalog(IEnumerable<FSharpCatalogItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in FlattenCatalog(item.Members))
            {
                yield return child;
            }
        }
    }
}