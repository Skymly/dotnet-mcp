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
一次受限写操作的 preview / apply 合同：按文件的路径 + 旧/新文本 + 将失效的 SymbolHandle。必须先 preview。
_Avoid_: apply_edit, patch_file, generic write

**Rename preview**:
针对手写符号的 Workspace Edit 预览（`symbol_preview_rename`）。带 Epoch + TTL 的不透明 previewId；`Origin = SourceGenerator` 拒绝。本分期 apply 另票。
_Avoid_: renaming generated members; writing disk from preview

**XAML document**:
受信根内的 Avalonia `.axaml` 文档；P1 只注册 Avalonia，不加载其它 UI 框架。
_Avoid_: generic XML file, WPF/MAUI document (as the current product surface)

**Binding path**:
从 `x:DataType` 出发、经 Core 内层 API 逐段走到属性/字段的路径（CompiledBindings 主路径）。
_Avoid_: code-behind-only DataContext walk

**P2 VB.NET**:
工作区可加载 SDK 风格 `.vbproj` / 混合解决方案；`workspace_list_projects` 以 `csharp` / `vb` 区分项目。VB 符号使用 `vb:` SymbolHandle，导航/分析/诊断/源生成器归因与 C# 同级。
_Avoid_: treating non-csharp handles as a blanket reject

**P3 F#**:
工作区可加载 SDK 风格 `.fsproj` / 混合解决方案；语言标记为 `fsharp`。F# 符号使用独立 FCS 栈上的 `fsharp:` SymbolHandle，导航/分析/诊断与 C# 同级。F# 源生成器归因不在本分期。
_Avoid_: LSP proxy, stuffing F# into Roslyn ISymbol

**InteropKind**:
符号是否为 COM 互操作包装的正交标记（`None` / `ComImport` / `ComInteropWrapper`），不进入 Origin/Attribution。
_Avoid_: MetadataGenerated, COM-in-Origin

**Dynamic invocation site**:
`dynamic` 调用点（IOperation），带可选的静态接收者/参数类型；不是符号归因。
_Avoid_: treating dynamic as SymbolAttribution
