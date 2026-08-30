# Server 深模块类型与现有直接单测对照

Snapshot: `origin/main` @ `6d6240a58f51a7e1c8175212a635088aa532b72d`.

Scope: Goal 点名的 Server 深模块 — Workspace Edit、PathPolicy / TrustedRoots、MCP envelope — 以及票面点名的 `AuditOptions`.

## Criterion

Direct = 不经 `InProcessMcpFixture` / MCP `CallToolAsync`. `new` / static 入口算直接。源码扫描不算。Mixed `*SeamTests` 用 †。

## Inventory

| Type | Visibility | Source | Role | Direct tests |
| --- | --- | --- | --- | --- |
| `WorkspaceEdit` | public class | `WorkspaceEdit.cs` | service | SeamTests†：`WorkspaceEditSeamTests` 前若干方法 `new WorkspaceEdit` + fake writer，无 MCP；后部 MCP apply |
| `WorkspaceEditKind` | public enum | `WorkspaceEdit.cs` | other | 仅 SeamTests† |
| `WorkspaceEditDocument` | public record | `WorkspaceEdit.cs` | DTO | 仅 SeamTests† |
| `WorkspaceEditDraft` | public record | `WorkspaceEdit.cs` | DTO | 仅 SeamTests† |
| `WorkspaceEditPreview` | public record | `WorkspaceEdit.cs` | DTO | 仅 SeamTests† |
| `WorkspaceEditApplied` | public record | `WorkspaceEdit.cs` | DTO | 仅 SeamTests† |
| `WorkspaceEditOutcome<T>` | public struct | `WorkspaceEdit.cs` | DTO | 仅 SeamTests† |
| `IWorkspaceEditWriter` | public interface | `IWorkspaceEditWriter.cs` | other | SeamTests† 内 nested fake |
| `WriteSuppression` | public class | `WriteSuppression.cs` | other | 无独立单测文件 |
| `PathPolicy` | public static class | `PathPolicy.cs` | policy | 无。`PathPolicySeamTests` 全 MCP；`SymbolPreviewRenameSeamTests` 用 `Normalize`/`IsUnderRoot` 作断言助手 |
| `TrustedRoots` | public class | `TrustedRoots.cs` | policy | 协作者：`TrustedRoots.Create` 出现在大量 SeamTests / `WorkspaceEditSeamTests`；无 `TrustedRootsTests` |
| `McpToolEnvelope` | public static class | `McpToolEnvelope.cs` | envelope | SeamTests†：`envelope_error_result_marks_is_error_and_serializes_policy_dto` 直接 `ErrorResult`；另一方法是源码扫描 |
| `AuditOptions` | public class | `AuditOptions.cs` | options | **直接**：`AuditOptionsTests.cs`（整文件无 MCP） |

## Appendix 1 — public 入口

**PathPolicy:** `Normalize(path)`, `IsUnderRoot(normalizedPath, normalizedRoot)`.

**TrustedRoots:** `Create(roots)`, `FromStartup(args)`, `Contains(path)`, `Roots`.

**WorkspaceEdit:** ctor `(IWorkspaceEditWriter, TrustedRoots, TimeProvider, TimeSpan ttl)`；`Preview(draft)`；`Apply(previewId, kind)`.

**McpToolEnvelope:** `TryGetReadySession(...)`, `ToPolicyError(SymbolQueryError|XamlQueryError)`, `OkResult<T>`, `ErrorResult`.

**AuditOptions:** `FromEnvironment()`, `FromEnvironment(getEnv)`, `Default`, `Enabled`.

## Appendix 2 — 现有测试如何构造输入

| File | How |
| --- | --- |
| `PathPolicySeamTests` | `InProcessMcpFixture(TrustedRoots.Create([root]))` + MCP `workspace_open` / 工具调用。不直接打 `PathPolicy` |
| `WorkspaceEditSeamTests` | 无 MCP 方法：`new WorkspaceEdit(FakeWorkspaceEditWriter, TrustedRoots.Create([root]), TimeProvider, ttl)` + temp 目录路径；MCP 方法：`InProcessMcpFixture` + `FakeSolutionLoader` |
| `McpToolEnvelopeSeamTests` | 源码扫描 tool class；以及 `new PolicyErrorDto` + `McpToolEnvelope.ErrorResult` |
| `AuditOptionsTests` | `AuditOptions.FromEnvironment` 注入 `getEnvironmentVariable` 委托，无磁盘、无 MCP |