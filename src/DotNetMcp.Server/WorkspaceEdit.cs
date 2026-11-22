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
    IReadOnlyList<string> InvalidatedHandles)
{
    internal long? ExpectedEpoch { get; init; }
    internal IReadOnlyList<string> AffectedProjectIds { get; init; } = [];
}

public sealed record WorkspaceEditPreview(
    string PreviewId,
    long Epoch,
    DateTimeOffset ExpiresAt,
    WorkspaceEditKind Kind,
    IReadOnlyList<WorkspaceEditDocument> Documents,
    IReadOnlyList<string> InvalidatedHandles)
{
    internal PreviewReview Review { get; } = new(Documents);
    internal IReadOnlyList<string> AffectedProjectIds { get; init; } = [];
}

public sealed record WorkspaceEditApplied(
    string PreviewId,
    long Epoch,
    IReadOnlyList<string> WrittenPaths,
    IReadOnlyList<string> InvalidatedHandles)
{
    internal IReadOnlyList<string> AffectedProjectIds { get; init; } = [];
}

public readonly record struct WorkspaceEditOutcome<T>(T? Value, PolicyErrorDto? Error)
{
    /// <summary>Server-only path for audit. Not part of the MCP error payload.</summary>
    public string? DeniedPath { get; init; }

    public bool Failed => Error is not null;
}

/// <summary>
/// Deep Workspace Edit module (ADR-0005): one store, kind-tagged previews, apply must match kind.
/// Apply is single-flight per previewId; write/backfill/Epoch is one outcome (ADR-0002).
/// Disk I/O and the writer run outside <c>_gate</c>. Expired entries are swept on Preview/Apply.
/// </summary>
public sealed class WorkspaceEdit
{
    private readonly IWorkspaceEditWriter _writer;
    private readonly TrustedRoots _trustedRoots;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();
    private readonly Dictionary<string, Stored> _items = new(StringComparer.Ordinal);
    private readonly IAuditLogger? _audit;
    private long _observedGeneration;

    public WorkspaceEdit(
        IWorkspaceEditWriter writer,
        TrustedRoots trustedRoots,
        TimeProvider timeProvider,
        TimeSpan ttl,
        IAuditLogger? auditLogger = null)
    {
        _writer = writer;
        _trustedRoots = trustedRoots;
        _time = timeProvider;
        _ttl = ttl;
        _audit = auditLogger;
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
        var currentEpoch = _writer.CurrentEpoch;
        if (draft.ExpectedEpoch is { } expectedEpoch && currentEpoch != expectedEpoch)
        {
            return Fail<WorkspaceEditPreview>(
                PolicyErrorCodes.PreviewEpochMismatch,
                "The workspace Epoch changed while preparing the preview.",
                "Call " + Tools(draft.Kind).Preview + " again on the current snapshot.");
        }

        var preview = new WorkspaceEditPreview(
            PreviewId: Convert.ToHexString(Guid.NewGuid().ToByteArray())[..16].ToLowerInvariant(),
            Epoch: currentEpoch,
            ExpiresAt: now + _ttl,
            Kind: draft.Kind,
            Documents: draft.Documents,
            InvalidatedHandles: draft.InvalidatedHandles)
        {
            AffectedProjectIds = draft.AffectedProjectIds,
        };

        lock (_gate)
        {
            DropIfGenerationChangedUnlocked();
            SweepExpiredUnlocked(now);
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

        var acquired = Lookup(previewId, kind);
        if (acquired.Error is not null)
        {
            return new(null, acquired.Error);
        }

        var stored = acquired.Value!;
        foreach (var document in stored.Preview.Documents)
        {
            if (!_trustedRoots.Contains(document.Path))
            {
                _audit?.PathPolicyDenied(tools.Apply, document.Path);
                Restore(stored);
                return Fail<WorkspaceEditApplied>(
                    PolicyErrorCodes.PathOutsideTrustedRoots,
                    "A preview document is outside trusted roots; nothing was written.",
                    "Re-open the workspace under a trusted root that contains every preview path.");
            }

            if (!_writer.PathExists(document.Path))
            {
                Restore(stored);
                return Fail<WorkspaceEditApplied>(
                    PolicyErrorCodes.PreviewTargetMissing,
                    "A preview document no longer exists on disk; nothing was written.",
                    "Call " + tools.Preview + " again on the current snapshot.");
            }
        }

        var written = _writer.WriteDeclaredPaths(stored.Preview.Documents);
        if (written.Error is not null)
        {
            if (written.Error.Error == PolicyErrorCodes.PathOutsideTrustedRoots)
            {
                _audit?.PathPolicyDenied(tools.Apply, written.DeniedPath);
            }

            Restore(stored);
            var error = MapApplyError(written.Error, kind, tools);
            return new WorkspaceEditOutcome<WorkspaceEditApplied>(null, error);
        }

        return new WorkspaceEditOutcome<WorkspaceEditApplied>(
            new WorkspaceEditApplied(
                stored.Preview.PreviewId,
                written.Value,
                stored.Preview.Documents.Select(static d => d.Path).ToArray(),
                stored.Preview.InvalidatedHandles)
            {
                AffectedProjectIds = stored.Preview.AffectedProjectIds,
            },
            null);
    }

    internal WorkspaceEditOutcome<PreviewReviewPage> ReadReview(
        string previewId, int startFileIndex, int? fileIndex, int offset)
    {
        var found = Lookup(previewId, applyKind: null);
        return found.Error is null
            ? found.Value!.Preview.Review.Read(startFileIndex, fileIndex, offset)
            : new(null, found.Error);
    }

    private WorkspaceEditOutcome<Stored> Lookup(string previewId, WorkspaceEditKind? applyKind)
    {
        lock (_gate)
        {
            DropIfGenerationChangedUnlocked();
            SweepExpiredUnlocked(_time.GetUtcNow(), keepId: previewId);
            if (string.IsNullOrWhiteSpace(previewId) || !_items.TryGetValue(previewId, out var stored))
            {
                return Fail<Stored>(
                    PolicyErrorCodes.PreviewNotFound,
                    "Unknown previewId.",
                    applyKind is { } requestedKind ? "Call " + Tools(requestedKind).Preview + " to obtain a fresh previewId; do not invent preview ids."
                        : "Call the preview tool again to obtain a fresh previewId.");
            }

            var now = _time.GetUtcNow();
            if (stored.Preview.ExpiresAt <= now)
            {
                _items.Remove(previewId);
                return Fail<Stored>(
                    PolicyErrorCodes.PreviewExpired,
                    "The preview has expired.",
                    "Call " + Tools(stored.Preview.Kind).Preview + " again to obtain a fresh previewId.");
            }

            if (stored.Preview.Epoch != _writer.CurrentEpoch)
            {
                return Fail<Stored>(
                    PolicyErrorCodes.PreviewEpochMismatch,
                    "The preview is bound to a previous workspace Epoch.",
                    "Call " + Tools(stored.Preview.Kind).Preview + " on the current snapshot.");
            }

            if (applyKind is { } kind && stored.Preview.Kind != kind)
            {
                var tools = Tools(kind);
                var storedTools = Tools(stored.Preview.Kind);
                return Fail<Stored>(
                    PolicyErrorCodes.PreviewKindMismatch,
                    "This previewId was stored under a different Workspace Edit kind.",
                    "Call " + storedTools.Apply + " with this previewId, or call " + tools.Preview + " for a " + KindNoun(kind) + ".");
            }

            if (applyKind.HasValue)
            {
                _items.Remove(previewId);
            }

            return new(stored, null);
        }
    }

    private static PolicyErrorDto MapApplyError(PolicyErrorDto error, WorkspaceEditKind kind, (string Preview, string Apply) tools)
    {
        if (error.Error == PolicyErrorCodes.WorkspaceEditApplyFailed)
        {
            var code = kind switch
            {
                WorkspaceEditKind.FixPreview => PolicyErrorCodes.FixApplyFailed,
                WorkspaceEditKind.RefactoringPreview => PolicyErrorCodes.RefactoringApplyFailed,
                _ => PolicyErrorCodes.RenameApplyFailed,
            };
            return new PolicyErrorDto
            {
                Error = code,
                Message = error.Message,
                SuggestedAction = "Retry " + tools.Apply + " with the same previewId, or preview again if the disk changed."
            };
        }

        if (error.Error is PolicyErrorCodes.WorkspaceNotReady
            or PolicyErrorCodes.PreviewTargetMissing
            or PolicyErrorCodes.PreviewTextMismatch)
        {
            return new PolicyErrorDto
            {
                Error = error.Error,
                Message = error.Message,
                SuggestedAction = error.Error == PolicyErrorCodes.WorkspaceNotReady
                    ? "Call workspace_status until ready, then preview and apply again."
                    : "Call " + tools.Preview + " again on the current snapshot."
            };
        }

        return error;
    }

    private void Restore(Stored stored)
    {
        lock (_gate)
        {
            DropIfGenerationChangedUnlocked();
            if (stored.Generation != _observedGeneration)
            {
                return;
            }

            if (stored.Preview.ExpiresAt <= _time.GetUtcNow())
            {
                return;
            }

            _items.TryAdd(stored.Preview.PreviewId, stored);
        }
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

    private void SweepExpiredUnlocked(DateTimeOffset now, string? keepId = null)
    {
        List<string>? expired = null;
        foreach (var (id, stored) in _items)
        {
            if (keepId is not null && string.Equals(id, keepId, StringComparison.Ordinal))
            {
                continue;
            }

            if (stored.Preview.ExpiresAt <= now)
            {
                expired ??= [];
                expired.Add(id);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var id in expired)
        {
            _items.Remove(id);
        }
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
