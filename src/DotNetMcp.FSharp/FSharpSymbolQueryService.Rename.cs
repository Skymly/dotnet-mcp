using Microsoft.FSharp.Core;
using DotNetMcp.Core;
using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Symbols;
using FSharp.Compiler.Syntax;

namespace DotNetMcp.FSharp;

public sealed partial class FSharpSymbolQueryService
{
    public async Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildRenamePreviewAsync(
        IWorkspaceSession session,
        string handle,
        string newName,
        CancellationToken cancellationToken = default)
    {
        using var epochHold = EnterRequest(session.FSharpSnapshot.Epoch);
        if (string.IsNullOrWhiteSpace(newName) || !PrettyNaming.IsIdentifierName(newName))
        {
            return (null, new InvalidRenameNameError(
                "New name must be a single F# identifier.",
                "Pass an F# identifier (no qualification, no keywords) as newName."));
        }

        var (item, project, check, error) = await TryResolveWithCheckAsync(session, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return (null, error);
        }

        var oldName = LastIdentifier(item!.DisplayName) ?? LastIdentifier(item.SignatureQualifiedName);
        if (string.IsNullOrWhiteSpace(oldName))
        {
            return (null, new InvalidRenameNameError(
                "Could not determine a simple identifier for this F# symbol.",
                "Choose a handwritten function, value, or type with a simple name."));
        }

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return (null, new InvalidRenameNameError(
                $"New name '{newName}' is identical to the current symbol name.",
                "Pass a different identifier as newName."));
        }

        if (check is null)
        {
            return (null, new CompilationUnavailableError(
                "F# check results are unavailable for rename.",
                "Retry after workspace_status is ready."));
        }

        if (project!.DependentProjects.Count > 0 && IsVisibleOutsideDefiningAssembly(check, item))
        {
            return (null, new FSharpCrossProjectRenameUnsupportedError(
                $"F# symbol '{oldName}' is visible outside project '{project.Name}' and is referenced by {DescribeDependents(project.DependentProjects)}; the preview would only edit the defining project.",
                "Rename only private symbols or symbols in projects without dependents; F# dependents are tracked in #526, and C#/VB dependents need cross-language rename, which is not supported."));
        }

        var edits = new Dictionary<string, List<(int Start, int Length)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var use in check.GetAllUsesOfAllSymbols(null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SameSymbol(use.Symbol, item))
            {
                continue;
            }

            if (IsProvided(use.Symbol) || IsGeneratedPath(use.FileName))
            {
                return (null, new GeneratedSymbolRenameRefusedError(
                    "Type-provider or generated F# declarations cannot be renamed.",
                    "Rename the handwritten input instead of the generated/provided symbol."));
            }

            if (!TryGetSnapshot(session.FSharpSnapshot.Epoch, use.FileName, out var path, out var text))
            {
                continue;
            }

            var (start, length) = ToSpan(text, use.Range);
            if (length <= 0 || start + length > text.Length)
            {
                return (null, new InvalidRenameNameError(
                    "An F# use site is not a simple identifier span.",
                    "Choose a handwritten identifier; operators and active patterns are refused."));
            }

            var slice = text.Substring(start, length);
            if (!IsSimpleIdentifierUse(slice, oldName))
            {
                return (null, new InvalidRenameNameError(
                    $"Use site '{slice}' is not a simple identifier rename of '{oldName}'.",
                    "Choose a handwritten identifier; operators and active patterns are refused."));
            }

            if (!edits.TryGetValue(path, out var list))
            {
                list = [];
                edits[path] = list;
            }

            var replaceStart = slice.EndsWith(oldName, StringComparison.Ordinal)
                ? start + slice.Length - oldName.Length
                : start;
            list.Add((replaceStart, oldName.Length));
        }

        if (edits.Count == 0)
        {
            return (null, new SymbolNotFoundError(
                "No handwritten F# use sites were found to rename.",
                "Call symbol_find_references to inspect uses, then retry with a source symbol."));
        }

        var documents = new List<RenameDocumentSlice>();
        foreach (var (path, spans) in edits)
        {
            if (!TryGetSnapshot(session.FSharpSnapshot.Epoch, path, out var full, out var oldText))
            {
                continue;
            }

            var newText = ApplyReplacements(oldText, spans, newName);
            if (oldText != newText)
            {
                documents.Add(new RenameDocumentSlice(full, oldText, newText));
            }
        }

        if (documents.Count == 0)
        {
            return (null, new GeneratedSymbolRenameRefusedError(
                "Type-provider or generated F# declarations cannot be renamed.",
                "Rename the handwritten input instead of the generated/provided symbol."));
        }

        return (new RenamePreviewDraft(handle, newName, documents, [handle]), null);
    }

    private static string DescribeDependents(IReadOnlyList<FSharpDependentProject> dependents)
    {
        var shown = string.Join(", ", dependents
            .Take(5)
            .Select(dependent => $"{dependent.Name} ({dependent.Language})"));
        return dependents.Count > 5 ? shown + $" and {dependents.Count - 5} more" : shown;
    }

    private static bool IsVisibleOutsideDefiningAssembly(FSharpCheckProjectResults check, FSharpCatalogItem item)
    {
        FSharpSymbol? symbol = null;
        foreach (var use in check.GetAllUsesOfAllSymbols(null))
        {
            if (use.IsFromDefinition && SameSymbol(use.Symbol, item))
            {
                symbol = use.Symbol;
                break;
            }
        }

        if (symbol is null)
        {
            return true;
        }

        try
        {
            var sawInternal = false;
            for (FSharpSymbol? current = symbol; current is not null; current = DeclaringEntity(current))
            {
                if (current.Accessibility.IsPrivate)
                {
                    return false;
                }

                sawInternal |= !current.Accessibility.IsPublic;
            }

            return !sawInternal || DeclaresInternalsVisibleTo(check);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static FSharpEntity? DeclaringEntity(FSharpSymbol symbol) =>
        symbol switch
        {
            FSharpMemberOrFunctionOrValue member => member.DeclaringEntity?.Value,
            FSharpEntity entity => entity.DeclaringEntity?.Value,
            FSharpUnionCase unionCase => unionCase.DeclaringEntity,
            FSharpField field => field.DeclaringEntity?.Value,
            _ => null
        };

    private static bool DeclaresInternalsVisibleTo(FSharpCheckProjectResults check)
    {
        try
        {
            return check.AssemblySignature.Attributes.Any(attribute =>
                string.Equals(
                    attribute.AttributeType?.FullName,
                    "System.Runtime.CompilerServices.InternalsVisibleToAttribute",
                    StringComparison.Ordinal));
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool IsProvided(FSharpSymbol symbol) =>
        symbol switch
        {
            FSharpEntity entity => entity.IsProvided || entity.IsProvidedAndErased,
            FSharpMemberOrFunctionOrValue member => member.DeclaringEntity is { } declaringEntity
                && (declaringEntity.Value.IsProvided || declaringEntity.Value.IsProvidedAndErased),
            _ => false
        };

    private static bool IsGeneratedPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".g.fs", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".fs.g.cs", StringComparison.OrdinalIgnoreCase));

    private static bool IsSimpleIdentifierUse(string slice, string oldName)
    {
        if (string.Equals(slice, oldName, StringComparison.Ordinal))
        {
            return true;
        }

        return slice.EndsWith("." + oldName, StringComparison.Ordinal) &&
               slice.All(static ch => char.IsLetterOrDigit(ch) || ch is '_' or '.');
    }

    private static string? LastIdentifier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        var paren = trimmed.IndexOfAny(['(', '<', ' ']);
        if (paren > 0)
        {
            trimmed = trimmed[..paren];
        }

        var last = trimmed.LastIndexOf('.');
        return last >= 0 ? trimmed[(last + 1)..] : trimmed;
    }

    private static string ApplyReplacements(string text, List<(int Start, int Length)> spans, string newName)
    {
        var unique = spans
            .Distinct()
            .OrderByDescending(s => s.Start)
            .ToList();
        var builder = new System.Text.StringBuilder(text);
        foreach (var (start, length) in unique)
        {
            builder.Remove(start, length);
            builder.Insert(start, newName);
        }

        return builder.ToString();
    }
}
