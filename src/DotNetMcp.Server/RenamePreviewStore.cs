namespace DotNetMcp.Server;

public static class RenamePreviewErrorCodes
{
    public const string PreviewNotFound = "PreviewNotFound";
    public const string PreviewExpired = "PreviewExpired";
    public const string PreviewEpochMismatch = "PreviewEpochMismatch";
}

public sealed record StoredRenamePreview(
    string PreviewId,
    long Epoch,
    DateTimeOffset ExpiresAt,
    string OldHandle,
    string NewName,
    IReadOnlyList<RenameDocumentSliceDto> Documents,
    IReadOnlyList<string> InvalidatedHandles);

/// <summary>
/// Process-local preview store (Spike S4): previewId + Epoch + TTL. No apply.
/// </summary>
public sealed class RenamePreviewStore
{
    private readonly Dictionary<string, StoredRenamePreview> _items = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public StoredRenamePreview Add(
        long epoch,
        DateTimeOffset expiresAt,
        string oldHandle,
        string newName,
        IReadOnlyList<RenameDocumentSliceDto> documents,
        IReadOnlyList<string> invalidatedHandles)
    {
        var preview = new StoredRenamePreview(
            PreviewId: Convert.ToHexString(Guid.NewGuid().ToByteArray())[..16].ToLowerInvariant(),
            Epoch: epoch,
            ExpiresAt: expiresAt,
            OldHandle: oldHandle,
            NewName: newName,
            Documents: documents,
            InvalidatedHandles: invalidatedHandles);

        lock (_gate)
        {
            _items[preview.PreviewId] = preview;
        }

        return preview;
    }

    public (StoredRenamePreview? Preview, string? ErrorCode) TryGet(
        string previewId,
        long currentEpoch,
        DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(previewId))
        {
            return (null, RenamePreviewErrorCodes.PreviewNotFound);
        }

        StoredRenamePreview? preview;
        lock (_gate)
        {
            _items.TryGetValue(previewId, out preview);
        }

        if (preview is null)
        {
            return (null, RenamePreviewErrorCodes.PreviewNotFound);
        }

        if (preview.ExpiresAt <= utcNow)
        {
            return (null, RenamePreviewErrorCodes.PreviewExpired);
        }

        if (preview.Epoch != currentEpoch)
        {
            return (null, RenamePreviewErrorCodes.PreviewEpochMismatch);
        }

        return (preview, null);
    }

    public void Remove(string previewId)
    {
        lock (_gate)
        {
            _items.Remove(previewId);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }
}
