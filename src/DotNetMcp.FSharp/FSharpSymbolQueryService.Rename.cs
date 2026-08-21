using Microsoft.FSharp.Core;
using DotNetMcp.Core;
using FSharp.Compiler.Symbols;

namespace DotNetMcp.FSharp;

public sealed partial class FSharpSymbolQueryService
{
    public async Task<(RenamePreviewDraft? Draft, SymbolQueryError? Error)> BuildRenamePreviewAsync(
        IWorkspaceSession session,
        string handle,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(['.', ' ', '\t']) >= 0)
        {
            return (null, new InvalidRenameNameError(
                "New name must be a single identifier.",
                "Pass an F# identifier (no qualification) as newName."));
        }

        var (item, _, check, error) = await TryResolveWithCheckAsync(session, handle, cancellationToken)
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

            var source = session.Solution.Projects
                .SelectMany(p => p.Documents)
                .FirstOrDefault(d => SameDocumentPath(d.FilePath, use.FileName));
            if (source?.FilePath is null)
            {
                continue;
            }

            var text = (await source.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
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

            if (!edits.TryGetValue(source.FilePath, out var list))
            {
                list = [];
                edits[source.FilePath] = list;
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
            var document = session.Solution.Projects
                .SelectMany(p => p.Documents)
                .First(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
            var oldText = (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
            var newText = ApplyReplacements(oldText, spans, newName);
            if (oldText != newText)
            {
                documents.Add(new RenameDocumentSlice(path, oldText, newText));
            }
        }

        return (new RenamePreviewDraft(handle, newName, documents, [handle]), null);
    }

    private static bool IsProvided(FSharpSymbol symbol) =>
        symbol switch
        {
            FSharpEntity entity => entity.IsProvided || entity.IsProvidedAndErased,
            FSharpMemberOrFunctionOrValue member => OptionModule.IsSome(member.DeclaringEntity) && (member.DeclaringEntity.Value.IsProvided || member.DeclaringEntity.Value.IsProvidedAndErased),
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
