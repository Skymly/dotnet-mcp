using System.ComponentModel;
using System.Text.Json;
using DotNetMcp.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

[McpServerToolType]
public sealed class SymbolRefactoringTools
{
    private readonly WorkspaceHost _workspaceHost;
    private readonly WorkspaceEdit _workspaceEdit;
    private readonly CodeRefactoringService _refactorings;
    private readonly IAuditLogger _audit;

    public SymbolRefactoringTools(
        WorkspaceHost workspaceHost,
        WorkspaceEdit workspaceEdit,
        CodeRefactoringService refactorings,
        IAuditLogger audit)
    {
        _workspaceHost = workspaceHost;
        _workspaceEdit = workspaceEdit;
        _refactorings = refactorings;
        _audit = audit;
    }

    [McpServerTool(Name = "symbol_list_refactorings"), Description(
        "List first-party / project-loaded Code Refactorings at a handwritten SymbolHandle identifier. " +
        "Zero refactorings is success with an empty list. F# handles return RefactoringLanguageNotSupported. " +
        "SourceGenerator Origin is refused. Does not write disk.")]
    public async Task<CallToolResult> SymbolListRefactorings(
        [Description("Handwritten C# / VB SymbolHandle from symbol_resolve.")]
        string handle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_list_refactorings");

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _refactorings
            .ListAsync(session!, handle, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        return OkResult(new SymbolListRefactoringsResultDto
        {
            Items = success!.Items.Select(i => new CodeRefactoringItemDto
            {
                RefactoringIndex = i.RefactoringIndex,
                Title = i.Title,
                EquivalenceKey = i.EquivalenceKey
            }).ToArray()
        });
    }

    [McpServerTool(Name = "symbol_preview_refactoring"), Description(
        "Preview applying one Code Refactoring as a Workspace Edit. " +
        "Returns previewId bound to the current Epoch + TTL. Does not write disk. " +
        "Generated documents are refused. Not a generic apply_edit / write / shell.")]
    public async Task<CallToolResult> SymbolPreviewRefactoring(
        [Description("Handwritten C# / VB SymbolHandle from symbol_resolve.")]
        string handle,
        [Description("refactoringIndex from symbol_list_refactorings on the current snapshot.")]
        int refactoringIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_preview_refactoring");

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (draft, error) = await _refactorings
            .BuildPreviewAsync(session!, handle, refactoringIndex, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        var held = _workspaceEdit.Preview(new WorkspaceEditDraft(
            WorkspaceEditKind.RefactoringPreview,
            draft!.Documents.Select(d => new WorkspaceEditDocument(d.Path, d.OldText, d.NewText)).ToArray(),
            draft.InvalidatedHandles));
        if (held.Error is not null)
        {
            return ErrorResult(held.Error);
        }

        return OkResult(new SymbolPreviewRefactoringResultDto
        {
            PreviewId = held.Value!.PreviewId,
            Epoch = held.Value.Epoch,
            ExpiresAt = held.Value.ExpiresAt,
            Title = draft.Title,
            EquivalenceKey = draft.EquivalenceKey,
            Handle = draft.Handle,
            Documents = ToDocumentDtos(held.Value.Documents),
            InvalidatedHandles = held.Value.InvalidatedHandles
        });
    }

    [McpServerTool(Name = "symbol_apply_refactoring"), Description(
        "Apply a still-valid Code Refactoring preview. Writes only the documents listed in that preview, " +
        "all of which must already exist inside a trusted root. Uses WriteSuppression and advances Epoch. " +
        "There is no apply path that skips preview. Not a generic write / patch / shell tool.")]
    public Task<CallToolResult> SymbolApplyRefactoring(
        [Description("previewId from symbol_preview_refactoring (current Epoch, unexpired).")]
        string previewId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("symbol_apply_refactoring");

        if (!TryGetReadySession(out _, out var notReady))
        {
            return Task.FromResult(notReady!);
        }

        var applied = _workspaceEdit.Apply(previewId, WorkspaceEditKind.RefactoringPreview);
        if (applied.Error is not null)
        {
            return Task.FromResult(ErrorResult(applied.Error));
        }

        return Task.FromResult(OkResult(new SymbolApplyRefactoringResultDto
        {
            PreviewId = applied.Value!.PreviewId,
            Epoch = applied.Value.Epoch,
            WrittenPaths = applied.Value.WrittenPaths,
            InvalidatedHandles = applied.Value.InvalidatedHandles
        }));
    }

    private static IReadOnlyList<RenameDocumentSliceDto> ToDocumentDtos(
        IReadOnlyList<WorkspaceEditDocument> documents) =>
        documents.Select(d => new RenameDocumentSliceDto
        {
            Path = d.Path,
            OldText = d.OldText,
            NewText = d.NewText
        }).ToArray();

    private bool TryGetReadySession(out IWorkspaceSession? session, out CallToolResult? errorResult)
    {
        if (_workspaceHost.TryGetReadySession(out session) && session is not null)
        {
            errorResult = null;
            return true;
        }

        var status = _workspaceHost.GetStatus();
        errorResult = ErrorResult(new PolicyErrorDto
        {
            Error = PolicyErrorCodes.WorkspaceNotReady,
            Message =
                $"Workspace is not ready (phase={status.Phase}). Query tools cannot run until load completes.",
            SuggestedAction =
                "Call workspace_status to poll until phase is ready; do not retry workspace_open while loading."
        });
        return false;
    }

    private static PolicyErrorDto ToPolicyError(SymbolQueryError error) => new()
    {
        Error = error.Code,
        Message = error.Message,
        SuggestedAction = error.SuggestedAction
    };

    private static CallToolResult OkResult<T>(T payload) => new()
    {
        Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(payload, JsonOptions.Default)
            }
        ]
    };

    private static CallToolResult ErrorResult(PolicyErrorDto error) => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock
            {
                Text = JsonSerializer.Serialize(error, JsonOptions.Default)
            }
        ]
    };
}
