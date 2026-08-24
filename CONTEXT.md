# DotNetMCP

面向 AI 编程助手的 .NET MCP 服务器上下文：解决方案工作区、符号句柄、源生成器归因，以及受限的可预览 Workspace Edit。架构取舍见 `docs/adr/`。

## Language

**SymbolHandle**:
跨 MCP 调用稳定引用某一符号的不透明句柄（含所属项目与签名身份，不含工作区代次）。
_Avoid_: Roslyn ISymbol id, document URI alone

**Workspace**:
进程内当前打开的单个解决方案（或项目）语义上下文。
_Avoid_: solution index, project cache (as the public concept)

**WorkspaceSession**:
一次打开所对应的工作区会话；查询经此会话取得当前快照上的结果。
_Avoid_: global Roslyn Workspace handle

**Epoch**:
工作区内容代次；用于使分页游标与当前快照对齐。
_Avoid_: version stamp on symbol handles

**Trusted root**:
服务器允许读取的路径前缀集合；路径参数必须落在某一受信根之内。
_Avoid_: sandbox, chroot, allowlist path (as synonyms in docs)

**Soft budget**:
列表/扫描类查询的软性时间预算；用尽时允许部分结果而非一律失败。
_Avoid_: hard timeout that aborts with empty error

**Origin**:
符号声明来源（如手写源与生成文档）的正交分类轴之一。
_Avoid_: SourceKind as a catch-all enum for COM/dynamic/generators

**Attribution**:
符号或成员归属于哪个源生成器（或手写声明）的对应关系。
_Avoid_: "generated" boolean without generator identity

**Drift**:
工作区快照相对磁盘源文件可能已过期的状态。
_Avoid_: stale cache (without the drift/check semantics)

**Read-only tool surface**:
读工具（导航、分析、诊断、归因）的工具面。写侧只有显式 opt-in 的 Workspace Edit，不是通用写。
_Avoid_: treating the whole server as read-only after 2.0; apply_edit / patch_file / write / shell

**Workspace Edit**:
一次受限写操作的 preview / apply 合同：路径 + 旧/新文本 + 失效 SymbolHandle，外加 kind（Rename preview / Fix preview / Refactoring preview）。apply 必须匹配 kind；newName / Title / scope 不是入库合同。必须先 preview。
_Avoid_: apply_edit, patch_file, generic write; applying a preview under a different kind; RenamePreviewStore as the contract

**Rename preview**:
针对手写符号的 Workspace Edit 预览（`symbol_preview_rename`）。带 Epoch + TTL 的不透明 previewId；`Origin = SourceGenerator` 拒绝。apply 走自写抑制并推进 Epoch。
_Avoid_: renaming generated members; writing disk from preview

**XAML document**:
已注册框架的 UI 文档：Avalonia `.axaml`，或根 xmlns 为 MAUI URI 的 `.xaml`。工具名不分裂。
_Avoid_: generic XML file, WPF/WinUI document

**Binding path**:
从 `x:DataType` 出发、经 Core 内层 API 逐段走到属性/字段的路径（CompiledBindings 主路径）。
_Avoid_: code-behind-only DataContext walk

**P2 VB.NET**:
工作区可加载 SDK 风格 `.vbproj` / 混合解决方案；`workspace_list_projects` 以 `csharp` / `vb` 区分项目。VB 符号使用 `vb:` SymbolHandle，导航/分析/诊断/源生成器归因与 C# 同级。
_Avoid_: treating non-csharp handles as a blanket reject

**P3 F#**:
工作区可加载 SDK 风格 `.fsproj` / 混合解决方案；语言标记为 `fsharp`。F# 符号使用独立 FCS 栈上的 `fsharp:` SymbolHandle，导航/分析/诊断与 C# 同级。F# 源生成器归因不在本分期。
_Avoid_: LSP proxy, stuffing F# into Roslyn ISymbol


**DTO facade**:
MCP 工具面的 Core 外层：SymbolHandle、摘要、分页；按 Language 选一次 ILanguageAdapter。
_Avoid_: 再套一层只转发的查询 hop; merging tools into Core

**ILanguageAdapter**:
Core 语言接缝（ADR-0001 §5）。`SymbolHandle.Language` / project language 选一次。两个真实 adapter：Roslyn（C#/VB）与 FCS（F#）。XAML 是 Core 内层 API 的调用者，不是 adapter。
_Avoid_: per-module `if (fsharp:)`; treating XAML as a third adapter

**FSharpWorkspaceSnapshot**:
与 Roslyn Solution 并列、按同一 Epoch 冻结的 F# 项目/源文本快照。FCS adapter 只读这份快照。
_Avoid_: session.Solution for F#; stuffing F# into IWorkspaceSession.Solution

**MCP tool envelope**:
MCP 工具面的 ready session 与 JSON 信封（`TryGetReadySession` / `OkResult` / `ErrorResult` / `ToPolicyError`）。六个 tool class 只留 MCP 工具名与参数映射。
_Avoid_: merging tools into Core; copying the envelope into each tool class; generic apply_edit

**InteropKind**:
符号是否为 COM 互操作包装的正交标记（`None` / `ComImport` / `ComInteropWrapper`），不进入 Origin/Attribution。
_Avoid_: MetadataGenerated, COM-in-Origin

**Dynamic invocation site**:
`dynamic` 调用点（IOperation），带可选的静态接收者/参数类型；不是符号归因。
_Avoid_: treating dynamic as SymbolAttribution

**Diagnostic fix**:
针对一条 `project_diagnostics` 出现的、由 first-party 或项目已加载 CodeFixProvider 提供的修复动作。
_Avoid_: invented patch, generic apply_edit, analyzer downloaded just-in-time

**Fix preview**:
一次 Diagnostic fix（或 document / project Fix all）的 Workspace Edit 预览；带 Epoch + TTL 的 previewId，apply 前不得写盘。
_Avoid_: applying a CodeFix without preview

**Fix equivalence key**:
Roslyn CodeAction.EquivalenceKey，用于把同一文档或同一项目内的等价诊断收成一次 Fix all。
_Avoid_: fix-all-in-solution

**Code Refactoring**:
针对手写符号标识符处、由 first-party 或项目已加载 CodeRefactoringProvider 提供的命名写操作；与 Diagnostic fix 正交（无诊断定位）。
_Avoid_: invented patch, extract method selection, change-signature UI, generic apply_edit

**Refactoring preview**:
一次 Code Refactoring 的 Workspace Edit 预览；带 Epoch + TTL 的 previewId，apply 前不得写盘。
_Avoid_: applying a refactoring without preview
