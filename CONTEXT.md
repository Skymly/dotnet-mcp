# DotNetMCP

面向 AI 编程助手的 .NET 读侧 MCP 服务器上下文：解决方案工作区、符号句柄与源生成器归因。架构取舍见 `docs/adr/`。

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
当前产品阶段仅提供导航、分析、诊断与归因类工具的工具面边界。
_Avoid_: refactor tools, apply_edit in v0

**XAML document**:
受信根内的 Avalonia `.axaml` 文档；P1 只注册 Avalonia，不加载其它 UI 框架。
_Avoid_: generic XML file, WPF/MAUI document (as the current product surface)

**Binding path**:
从 `x:DataType` 出发、经 Core 内层 API 逐段走到属性/字段的路径（CompiledBindings 主路径）。
_Avoid_: code-behind-only DataContext walk

