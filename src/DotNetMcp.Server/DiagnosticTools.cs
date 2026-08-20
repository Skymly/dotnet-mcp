using System.ComponentModel;
using System.Text.Json;
using DotNetMcp.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotNetMcp.Server;

[McpServerToolType]
public sealed class DiagnosticTools
{
    private readonly WorkspaceHost _workspaceHost;
    private readonly WorkspaceEdit _workspaceEdit;
    private readonly DiagnosticFixService _fixes;
    private readonly IAuditLogger _audit;

    public DiagnosticTools(
        WorkspaceHost workspaceHost,
        WorkspaceEdit workspaceEdit,
        DiagnosticFixService fixes,
        IAuditLogger audit)
    {
        _workspaceHost = workspaceHost;
        _workspaceEdit = workspaceEdit;
        _fixes = fixes;
        _audit = audit;
    }

    [McpServerTool(Name = "diagnostics_list_fixes"), Description(
        "List first-party / project-loaded CodeFixes for one project_diagnostics occurrence. " +
        "Locator is projectId + diagnosticId + optional filePath/span (1-based lines, 0-based characters). " +
        "Zero fixes is success with an empty list. F# projects return FixLanguageNotSupported. " +
        "Does not write disk.")]
    public async Task<CallToolResult> DiagnosticsListFixes(
        [Description("Roslyn projectId from workspace_list_projects / project_diagnostics.")]
        string projectId,
        [Description("Diagnostic Id from project_diagnostics (for example CS0246).")]
        string diagnosticId,
        [Description("Optional source file path from project_diagnostics.")]
        string? filePath = null,
        [Description("Optional 1-based start line from project_diagnostics.")]
        int? startLine = null,
        [Description("Optional 0-based start character from project_diagnostics.")]
        int? startCharacter = null,
        [Description("Optional 1-based end line from project_diagnostics.")]
        int? endLine = null,
        [Description("Optional 0-based end character from project_diagnostics.")]
        int? endCharacter = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("diagnostics_list_fixes");

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (success, error) = await _fixes
            .ListFixesAsync(
                session!,
                projectId,
                diagnosticId,
                filePath,
                startLine,
                startCharacter,
                endLine,
                endCharacter,
                cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        return OkResult(new DiagnosticsListFixesResultDto
        {
            Items = success!.Items.Select(i => new DiagnosticFixItemDto
            {
                FixIndex = i.FixIndex,
                Title = i.Title,
                EquivalenceKey = i.EquivalenceKey
            }).ToArray()
        });
    }

    [McpServerTool(Name = "diagnostics_preview_fix"), Description(
        "Preview applying one Diagnostic fix as a Workspace Edit. " +
        "Returns previewId bound to the current Epoch + TTL. Does not write disk. " +
        "scope=occurrence (default), scope=document, or scope=project for Fix all with the same EquivalenceKey. " +
        "Generated documents are refused. Not a generic apply_edit / write / shell.")]
    public async Task<CallToolResult> DiagnosticsPreviewFix(
        [Description("Roslyn projectId from workspace_list_projects / project_diagnostics.")]
        string projectId,
        [Description("Diagnostic Id from project_diagnostics.")]
        string diagnosticId,
        [Description("fixIndex from diagnostics_list_fixes on the current snapshot.")]
        int fixIndex,
        [Description("Optional source file path from project_diagnostics.")]
        string? filePath = null,
        [Description("Optional 1-based start line from project_diagnostics.")]
        int? startLine = null,
        [Description("Optional 0-based start character from project_diagnostics.")]
        int? startCharacter = null,
        [Description("Optional 1-based end line from project_diagnostics.")]
        int? endLine = null,
        [Description("Optional 0-based end character from project_diagnostics.")]
        int? endCharacter = null,
        [Description("occurrence (default), document (this file), or project (this project) for the same EquivalenceKey.")]
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("diagnostics_preview_fix");

        if (!TryGetReadySession(out var session, out var notReady))
        {
            return notReady!;
        }

        var (draft, error) = await _fixes
            .BuildPreviewAsync(
                session!,
                projectId,
                diagnosticId,
                filePath,
                startLine,
                startCharacter,
                endLine,
                endCharacter,
                fixIndex,
                scope,
                cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            return ErrorResult(ToPolicyError(error));
        }

        var held = _workspaceEdit.Preview(new WorkspaceEditDraft(
            WorkspaceEditKind.FixPreview,
            draft!.Documents.Select(d => new WorkspaceEditDocument(d.Path, d.OldText, d.NewText)).ToArray(),
            draft.InvalidatedHandles));
        if (held.Error is not null)
        {
            return ErrorResult(held.Error);
        }

        return OkResult(new DiagnosticsPreviewFixResultDto
        {
            PreviewId = held.Value!.PreviewId,
            Epoch = held.Value.Epoch,
            ExpiresAt = held.Value.ExpiresAt,
            Title = draft.Title,
            EquivalenceKey = draft.EquivalenceKey,
            Scope = draft.Scope,
            Documents = ToDocumentDtos(held.Value.Documents),
            InvalidatedHandles = held.Value.InvalidatedHandles
        });
    }

    [McpServerTool(Name = "diagnostics_apply_fix"), Description(
        "Apply a still-valid Diagnostic fix preview. Writes only the documents listed in that preview, " +
        "all of which must already exist inside a trusted root. Uses WriteSuppression and advances Epoch. " +
        "There is no apply path that skips preview. Not a generic write / patch / shell tool.")]
    public Task<CallToolResult> DiagnosticsApplyFix(
        [Description("previewId from diagnostics_preview_fix (current Epoch, unexpired).")]
        string previewId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _audit.ToolInvoked("diagnostics_apply_fix");

        if (!TryGetReadySession(out _, out var notReady))
        {
            return Task.FromResult(notReady!);
        }

        var applied = _workspaceEdit.Apply(previewId, WorkspaceEditKind.FixPreview);
        if (applied.Error is not null)
        {
            return Task.FromResult(ErrorResult(applied.Error));
        }

        return Task.FromResult(OkResult(new DiagnosticsApplyFixResultDto
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
