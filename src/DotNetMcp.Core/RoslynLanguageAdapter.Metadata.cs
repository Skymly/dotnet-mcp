using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

public sealed record MetadataMember(string Name, string Kind, string Signature, string? DocumentationId, string? Xml);
public sealed record MetadataType(string AssemblyIdentity, string TypeName, string Kind, string? DocumentationId,
    string? Xml, PagedResult<MetadataMember> Members);

public sealed partial class RoslynLanguageAdapter
{
    private static MetadataNavigation? MetadataTarget(Project project, ISymbol symbol)
    {
        if (!symbol.Locations.Any(l => l.IsInMetadata) || symbol.ContainingAssembly is not { } assembly)
            return null;
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        return type is null ? null : new MetadataNavigation(project.Id.Id.ToString("D"), assembly.Identity.ToString(),
            MetadataName(type), symbol is INamedTypeSymbol ? null : symbol.Name);
    }

    // A display signature alone collides for the same CLR member reached through two extern aliases.
    // Keep the outer handle contract and carry declaration identity in its opaque signature field.
    private const string MetadataHandlePrefix = "pe:";

    private static string MetadataHandleSignature(MetadataNavigation target, string declarationId) =>
        MetadataHandlePrefix + string.Join('|', Uri.EscapeDataString(target.AssemblyIdentity),
            Uri.EscapeDataString(target.TypeName), Uri.EscapeDataString(declarationId));

    private static ISymbol? ResolveMetadataHandle(Compilation compilation, string signature, CancellationToken ct)
    {
        var fields = signature[MetadataHandlePrefix.Length..].Split('|');
        if (fields.Length != 3)
            return null;
        var identity = Uri.UnescapeDataString(fields[0]);
        var typeName = Uri.UnescapeDataString(fields[1]);
        var declarationId = Uri.UnescapeDataString(fields[2]);
        return BoundAssemblies(compilation, identity, ct)
            .Select(a => FindDeclaration(a, typeName, declarationId)).FirstOrDefault(s => s is not null);
    }

    private static ISymbol? FindDeclaration(IAssemblySymbol assembly, string typeName, string declarationId)
    {
        var type = assembly.GetTypeByMetadataName(typeName);
        if (type is null)
            return null;
        if (type.GetDocumentationCommentId() == declarationId)
            return type;
        return type.GetMembers().FirstOrDefault(m => m.GetDocumentationCommentId() == declarationId);
    }

    private static IEnumerable<IAssemblySymbol> BoundAssemblies(
        Compilation compilation, string? identity, CancellationToken ct)
    {
        foreach (var reference in compilation.References.OfType<PortableExecutableReference>())
        {
            ct.ThrowIfCancellationRequested();
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly &&
                (identity is null || assembly.Identity.ToString() == identity))
                yield return assembly;
        }
    }

    private static readonly SymbolDisplayFormat MetadataSignature = SymbolDisplayFormats.SignatureQualified
        .AddMemberOptions(SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeModifiers |
            SymbolDisplayMemberOptions.IncludeAccessibility)
        .AddParameterOptions(SymbolDisplayParameterOptions.IncludeDefaultValue | SymbolDisplayParameterOptions.IncludeName)
        .AddGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeConstraints)
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static async Task<(MetadataType? Result, SymbolQueryError? Error)> InspectMetadataAsync(
        IWorkspaceSession session, Project project, string typeName, string? assemblyIdentity, string? memberName,
        int? limit, string? cursor, CancellationToken cancellationToken)
    {
        Compilation compilation;
        try
        {
            compilation = await session.GetCompilationAsync(project.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return (null, new CompilationUnavailableError("No compilation is available for this project.",
                "Restore the selected project and reopen the workspace."));
        }

        var types = new List<INamedTypeSymbol>();
        foreach (var assembly in BoundAssemblies(compilation, assemblyIdentity, cancellationToken))
        {
            if (assembly.GetTypeByMetadataName(typeName) is { } type && !types.Contains(type, SymbolEqualityComparer.Default))
                types.Add(type);
        }
        if (types.Count == 0)
            return (null, new SymbolNotFoundError("No bound PE reference in this project provides that metadata type.",
                "Check the projectId, target framework, exact CLR type name and assemblyIdentity."));
        if (types.Count > 1)
            return (null, new SymbolAmbiguousError("The selected references contain this type in multiple assemblies: " +
                string.Join(" | ", types.Select(t => t.ContainingAssembly.Identity.ToString()).Order(StringComparer.Ordinal)),
                "Repeat with the exact assemblyIdentity."));

        var target = types[0];
        var identity = target.ContainingAssembly.Identity.ToString();
        var members = (memberName is null ? target.GetMembers() : target.GetMembers(memberName))
            .Where(m => m.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)
            .Where(m => m is not IMethodSymbol { AssociatedSymbol: not null })
            .OrderBy(m => m.GetDocumentationCommentId(), StringComparer.Ordinal).ToArray();
        var (page, error) = SoftBudgetPage.Page(members, session.Epoch, false, cursor, ClampLimit(limit),
            "project_inspect_metadata", $"{project.Id.Id}|{identity}|{typeName}|{memberName}",
            "No declared public/protected member matches.", "Declared public/protected members complete.");
        if (error is not null)
            return (null, error);

        static string? Document(ISymbol s, CancellationToken ct)
        {
            var xml = s.GetDocumentationCommentXml(expandIncludes: false, cancellationToken: ct);
            return string.IsNullOrWhiteSpace(xml) ? null : xml.Trim();
        }
        var items = page!.Items.Select(m => new MetadataMember(m.Name, m.Kind.ToString(), m.ToDisplayString(MetadataSignature),
            m.GetDocumentationCommentId(), Document(m, cancellationToken))).ToArray();
        return (new MetadataType(identity, typeName, target.TypeKind.ToString(), target.GetDocumentationCommentId(),
            Document(target, cancellationToken), new(items, page.Truncated, page.NextCursor, page.Message)), null);
    }
}
