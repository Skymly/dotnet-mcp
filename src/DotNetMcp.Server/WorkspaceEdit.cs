namespace DotNetMcp.Server;

public enum WorkspaceEditKind
{
    RenamePreview,
    FixPreview,
    RefactoringPreview
}

public sealed record WorkspaceEditDocument(string Path, string OldText, string NewText);

public sealed record WorkspaceEditDraft(
    WorkspaceEditKind Kind,
    IReadOnlyList<WorkspaceEditDocument> Documents,
    IReadOnlyList<string> InvalidatedHandles);

public sealed record WorkspaceEditPreview(
    string PreviewId,
    long Epoch,
    DateTimeOffset ExpiresAt,
    WorkspaceEditKind Kind,
    IReadOnlyList<WorkspaceEditDocument> Documents,
    IReadOnlyList<string> InvalidatedHandles);

public sealed record WorkspaceEditApplied(
    string PreviewId,
    long Epoch,
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> InvalidatedHandles);

public readonly record struct WorkspaceEditOutcome<T>(T? Value, PolicyErrorDto? Error)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// Deep Workspace Edit module (ADR-0005): one store, kind-tagged previews, apply must match kind.
/// </summary>
public sealed class WorkspaceEdit
{
    private readonly IWorkspaceEditWriter _writer;
    private readonly TrustedRoots _trustedRoots;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private readonly Dictionary<string, Stored> _items = new(StringComparer.Ordinal);
    private long _observedGeneration;

    public WorkspaceEdit(
        IWorkspaceEditWriter writer,
        TrustedRoots trustedRoots,
        TimeProvider timeProvider,
        TimeSpan ttl)
    {
        _writer = writer;
        _trustedRoots = trustedRoots;
        _time = timeProvider;
        _ttl = ttl;
        _observedGeneration = writer.Generation;
    }

    public WorkspaceEditOutcome<WorkspaceEditPreview> Preview(WorkspaceEditDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(draft.Documents);
        ArgumentNullException.ThrowIfNull(draft.InvalidatedHandles);

        foreach (var document in draft.Documents)
        {
            if (!_trustedRoots.Contains(document.Path))
            {
                return Fail<WorkspaceEditPreview>(
                    PolicyErrorCodes.PreviewPathOutsideTrustedRoots,
                    KindNoun(draft.Kind) + " includes a path outside trusted roots; the preview was not stored.",
                    "Open a workspace whose documents all sit under a trusted root, then retry.");
            }
        }

        var now = _time.GetUtcNow();
        var preview = new WorkspaceEditPreview(
            PreviewId: Convert.ToHexString(Guid.NewGuid().ToByteArray())[..16].ToLowerInvariant(),
            Epoch: _writer.CurrentEpoch,
            ExpiresAt: now + _ttl,
            Kind: draft.Kind,
            Documents: draft.Documents,
            InvalidatedHandles: draft.InvalidatedHandles);

        lock (_gate)
        {
            DropIfGenerationChangedUnlocked();
            _items[preview.PreviewId] = new Stored(preview, _observedGeneration);
        }

        return new WorkspaceEditOutcome<WorkspaceEditPreview>(preview, null);
    }

    public WorkspaceEditOutcome<WorkspaceEditApplied> Apply(string previewId, WorkspaceEditKind kind)
    {
        var tools = Tools(kind);

        if (string.IsNullOrWhiteSpace(previewId))
        {
            return Fail<WorkspaceEditApplied>(
                PolicyErrorCodes.PreviewNotFound,
                "Apply requires a previewId from " + tools.Preview + ".",
                "Call " + tools.Preview + " first, then pass that previewId to " + tools.Apply + ".");
        }

        Stored stored;
        lock (_gate)
        {
            DropIfGenerationChangedUnlocked();
            if (!_items.TryGetValue(previewId, out stored!))
            {
                return Fail<WorkspaceEditApplied>(
                    PolicyErrorCodes.PreviewNotFound,
                    "Unknown previewId.",
                    "Call " + tools.Preview + " to obtain a fresh previewId; do not invent preview ids.");
            }
        }

        var now = _time.GetUtcNow();
        if (stored.Preview.ExpiresAt <= now)
        {
            return Fail<WorkspaceEditApplied>(
                PolicyErrorCodes.PreviewExpired,
                "The preview has expired.",
                "Call " + tools.Preview + " again, then apply the new previewId.");
        }

        if (stored.Preview.Epoch != _writer.CurrentEpoch)
        {
            return Fail<WorkspaceEditApplied>(
                PolicyErrorCodes.PreviewEpochMismatch,
                "The preview is bound to a previous workspace Epoch.",
                "Call " + tools.Preview + " on the current snapshot, then apply that previewId.");
        }

        if (stored.Preview.Kind != kind)
        {
            var storedTools = Tools(stored.Preview.Kind);
            return Fail<WorkspaceEditApplied>(
                PolicyErrorCodes.PreviewKindMismatch,
                "This previewId was stored under a different Workspace Edit kind.",
                "Call " + storedTools.Apply + " with this previewId, or call " + tools.Preview + " for a " + KindNoun(kind) + ".");
        }

        foreach (var document in stored.Preview.Documents)
        {
            if (!_trustedRoots.Contains(document.Path))
            {
                return Fail<WorkspaceEditApplied>(
                    PolicyErrorCodes.PathOutsideTrustedRoots,
                    "A preview document is outside trusted roots; nothing was written.",
                    "Re-open the workspace under a trusted root that contains every preview path.");
            }

            if (!_writer.PathExists(document.Path))
            {
                return Fail<WorkspaceEditApplied>(
                    PolicyErrorCodes.PreviewTargetMissing,
                    "A preview document no longer exists on disk; nothing was written.",
                    "Call " + tools.Preview + " again on the current snapshot.");
            }
        }

        var written = _writer.WriteDeclaredPaths(stored.Preview.Documents);
        if (written.Error is not null)
        {
            var error = written.Error;
            if (error.Error is PolicyErrorCodes.WorkspaceNotReady or PolicyErrorCodes.PreviewTargetMissing)
            {
                error = new PolicyErrorDto
                {
                    Error = error.Error,
                    Message = error.Message,
                    SuggestedAction = error.Error == PolicyErrorCodes.WorkspaceNotReady
                        ? "Call workspace_status until ready, then preview and apply again."
                        : "Call " + tools.Preview + " again on the current snapshot."
                };
            }

            return new WorkspaceEditOutcome<WorkspaceEditApplied>(null, error);
        }

        lock (_gate)
        {
            _items.Remove(previewId);
        }

        return new WorkspaceEditOutcome<WorkspaceEditApplied>(
            new WorkspaceEditApplied(
                stored.Preview.PreviewId,
                written.Value,
                stored.Preview.Documents.Select(static d => d.Path).ToArray(),
                stored.Preview.InvalidatedHandles),
            null);
    }

    private void DropIfGenerationChangedUnlocked()
    {
        var generation = _writer.Generation;
        if (generation == _observedGeneration)
        {
            return;
        }

        _items.Clear();
        _observedGeneration = generation;
    }

    private static WorkspaceEditOutcome<T> Fail<T>(string code, string message, string suggested)
        where T : class =>
        new(null, new PolicyErrorDto
        {
            Error = code,
            Message = message,
            SuggestedAction = suggested
        });

    private static string KindNoun(WorkspaceEditKind kind) => kind switch
    {
        WorkspaceEditKind.FixPreview => "Diagnostic fix preview",
        WorkspaceEditKind.RefactoringPreview => "Code Refactoring preview",
        _ => "Rename preview"
    };

    private static (string Preview, string Apply) Tools(WorkspaceEditKind kind) => kind switch
    {
        WorkspaceEditKind.FixPreview => ("diagnostics_preview_fix", "diagnostics_apply_fix"),
        WorkspaceEditKind.RefactoringPreview => ("symbol_preview_refactoring", "symbol_apply_refactoring"),
        _ => ("symbol_preview_rename", "symbol_apply_rename")
    };

    private sealed record Stored(WorkspaceEditPreview Preview, long Generation);
}
