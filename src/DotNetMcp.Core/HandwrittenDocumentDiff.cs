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

        foreach (var project in changes.GetAddedProjects())
        {
            touchedGenerated |= await CollectProjectDocumentsAsync(
                    after.GetProject(project.Id) ?? project,
                    added: true,
                    slices,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var project in changes.GetRemovedProjects())
        {
            touchedGenerated |= await CollectProjectDocumentsAsync(
                    before.GetProject(project.Id) ?? project,
                    added: false,
                    slices,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var projectChange in changes.GetProjectChanges())
        {
            var generatedIds = await GeneratedIdsAsync(
                    before.GetProject(projectChange.ProjectId),
                    after.GetProject(projectChange.ProjectId),
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (var docId in projectChange.GetAddedDocuments())
            {
                touchedGenerated |= await CollectDocumentAsync(
                        after.GetDocument(docId),
                        generatedIds,
                        oldText: string.Empty,
                        newTextFromDoc: true,
                        slices,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var docId in projectChange.GetRemovedDocuments())
            {
                touchedGenerated |= await CollectDocumentAsync(
                        before.GetDocument(docId),
                        generatedIds,
                        oldText: null,
                        newTextFromDoc: false,
                        slices,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

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
                    continue;
                }

                var path = oldDoc.FilePath;
                if (string.IsNullOrWhiteSpace(path))
                {
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


    private static async Task<bool> CollectProjectDocumentsAsync(
        Project? project,
        bool added,
        List<RenameDocumentSlice> slices,
        CancellationToken cancellationToken)
    {
        if (project is null)
        {
            return false;
        }

        var generatedIds = await GeneratedIdsAsync(project, project, cancellationToken).ConfigureAwait(false);
        var touchedGenerated = false;
        foreach (var document in project.Documents)
        {
            touchedGenerated |= await CollectDocumentAsync(
                    document,
                    generatedIds,
                    oldText: added ? string.Empty : null,
                    newTextFromDoc: added,
                    slices,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (generatedIds.Contains(generated.Id) || generated is SourceGeneratedDocument)
            {
                touchedGenerated = true;
            }
        }

        return touchedGenerated;
    }

    private static async Task<bool> CollectDocumentAsync(
        Document? document,
        HashSet<DocumentId> generatedIds,
        string? oldText,
        bool newTextFromDoc,
        List<RenameDocumentSlice> slices,
        CancellationToken cancellationToken)
    {
        if (document is null)
        {
            return false;
        }

        if (generatedIds.Contains(document.Id) || document is SourceGeneratedDocument)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(document.FilePath))
        {
            return false;
        }

        var text = (await document.GetTextAsync(cancellationToken).ConfigureAwait(false)).ToString();
        var sliceOld = oldText ?? text;
        var sliceNew = newTextFromDoc ? text : string.Empty;
        if (sliceOld == sliceNew)
        {
            return false;
        }

        slices.Add(new RenameDocumentSlice(document.FilePath, sliceOld, sliceNew));
        return false;
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
