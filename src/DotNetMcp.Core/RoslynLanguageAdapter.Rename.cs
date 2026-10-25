using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace DotNetMcp.Core;

public sealed partial class RoslynLanguageAdapter
{
    public async Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildRenamePreviewAsync(
        IWorkspaceSession session,
        string handle,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            return (null, new InvalidRenameNameError(
                "New name must be a single identifier.",
                "Pass a C# identifier (no qualification) as newName."));
        }

        if (!SymbolHandle.TryParse(handle, out var parsed, out var parseError) || parsed is null)
        {
            return (null, new InvalidSymbolHandleError(
                parseError ?? "Handle format or checksum is invalid.",
                "Call symbol_resolve with a name/FQN to obtain a fresh SymbolHandle; do not invent handles."));
        }

        if (!OwnsLanguage(parsed.Language))
        {
            return (null, new InvalidSymbolHandleError(
                $"Unsupported language '{parsed.Language}'.",
                "Call symbol_resolve for a C# or VB symbol to obtain a csharp or vb handle."));
        }

        if (!IsValidIdentifier(parsed.Language, newName))
        {
            return (null, new InvalidRenameNameError(
                "New name must be a single identifier.",
                string.Equals(parsed.Language, VbLanguage, StringComparison.Ordinal)
                    ? "Pass a VB identifier (no qualification) as newName."
                    : "Pass a C# identifier (no qualification) as newName."));
        }

        var (project, symbol, resolveError) = await TryResolveHandleAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (resolveError is not null)
        {
            return (null, resolveError);
        }

        var (attribution, attrError) = await GetAttributionAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (attrError is not null)
        {
            return (null, attrError);
        }

        var xamlSource = (session as IXamlUsageSourceProvider)?.XamlUsageSource;

        if (attribution!.Attribution.OriginKind == SymbolOrigin.SourceGenerator)
        {
            // Escape hatch (ADR-0006): a generated field that an x:Name usage in
            // its defining project points at is renamed through the XAML value
            // plus handwritten references — the generated declaration
            // regenerates. Every other generated declaration stays refused.
            if (xamlSource is not null &&
                symbol is IFieldSymbol &&
                await HasMatchingXNameUsageAsync(
                        session, project!, symbol, xamlSource, cancellationToken)
                    .ConfigureAwait(false))
            {
                return await BuildXNameFieldRenameDraftAsync(
                        session, handle, symbol, newName, xamlSource, cancellationToken)
                    .ConfigureAwait(false);
            }

            return (null, new GeneratedSymbolRenameRefusedError(
                "SourceGenerator declarations cannot be renamed.",
                "Change the generator input (handwritten partial / attribute) and call symbol_preview_rename on that symbol."));
        }

        if (string.Equals(symbol!.Name, newName, StringComparison.Ordinal))
        {
            return (null, new InvalidRenameNameError(
                $"New name '{newName}' is identical to the current symbol name.",
                "Pass a different identifier as newName."));
        }

        var renamed = await Renamer.RenameSymbolAsync(
            session.Solution,
            symbol,
            DefaultRenameOptions,
            newName,
            cancellationToken).ConfigureAwait(false);

        var diff = await HandwrittenDocumentDiff
            .FromSolutionsAsync(session.Solution, renamed, cancellationToken)
            .ConfigureAwait(false);
        if (diff.AddsOrRemovesDocuments)
        {
            return (null, new DocumentAddOrRemoveRefusedError(
                "This rename would add or remove documents; rename only edits existing documents.",
                "Rename the symbol by hand, or revert the added / removed files and preview again."));
        }

        var (slices, generated) = (diff.Slices, diff.TouchedGenerated);
        if (generated && slices.Count == 0)
        {
            return (null, new GeneratedSymbolRenameRefusedError(
                "This rename would only change generated documents.",
                "Change the generator input (handwritten partial / attribute) and call symbol_preview_rename on that symbol."));
        }

        var invalidated = new List<string> { handle };
        if (symbol is INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers().Where(static m => !m.IsImplicitlyDeclared))
            {
                if (member.Kind is SymbolKind.NamedType or SymbolKind.Namespace)
                {
                    continue;
                }

                var memberHandle = SymbolHandle.Create(
                    parsed.Language,
                    parsed.ProjectId,
                    member.ToDisplayString(SymbolDisplayFormats.SignatureQualified)).Format();
                if (!invalidated.Contains(memberHandle, StringComparer.Ordinal))
                {
                    invalidated.Add(memberHandle);
                }
            }
        }

        // Registered XAML documents join the preview as ordinary slices
        // (ADR-0005 Amendment 1); Unverified name matches are reported only.
        // Unwired sessions collect nothing and the draft is unchanged.
        var xaml = await CollectXamlRenameAsync(
                session, symbol!, newName, xamlSource, cancellationToken)
            .ConfigureAwait(false);
        var documents = xaml.Slices.Count == 0
            ? slices
            : slices.Concat(xaml.Slices).ToArray();

        return (new RenamePreviewDraft(handle, newName, documents, invalidated, xaml.Mentions), null);
    }

    private static bool IsValidIdentifier(string language, string newName)
    {
        if (string.Equals(language, VbLanguage, StringComparison.Ordinal))
        {
            var kind = Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts.GetKeywordKind(newName);
            return Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts.IsValidIdentifier(newName)
                && !Microsoft.CodeAnalysis.VisualBasic.SyntaxFacts.IsReservedKeyword(kind);
        }

        var csharpKind = Microsoft.CodeAnalysis.CSharp.SyntaxFacts.GetKeywordKind(newName);
        return Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(newName)
            && !Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsReservedKeyword(csharpKind);
    }
}
