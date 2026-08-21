using Microsoft.CodeAnalysis;

namespace DotNetMcp.Core;

/// <summary>
/// Solution pair (or equivalent document pairs) → handwritten Workspace Edit slices.
/// Origin=SourceGenerator documents never become write slices.
/// </summary>
public static class HandwrittenDocumentDiff
{
    public static async Task<(IReadOnlyList<RenameDocumentSlice> Slices, bool TouchedGenerated)> FromSolutionsAsync(
        Solution before,
        Solution after,
        CancellationToken cancellationToken = default)
    {
        var slices = new List<RenameDocumentSlice>();
        var touchedGenerated = false;
        var changes = after.GetChanges(before);
        if (changes.GetAddedProjects().Any() || changes.GetRemovedProjects().Any())
        {
            touchedGenerated = true;
        }

        foreach (var projectChange in changes.GetProjectChanges())
        {
            if (projectChange.GetAddedDocuments().Any() || projectChange.GetRemovedDocuments().Any())
            {
                touchedGenerated = true;
            }

            var generatedIds = await GeneratedIdsAsync(
                    before.GetProject(projectChange.ProjectId),
                    after.GetProject(projectChange.ProjectId),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var docId in projectChange.GetChangedDocuments())
            {
                var oldDoc = before.GetDocument(docId);
                var newDoc = after.GetDocument(docId);
                if (generatedIds.Contains(docId)
                    || oldDoc is SourceGeneratedDocument
                    || newDoc is SourceGeneratedDocument)
                {
                    if (await GeneratedTextChangedAsync(before, after, docId, cancellationToken).ConfigureAwait(false))
                    {
                        touchedGenerated = true;
                    }

                    continue;
                }

                if (oldDoc is null || newDoc is null)
                {
                    touchedGenerated = true;
                    continue;
                }

                var path = oldDoc.FilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    touchedGenerated = true;
                    continue;
                }

                var oldText = (await oldDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                var newText = (await newDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
                if (oldText == newText)
                {
                    continue;
                }

                slices.Add(new RenameDocumentSlice(path, oldText, newText));
            }
        }

        return (slices, touchedGenerated);
    }

    public static async Task<(IReadOnlyList<RenameDocumentSlice> Slices, bool TouchedGenerated)> FromDocumentPairsAsync(
        Solution solution,
        IEnumerable<RenameDocumentSlice> pairs,
        CancellationToken cancellationToken = default)
    {
        var generatedPaths = await GeneratedPathsAsync(solution, cancellationToken).ConfigureAwait(false);
        var slices = new List<RenameDocumentSlice>();
        var touchedGenerated = false;

        foreach (var pair in pairs)
        {
            if (string.IsNullOrWhiteSpace(pair.Path) || generatedPaths.Contains(Normalize(pair.Path)))
            {
                touchedGenerated = true;
                continue;
            }

            if (pair.OldText == pair.NewText)
            {
                continue;
            }

            slices.Add(pair);
        }

        return (slices, touchedGenerated);
    }

    private static async Task<bool> GeneratedTextChangedAsync(
        Solution before,
        Solution after,
        DocumentId documentId,
        CancellationToken cancellationToken)
    {
        var oldDoc = await before.GetSourceGeneratedDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
        var newDoc = await after.GetSourceGeneratedDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (oldDoc is null || newDoc is null)
        {
            return true;
        }

        var oldText = (await oldDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        var newText = (await newDoc.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        return oldText != newText;
    }

    private static async Task<HashSet<DocumentId>> GeneratedIdsAsync(
        Project? beforeProject,
        Project? afterProject,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<DocumentId>();
        await AddGeneratedIdsAsync(ids, beforeProject, cancellationToken).ConfigureAwait(false);
        await AddGeneratedIdsAsync(ids, afterProject, cancellationToken).ConfigureAwait(false);
        return ids;
    }

    private static async Task AddGeneratedIdsAsync(
        HashSet<DocumentId> ids,
        Project? project,
        CancellationToken cancellationToken)
    {
        if (project is null)
        {
            return;
        }

        foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(generated.Id);
        }
    }

    private static async Task<HashSet<string>> GeneratedPathsAsync(
        Solution solution,
        CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(generated.FilePath))
                {
                    paths.Add(Normalize(generated.FilePath));
                }
            }
        }

        return paths;
    }

    private static string Normalize(string path) => Path.GetFullPath(path);
}
