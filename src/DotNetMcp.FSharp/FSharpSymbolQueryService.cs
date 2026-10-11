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
    private readonly ConcurrentDictionary<(long Epoch, string Path), string> _snapshotTexts = new(EpochPathComparer.Instance);
    private readonly ConcurrentDictionary<(long Epoch, string Path), string> _notifiedTexts = new(EpochPathComparer.Instance);
    private readonly ConcurrentDictionary<long, int> _epochsInFlight = new();
    private readonly AsyncLocal<long?> _currentEpoch = new();
    private readonly FSharpChecker _checker;

    internal int FileChangeNotifications { get; private set; }

    public bool OwnsLanguage(string languageToken) =>
        string.Equals(languageToken, LanguageAdapters.FSharpLanguage, StringComparison.Ordinal);

    public bool OwnsProject(RoslynProject project) =>
        project.Language == LanguageNames.FSharp ||
        (project.FilePath?.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ?? false);

    public bool SupportsCodeRefactoring => false;

    public bool SupportsDiagnosticFix => false;

    public bool SupportsSourceGenerators => false;

    public bool SupportsDynamicInvocations => false;

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
        foreach (var project in FSharpProjects(session.FSharpSnapshot, projectId, out var filterError))
        {
            if (filterError is not null)
            {
                return (null, filterError);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var catalog = FlattenCatalog(await CatalogAsync(project, session.FSharpSnapshot.Epoch, cancellationToken).ConfigureAwait(false));
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
            var exact = matches
                .Where(m => string.Equals(m.SignatureQualifiedName, query, StringComparison.Ordinal))
                .ToList();
            if (exact.Count == 1)
            {
                matches = exact;
            }
            else
            {
                var containers = matches.Where(static m => m.IsContainer).ToList();
                if (containers.Count == 1)
                {
                    matches = containers;
                }
                else
                {
                    var ids = string.Join(", ", matches.Select(m => m.ProjectId).Distinct());
                    return (null, new SymbolAmbiguousError(
                        $"Symbol '{name}' matched {matches.Count} candidates across projectId(s): {ids}.",
                        "Pass projectId (and a more specific FQN if needed) to symbol_resolve to disambiguate."));
                }
            }
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

    public async Task<(SymbolAttributionSuccess? Success, SymbolQueryError? Error)> GetAttributionAsync(
        IWorkspaceSession session,
        string handle,
        CancellationToken cancellationToken = default)
    {
        var (_, error) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        // FCS has no GeneratorDriver reconciliation. A constant Handwritten result would look verified.
        return (null, new GeneratorLanguageNotSupportedError(
            "Source-generator attribution is not available for this language.",
            "Call symbol_attribution on a C# or VB SymbolHandle."));
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
            ? LanguageAdapters.DefaultMemberPageLimit
            : Math.Min(limit.Value, LanguageAdapters.MaxMemberPageLimit);
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
            handle,
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

        if (!string.Equals(parsed.Language, LanguageAdapters.FSharpLanguage, StringComparison.Ordinal))
        {
            return (null, new InvalidSymbolHandleError(
                $"Unsupported language '{parsed.Language}'.",
                "Call symbol_resolve for an F# symbol to obtain a fsharp handle."));
        }

        var project = session.FSharpSnapshot.FindProject(parsed.ProjectId);
        if (project is null)
        {
            return (null, new SymbolNotFoundError(
                $"No F# project '{parsed.ProjectId}' is in the ready workspace.",
                "Call workspace_list_projects, then symbol_resolve for an F# symbol."));
        }

        var catalog = FlattenCatalog(await CatalogAsync(project, session.FSharpSnapshot.Epoch, cancellationToken).ConfigureAwait(false));
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
        FSharpProjectSnapshot project,
        long epoch,
        CancellationToken cancellationToken)
    {
        var (items, _, _) = await CheckProjectAsync(project, epoch, cancellationToken).ConfigureAwait(false);
        return items;
    }

    private async Task<(IReadOnlyList<FSharpCatalogItem> Items, FSharpCheckProjectResults? Check, IReadOnlyList<(string Path, string Text)> Sources)> CheckProjectAsync(
        FSharpProjectSnapshot project,
        long epoch,
        CancellationToken cancellationToken)
    {
        var previousEpoch = _currentEpoch.Value;
        EnterEpoch(epoch);
        _currentEpoch.Value = epoch;
        try
        {
            return await CheckProjectCoreAsync(project, epoch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _currentEpoch.Value = previousEpoch;
            ExitEpoch(epoch);
        }
    }

    private async Task<(IReadOnlyList<FSharpCatalogItem> Items, FSharpCheckProjectResults? Check, IReadOnlyList<(string Path, string Text)> Sources)> CheckProjectCoreAsync(
        FSharpProjectSnapshot project,
        long epoch,
        CancellationToken cancellationToken)
    {
        var sources = new List<(string Path, string Text)>();
        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sources.Add((document.Path, document.Text));
        }

        if (sources.Count == 0)
        {
            return ([], null, sources);
        }

        PublishSnapshots(epoch, sources);

        var projectFile = project.FilePath ?? Path.Combine(
            Path.GetDirectoryName(sources[0].Path) ?? Path.GetTempPath(),
            project.Name + ".fsproj");
        var dllName = Path.ChangeExtension(projectFile, ".dll");
        var argv = BuildCompilerArgs(dllName, sources.Select(s => s.Path), project.Defines, project.References);
        var options = _checker.GetProjectOptionsFromCommandLineArgs(projectFile, argv, null, null, null);
        foreach (var (path, sourceText) in sources)
        {
            var key = (epoch, path);
            if (_notifiedTexts.TryGetValue(key, out var previous) &&
                string.Equals(previous, sourceText, StringComparison.Ordinal))
            {
                continue;
            }

            await FSharpAsync.StartAsTask(
                    _checker.NotifyFileChanged(path, options, userOpName: null),
                    taskCreationOptions: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _notifiedTexts[key] = sourceText;
            FileChangeNotifications++;
        }

        var check = await FSharpAsync.StartAsTask(
                _checker.ParseAndCheckProject(options, userOpName: null),
                taskCreationOptions: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var projectId = project.ProjectId;
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
                if (members.Any(m => string.Equals(m.SignatureQualifiedName, memberName, StringComparison.Ordinal)))
                {
                    var withReturn = memberName + ":" + FormatReturnType(member);
                    memberName = members.Any(m => string.Equals(m.SignatureQualifiedName, withReturn, StringComparison.Ordinal))
                        ? memberName + "@" + member.DeclarationLocation.StartLine + ":" + member.DeclarationLocation.StartColumn
                        : withReturn;
                }

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

    private static IReadOnlyList<FSharpProjectSnapshot> FSharpProjects(
        FSharpWorkspaceSnapshot snapshot,
        string? projectId,
        out SymbolQueryError? error)
    {
        error = null;
        var all = snapshot.Projects.ToArray();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return all;
        }

        var match = all
            .Where(p => string.Equals(p.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (match.Length == 0)
        {
            error = new SymbolNotFoundError(
                $"F# project '{projectId}' was not found in the ready workspace.",
                "Call workspace_list_projects to obtain a fsharp projectId.");
        }

        return match;
    }

    private static bool Matches(FSharpCatalogItem item, string query)
    {
        if (string.Equals(item.SignatureQualifiedName, query, StringComparison.Ordinal) ||
            string.Equals(item.DisplayName, query, StringComparison.Ordinal) ||
            item.SignatureQualifiedName.EndsWith("." + query, StringComparison.Ordinal))
        {
            return true;
        }

        var unspecialized = StripParameterSignature(item.SignatureQualifiedName);
        return string.Equals(unspecialized, query, StringComparison.Ordinal) ||
               unspecialized.EndsWith("." + query, StringComparison.Ordinal);
    }

    private static string StripParameterSignature(string signatureQualifiedName)
    {
        var index = signatureQualifiedName.IndexOf('(');
        return index < 0 ? signatureQualifiedName : signatureQualifiedName[..index];
    }

    private static SymbolResolveSuccess ToSuccess(FSharpCatalogItem item)
    {
        var handle = SymbolHandle.Create(
            LanguageAdapters.FSharpLanguage,
            item.ProjectId,
            item.SignatureQualifiedName);
        var summary = new SymbolSummary(
            Kind: item.Kind,
            DisplayName: item.DisplayName,
            ContainingSymbol: item.ContainingSymbol,
            Accessibility: "Public",
            ProjectId: item.ProjectId,
            Language: LanguageAdapters.FSharpLanguage);
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
        member = ResolveAccessor(member, owner) ?? member;

        string baseName;
        if (member.IsConstructor)
        {
            baseName = EntityFullName(owner);
        }
        else if (!string.IsNullOrWhiteSpace(member.FullName) && member.FullName.Contains('.', StringComparison.Ordinal))
        {
            baseName = member.FullName;
        }
        else
        {
            var ownerName = EntityFullName(owner);
            var leaf = string.IsNullOrWhiteSpace(member.FullName) ? member.DisplayName : member.FullName;
            baseName = string.IsNullOrWhiteSpace(ownerName) ? leaf : ownerName + "." + leaf;
        }

        var signature = FormatParameterSignature(member);
        return string.IsNullOrEmpty(signature) ? baseName : baseName + signature;
    }

    private static FSharpMemberOrFunctionOrValue? ResolveAccessor(
        FSharpMemberOrFunctionOrValue member,
        FSharpEntity owner)
    {
        if (!member.IsPropertyGetterMethod && !member.IsPropertySetterMethod)
        {
            return null;
        }

        try
        {
            return owner.MembersFunctionsAndValues.FirstOrDefault(candidate =>
                candidate.IsProperty &&
                ((candidate.HasGetterMethod && SameAccessor(candidate.GetterMethod, member)) ||
                 (candidate.HasSetterMethod && SameAccessor(candidate.SetterMethod, member))));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SameAccessor(FSharpMemberOrFunctionOrValue? accessor, FSharpMemberOrFunctionOrValue member) =>
        accessor is not null &&
        (accessor.Equals(member) ||
         (string.Equals(accessor.FullName, member.FullName, StringComparison.Ordinal) &&
          string.Equals(accessor.DisplayName, member.DisplayName, StringComparison.Ordinal)));

    private static string FormatParameterSignature(FSharpMemberOrFunctionOrValue member)
    {
        try
        {
            var groups = member.CurriedParameterGroups;
            if (groups.Count == 0)
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var group in groups)
            {
                var formatted = group.Select(static parameter => FormatFSharpType(parameter.Type)).ToList();
                if (formatted is ["Microsoft.FSharp.Core.Unit"])
                {
                    formatted.Clear();
                }

                builder.Append('(');
                builder.Append(string.Join(",", formatted));
                builder.Append(')');
            }

            return builder.ToString();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string FormatReturnType(FSharpMemberOrFunctionOrValue member)
    {
        try
        {
            var parameter = member.ReturnParameter;
            return parameter is null ? "?" : FormatFSharpType(parameter.Type);
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private static string FormatFSharpType(FSharpType type)
    {
        try
        {
            while (type.IsAbbreviation)
            {
                type = type.AbbreviatedType;
            }

            if (type.IsGenericParameter)
            {
                return "'" + type.GenericParameter.Name;
            }

            var generic = type.GenericArguments;
            if (type.IsFunctionType && generic.Count == 2)
            {
                return FormatFSharpType(generic[0]) + "->" + FormatFSharpType(generic[1]);
            }

            if (type.IsTupleType && generic.Count > 0)
            {
                return string.Join("*", generic.Select(FormatFSharpType));
            }

            if (type.HasTypeDefinition)
            {
                var definition = type.TypeDefinition;
                if (definition.IsArrayType)
                {
                    var brackets = definition.ArrayRank <= 1 ? "[]" : "[" + new string(',', definition.ArrayRank - 1) + "]";
                    return (generic.Count > 0 ? FormatFSharpType(generic[0]) : "?") + brackets;
                }

                var name = StripArity(EntityFullName(definition));
                return generic.Count == 0
                    ? name
                    : name + "<" + string.Join(",", generic.Select(FormatFSharpType)) + ">";
            }

            return type.ToString() ?? "?";
        }
        catch (Exception)
        {
            return type.ToString() ?? "?";
        }
    }

    private static string StripArity(string name)
    {
        var index = name.IndexOf('`');
        while (index >= 0)
        {
            var end = index + 1;
            while (end < name.Length && char.IsDigit(name[end]))
            {
                end++;
            }

            name = string.Concat(name.AsSpan(0, index), name.AsSpan(end));
            index = name.IndexOf('`', index);
        }

        return name;
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
        if (TryReadCurrentEpoch(fileName, out var text))
        {
            return FSharpAsync.AwaitTask(
                Task.FromResult<FSharpOption<ISourceText>?>(FSharpOption<ISourceText>.Some(SourceText.ofString(text))));
        }

        return FSharpAsync.AwaitTask(Task.FromResult<FSharpOption<ISourceText>?>(null));
    }

    private bool TryReadCurrentEpoch(string fileName, out string text)
    {
        text = string.Empty;
        if (_currentEpoch.Value is long epoch && TryGetSnapshot(epoch, fileName, out _, out text))
        {
            return true;
        }

        string? only = null;
        foreach (var pair in _snapshotTexts)
        {
            if (!string.Equals(pair.Key.Path, fileName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(TryFullPath(pair.Key.Path), fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (only is null)
            {
                only = pair.Value;
                continue;
            }

            if (!string.Equals(only, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (only is null)
        {
            return false;
        }

        text = only;
        return true;
    }

    internal void PublishSnapshots(long epoch, IReadOnlyList<(string Path, string Text)> sources)
    {
        foreach (var (path, text) in sources)
        {
            _snapshotTexts[(epoch, path)] = text;
            var full = TryFullPath(path);
            if (full is not null)
            {
                _snapshotTexts[(epoch, full)] = text;
            }
        }

        DropIdleEpochsOlderThan(epoch);
    }

    internal bool TryGetSnapshot(long epoch, string? file, out string path, out string text)
    {
        path = file ?? string.Empty;
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(file))
        {
            return false;
        }

        if (_snapshotTexts.TryGetValue((epoch, file), out text!))
        {
            path = TryFullPath(file) ?? file;
            return true;
        }

        var full = TryFullPath(file);
        if (full is not null && _snapshotTexts.TryGetValue((epoch, full), out text!))
        {
            path = full;
            return true;
        }

        return false;
    }

    internal void EnterEpochForTests(long epoch) => EnterEpoch(epoch);

    internal void ExitEpochForTests(long epoch) => ExitEpoch(epoch);

    private EpochHold EnterRequest(long epoch) => new(this, epoch);

    private void EnterEpoch(long epoch)
    {
        _epochsInFlight.AddOrUpdate(epoch, 1, static (_, count) => count + 1);
    }

    private readonly struct EpochHold : IDisposable
    {
        private readonly FSharpSymbolQueryService _owner;
        private readonly long _epoch;
        private readonly long? _previous;

        public EpochHold(FSharpSymbolQueryService owner, long epoch)
        {
            _owner = owner;
            _epoch = epoch;
            _previous = owner._currentEpoch.Value;
            owner.EnterEpoch(epoch);
            owner._currentEpoch.Value = epoch;
        }

        public void Dispose()
        {
            _owner._currentEpoch.Value = _previous;
            _owner.ExitEpoch(_epoch);
        }
    }

    private void ExitEpoch(long epoch)
    {
        var left = _epochsInFlight.AddOrUpdate(epoch, 0, static (_, count) => count - 1);
        if (left <= 0)
        {
            _epochsInFlight.TryRemove(new KeyValuePair<long, int>(epoch, left));
        }
    }

    private void DropIdleEpochsOlderThan(long epoch)
    {
        foreach (var key in _snapshotTexts.Keys)
        {
            if (key.Epoch < epoch && !_epochsInFlight.ContainsKey(key.Epoch))
            {
                _snapshotTexts.TryRemove(key, out _);
            }
        }

        foreach (var key in _notifiedTexts.Keys)
        {
            if (key.Epoch < epoch && !_epochsInFlight.ContainsKey(key.Epoch))
            {
                _notifiedTexts.TryRemove(key, out _);
            }
        }
    }

    private sealed class EpochPathComparer : IEqualityComparer<(long Epoch, string Path)>
    {
        public static readonly EpochPathComparer Instance = new();

        public bool Equals((long Epoch, string Path) x, (long Epoch, string Path) y) =>
            x.Epoch == y.Epoch && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((long Epoch, string Path) obj) =>
            HashCode.Combine(obj.Epoch, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path));
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

    private static string[] BuildCompilerArgs(
        string dllName,
        IEnumerable<string> sourceFiles,
        IEnumerable<string>? defines = null,
        IEnumerable<string>? extraReferences = null)
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

        if (defines is not null)
        {
            foreach (var define in defines)
            {
                if (!string.IsNullOrWhiteSpace(define))
                {
                    args.Add("--define:" + define);
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in CompilerReferences().Concat(extraReferences ?? []))
        {
            if (string.IsNullOrWhiteSpace(reference) || !seen.Add(reference))
            {
                continue;
            }

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
            if (!OptionModule.IsSome(baseType))
            {
                return null;
            }

            var resolved = baseType.Value;
            while (resolved.IsAbbreviation)
            {
                resolved = resolved.AbbreviatedType;
            }

            if (!resolved.HasTypeDefinition)
            {
                return null;
            }

            var name = EntityFullName(resolved.TypeDefinition);
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
