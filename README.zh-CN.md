# dotnet-mcp

[English](README.md) | **简体中文** | [繁體中文](README.zh-TW.md) | [日本語](README.ja.md)

面向 .NET 工作区的 MCP（Model Context Protocol）服务器。它为编码智能体提供**编译器级准确**的符号导航、源生成器归因、Avalonia/MAUI XAML 查询，以及**受限的** Workspace Edit 能力（重命名 / 诊断修复 / 代码重构）。它不是通用文件写入工具、Shell 或 LSP 代理。

NuGet 包 ID：**`Skymly.DotNetMcp`**（CLI 命令：`dnmcp`）。需要 .NET 10+。

## 核心能力

- 加载 `.sln` / `.slnx` / `.slnf` / SDK 风格 `.csproj` / `.vbproj` / `.fsproj`
- 通过 Roslyn 支持 C# 与 VB.NET；通过与 Roslyn 快照并行的 FCS 技术栈支持 F#
- 将名称解析为带校验和的 `SymbolHandle`，进而执行转到定义、成员查询、查找引用、查找实现、查找调用方与类型层次结构
- 区分手写成员与源生成成员的归因（包含生成器程序集、类型与版本）
- 查询项目诊断；列出生成器 / 生成的源码 / 生成器诊断；列出 `dynamic` 调用点
- Avalonia `.axaml` 与 MAUI `.xaml` 支持：类解析、xmlns、`x:Name`、已编译绑定路径与 XAML 诊断
- 受限写入：针对重命名、诊断修复（含项目级 Fix all）与代码重构提供先预览后应用流程

## 不支持的功能

- 通用文件写入 / 补丁 / 创建 / 删除工具
- Shell、进程执行、HTTP 请求或包下载工具
- WPF / WinUI XAML
- F# 源生成器归因（对 F# 句柄调用 `symbol_attribution` 将返回 `GeneratorLanguageNotSupported`）、生成器查询、动态调用查询、诊断修复或代码重构
- 跨语言 F# 读写 —— F# 句柄仅搜索定义项目及其 F# 依赖方（或通过 `entireSolution` 搜索所有 F# 项目）；C#/VB 使用方仅会被列出名称而不会深入搜索，且被 C#/VB 依赖的可见 F# 符号无法重命名
- 提取方法（Extract-method） / 修改签名（change-signature）类交互界面
- 安全打开不受信任的仓库 —— `workspace_open` **会运行 MSBuild 求值以及项目分析器/生成器**

工具名称由快照测试锁定。领域词汇表见 [`CONTEXT.md`](CONTEXT.md)。

## 快速开始

**必须**配置受信根路径（trusted roots）。进程工作目录绝不会被隐式视为受信根。  
在 Windows 上使用 `;` 分隔多个根路径，也可设置环境变量 `DOTNET_MCP_TRUSTED_ROOTS`。当两者同时设置时，启动会取其并集，互不替代。`--roots=path` 与 `--roots path` 等价；空值将被拒绝。若未配置受信根或配置为空，进程将以退出码 1 退出，并在 stderr 输出单个 JSON 对象（`error`、`message`、`suggestedAction`），而不输出异常堆栈。

`Skymly.DotNetMcp` 已发布至 NuGet。推荐通过 `dnx` 运行，也可从源码构建或本地打包运行。

```bash
dnx Skymly.DotNetMcp --yes -- --roots /path/to/repo
```

MCP 客户端（stdio）配置：

```json
{
  "mcpServers": {
    "dnmcp": {
      "command": "dnx",
      "args": ["Skymly.DotNetMcp", "--yes", "--", "--roots", "/path/to/repo"]
    }
  }
}
```

等价命令：`dotnet tool exec Skymly.DotNetMcp --yes -- --roots /path/to/repo`，或 `dotnet tool install -g Skymly.DotNetMcp` 后运行 `dnmcp --roots /path/to/repo`。

### 从源码运行（开发调试）

在本仓库克隆目录中执行：

```bash
dotnet run --project src/DotNetMcp.Server -- --roots /path/to/repo
```

```json
{
  "mcpServers": {
    "dnmcp": {
      "command": "dotnet",
      "args": ["run", "--project", "src/DotNetMcp.Server", "--", "--roots", "/path/to/repo"]
    }
  }
}
```

### 本地打包测试

```bash
dotnet pack src/DotNetMcp.Server -c Release -o ./artifacts
dotnet tool exec --source ./artifacts --yes Skymly.DotNetMcp -- --roots /path/to/repo
```

仅支持 stdio。属于框架依赖型 .NET 工具，无需 NativeAOT。

典型智能体调用循环：`workspace_open`（立即返回）→ 轮询 `workspace_status` 直至 `ready` → `symbol_resolve` → 导航 / 归因。对于大型解决方案，建议优先打开 `.slnf` 或单个项目。对于重复出现的 `.slnf` 条目（或已被前置条目传递拉入的项目），系统会跳过并在加载警告中列出，而不会导致打开失败。

智能体接入说明见 [`docs/agent-guidance.md`](docs/agent-guidance.md)，其中提供了可直接粘贴到消费方仓库 `AGENTS.md` / `CLAUDE.md` 的片段——涵盖标准调用循环、1-based 位置模型与“先预览后应用”的写入规则。服务器也会在 initialize 结果的 `instructions` 中发送相同简要指引，并提供 `trace_symbol` / `safe_rename` 两个 prompt 引导智能体完成核心工作流。

## MCP 工具

| 分组 | 工具 |
|------|------|
| Workspace | `workspace_open` · `workspace_status` · `workspace_list_projects` · `workspace_check_drift` |
| Diagnostic fix | `diagnostics_list_fixes` · `diagnostics_preview_fix` · `diagnostics_apply_fix` |
| Symbol | `symbol_resolve` · `symbol_resolve_at` · `symbol_summary` · `symbol_goto_definition` · `symbol_source` · `symbol_members` · `symbol_find_references` · `symbol_find_implementations` · `symbol_find_callers` · `symbol_type_hierarchy` · `symbol_attribution` · `symbol_preview_rename` · `symbol_apply_rename` · `symbol_list_refactorings` · `symbol_preview_refactoring` · `symbol_apply_refactoring` |
| Project | `project_diagnostics` · `project_list_generators` · `project_list_generated_sources` · `project_list_generator_diagnostics` · `project_list_dynamic_invocations` |
| XAML | `xaml_resolve_class` · `xaml_list_xmlns` · `xaml_resolve_name` · `xaml_resolve_binding` · `xaml_diagnostics` |

`diagnostics_list_fixes` 与 `symbol_list_refactorings` 会返回 `epoch`。`fixIndex` 和 `refactoringIndex` 仅对该 `epoch` 有效。当 `epoch` 推进后，预览将返回 `FixListEpochMismatch` 或 `RefactoringListEpochMismatch`，绝不会误选其他操作。

若代码修复/重构提供程序抛出异常，`diagnostics_preview_fix` 与 `symbol_preview_refactoring` 仍返回 `FixApplyFailed` 或 `RefactoringApplyFailed`，并在消息中指明提供程序抛出异常，以区分于“未产生手写文件修改”。Fix all 遇到异常时会以 `FixApplyFailed` 停止，而不会跳过该处诊断。

未传入 `projectId` 时，`project_diagnostics` 会将加载失败的项目报告为 `error` 加上 `suggestedAction`，而非作为 `Severity=Error` 诊断返回；请勿将该条目传给 `diagnostics_list_fixes`。

当编译在分页前对全量诊断集合检查出 CS0006 或 CS0012（未能解析的元数据引用）时，`project_diagnostics` 会设置 `degraded` 与 `degradedReason`。此时列出的错误可能是工作区产物而非真实代码缺陷，建议在采信修复预览前先运行 `dotnet build` 校验。其他情况下省略这两个字段。  
在分页结果（`items` / `truncated` / `nextCursor`）中，`message` 为可选字段，无补充信息时省略。

对于 C#/VB 句柄，`symbol_find_references` 还会合并精确匹配的 XAML 使用点（绑定、`x:Class`、元素类型、`x:Name` 字段、事件处理程序）。源码命中优先分页返回；XAML 命中随后在同一分页流中返回并附带指明使用种类的 `xamlUsage` 字段——源码命中省略该字段。按名称匹配的 `Unverified` 点永不包含在内，XAML 扫描范围与源码引用一致（定义项目加依赖方，或使用 `entireSolution` 时覆盖全部项目）。两条通道共享一个软预算，游标会跨两阶段继续推进。

`symbol_resolve_at` 也接受已注册 XAML 文档内的位置：落在某个使用点内的位置会解析为目标句柄，返回 `resolvedFrom` = `Reference` 并将 `matched` 设为精确使用范围；若不在任何使用点内则返回 `NoSymbolAtPosition`。

未限定作用域的 C#/VB `symbol_resolve` 若触及软预算，会返回已验证的候选列表及 `incompleteProjectIds`。可使用候选结果中的 `summary.projectId` 限定下一次查找，或直接将源码位置传给 `symbol_resolve_at`。候选列表仅覆盖预算耗尽前已完成的工作。

`symbol_preview_rename` 会为 C#/VB 句柄同步更新 XAML：已注册 XAML 文档中的 `Exact` 使用点（绑定、`x:Class`、元素/特性名、`x:DataType`、标记扩展类型引用、`x:Name` 值、事件处理程序值）会在同一预览中成为常规 old/new 切片——每个切片仅编辑其范围内该符号的简单名称出现位置——且 `symbol_apply_rename` 通过同一写入管线应用（遵循 WriteSuppression，保留编码/BOM，不增删文件）。按名称匹配的 `Unverified` 引用报告在 `unverifiedXamlMentions` 中而不被写入。对 `Origin = SourceGenerator` 的拒绝策略依然有效，仅有一项例外：生成的 `x:Name` 字段可通过修改其 XAML 值及手写引用来完成重命名，生成器会在下次运行时重新生成该字段。诊断修复与代码重构的预览绝不会切片编辑 XAML 文档。

重命名预览与应用会返回拥有变更文件的已加载编译的 `affectedProjectIds`，涵盖链接文档、每个已加载的目标框架及已注册 XAML。如需检查是否引入新的编译错误，可在应用前后分别传入每个 ID 作为 `projectId` 调用 `project_diagnostics`，并使用 `nextCursor` 分页浏览。若某项目报告 `degraded`，请运行定向构建确认。此范围仅覆盖拥有变更的项目；如有需要请单独检查未受改动的使用方。

重命名的写入集合仅包括编译器可见引用以及上述 `Exact` XAML 使用点。它不会重写元数据名称字符串（例如 `GetTypeByMetadataName` 或 `ForAttributeWithMetadataName` 的实参）、编译器未绑定的短特性名、文档注释、普通注释或 PublicAPI 列表——这些仅按名称匹配的命中会报告在 `unverifiedMentions` 下（仅当设置 `includeDocMentions` 时才包含文档类命中），因此请先审阅该列表，切勿假定重命名已全部覆盖。

所有带位置信息的输出共享同一位置模型：`line` 与 `column` 均为 1-based，`column` 按 UTF-16 代码单元计数（制表符算作 1 列），`endColumn` 为开区间右边界——与编译器输出的 `file(line,col)` 约定一致。`start`/`length` 保留为用于切片的 UTF-16 偏移量。无源码位置（`InMetadata`、`None`）时省略位置字段；源生成文档中的位置为生成文本内的坐标。任一工具报告的位置均可原样传给下一工具——例如 `project_diagnostics` 返回行的 `line`/`column`/`endLine`/`endColumn` 可直接作为 `diagnostics_list_fixes` 与 `diagnostics_preview_fix` 的定位参数。

`symbol_attribution` 在声明可用性与生成器身份之外还报告第三维度：`triggers[]` 会在根符号及每个成员上指出触发生成该成员的手写代码——编译器绑定的 `PartialDefinition` 对应声明（C# 与 VB）、`Attribute` 声明（如 `[ObservableProperty]`、`[RelayCommand]` 或 `[JsonSerializable]`），或 `XamlElement`（通过 XAML 使用索引解析的 `x:Name` 字段或 `x:Class` 文档根）。每个触发器包含 `confidence`（编译器绑定证据为 `Exact`，唯一反向命名约定查找为 `Convention`）、统一位置四元组、`detail`，以及可通过 `symbol_summary` 重新解析的代码触发器 `handle`。手写符号返回 `triggers: []`；若生成成员所属生成器未匹配任何规则且无 partial 对应声明，则返回 `triggers: []` 并附带 `triggerMessage`，而不会猜测位置。

传给 `workspace_open` 或 XAML 工具的 `path` 若为空或纯空白，将返回空路径错误（`InvalidWorkspacePath` 或 `XamlDocumentNotFound`），而非 `PathOutsideTrustedRoots`。  
`workspace_open` 返回与 `workspace_status` 相同的进度字段，当后台加载尚未开始即失败时包含 `errorCode`（为 null 时省略）。

若源生成器或分析器引用了比本服务器内置版本更新的 `Microsoft.CodeAnalysis`，Roslyn 会拒绝加载，其产物会悄然从 `project_list_generators`、`project_list_generated_sources` 与 `symbol_attribution` 中消失。工作区就绪后，`workspace_status.warnings` 会携带一条包含文件名（不含路径）的 `AnalyzerReferencesNewerCompiler` 警告；该信息直接从程序集元数据读取，无需加载分析器即可得知。解决方法是升级至内置更新版本 Roslyn 的 dotnet-mcp 发布版。

对于 MSBuild 已声明但未解析出可加载程序集的分析器路径——例如输出尚未构建的 `OutputItemType="Analyzer"` 项目引用或磁盘上缺失的 DLL——也不会被静默忽略：`project_list_generators` 会在 `unresolvedAnalyzerReferences` 中列出，并附带 `message` 提示先构建分析器项目再重新打开工作区。生成器身份始终仅对实际成功加载的程序集报告。

## 安全性

1. **受信根（Trusted roots）** —— 每个路径都会规范化解析（包括父级重解析点），无法解析的链接按 fail-closed 失败关闭。已加载的项目图谱与应用写入路径都会再次校验。通过 `--roots` / `--roots=path` 或 `DOTNET_MCP_TRUSTED_ROOTS` 配置；同时设置时取并集。路径比较在 Windows 上忽略大小写，在 Linux 和 macOS 上区分大小写（即使默认 macOS 卷不区分大小写，也请使用与配置根一致的大小写写法）。启动配置失败时向 stderr 输出 `TrustedRootsConfigurationFailed`，而不是打印堆栈跟踪。
2. **打开即执行** —— 加载解决方案会执行 MSBuild 及所引用的分析器 / 源生成器。切勿将本服务器指向不受信任的代码树。`.slnf` 项目条目会在 MSBuild 打开前先行校验；`.sln` / `.slnx` / 单项目图谱会在加载后校验，因此超出受信根的 `ProjectReference` 会先被求值再遭拒绝。已加载的文档与分析器引用也可能解析到工具链根目录下——即 NuGet 包缓存和 dotnet 安装目录——因为 xunit v3 等包会向编译注入缓存中的源文件且构建本就会执行这些目录中的代码；所有项目路径以及全部写入集合仍严格要求位于受信根内。
3. **默认只读 + 限定写入** —— 仅开放重命名 / 诊断修复 / 重构的先预览后应用。无通用写入、命令执行或网络工具。对于无 BOM 且非合法 UTF-8 的文档，应用会拒绝且不写入（`SourceEncodingRefused`）。
4. **审计日志** —— 写往本地进程日志（stdio 模式下为 stderr）。仅记录工具名称与路径元数据，绝不记录源码文本，无任何遥测。可通过 `DOTNET_MCP_AUDIT=0` 关闭。日志配置仅读取环境变量及服务器自身安装目录，不会读取 MCP 客户端启动服务器所在目录下的 `appsettings.json`，从而防止被打开的仓库篡改格式或静默审计日志。

详见 [ADR-0004](docs/adr/0004-security-and-path-policy.md)。

## 软预算与长耗时负载

下列工具会在到达软预算时停止（参见 [ADR-0003](docs/adr/0003-long-running-operations-session-concurrency.md)），返回部分结果与 `nextCursor`，避免超过常见约 60 秒的客户端 `tools/call` 超时上限：`project_diagnostics`、`xaml_diagnostics`、`project_list_dynamic_invocations`、`symbol_find_references`、`symbol_find_callers`。对于 F# 句柄，预算会在各个项目扫描之间检查——预算用尽后返回部分分页与可在作用域内下一个项目继续的 `nextCursor`；单个项目的 FCS 检查本身不受该预算中断。下列工具不设预算，运行至完成（或客户端超时）：`symbol_members`、`symbol_type_hierarchy`、`symbol_find_implementations`、`project_list_generated_sources`、`project_list_generator_diagnostics`。`symbol_resolve` 受同一预算限制，但超预算时返回 `SoftBudgetExceeded` 而非部分分页。进度通知**不是**保活（keepalive）机制。

| 环境变量 | 默认值 | 用途 |
|----------|--------|------|
| `DOTNET_MCP_BUDGET_SINGLE_PROJECT_MS` | 5000 | 单项目编译（如 `project_diagnostics`） |
| `DOTNET_MCP_BUDGET_FIND_REFS_SCOPED_MS` | 5000 | 限定范围查找引用 |
| `DOTNET_MCP_BUDGET_FIND_REFS_ENTIRE_MS` | 20000 | 全解决方案查找引用 |
| `DOTNET_MCP_BUDGET_BATCH_DIAGNOSTICS_MS` | 15000 | 省略 `projectId` 时的 `project_diagnostics` |
| `DOTNET_MCP_BUDGET_FIXALL_PROJECT_MS` | 15000 | 项目级 Fix all；超出预算则预览失败 |

文档范围 Fix all 最多应用 32 个修复。项目范围 Fix all 也会在固定应用上限（默认 64 个）处停止。这两项应用上限均不通过环境变量配置。

`0` 表示预算已耗尽——列表工具会立即返回截断分页（可能为空）与 `nextCursor`，项目 Fix all 会导致预览失败；`symbol_find_references` 与 `symbol_find_callers` 仍会完成当前文档（且 `symbol_find_references` 会在其 XAML 通道上完成当前项目）以确保游标前进，因此该页仍可能包含条目；负数或无法解析的值回退为默认值。

`workspace_open` 永不阻塞 MCP 请求。未启用 MCP Tasks 的客户端应轮询 `workspace_status`。`workspace_status.watcher` 取值为 `ok`、`lost` 或 `off`；`lost` 表示文件监视失败并已尝试重启。详情见 `spikes/s3-mcp-long-running/CONCLUSIONS.md`，该笔记说明了预研阶段 `jobId` 形状与产品 DTO（无 `jobId`）的差异。

## 开发 / CI

产品与测试目标为 **net10.0**。测试夹具包含 net8.0 / net9.0 项目（`MsBuildWorkspaceIntegrationTests` 所需）。

```bash
dotnet restore DotNetMcp.slnx
dotnet build DotNetMcp.slnx -c Release --no-restore
dotnet test DotNetMcp.slnx -c Release --no-build --verbosity normal
```

仓库没有 `packages.lock.json`，也未设置 `RestorePackagesWithLockFile`。直接 `PackageReference` 版本在 `Directory.Packages.props` 中集中锁定；测试夹具与预研项目选择退出并使用各自锁定版本。传递依赖解析未被锁定，因此后续还原可能会选中不同的传递依赖版本。

CI 配置位于 [`.github/workflows/ci.yml`](.github/workflows/ci.yml)（Ubuntu、Windows **和** macOS；产品 SDK 通过 [`global.json`](global.json) 指定 10.0.x 前滚，夹具 SDK 为 8.0.x/9.0.x；包含 C# 空白格式检查、已知漏洞包检查、打包及 `McpServer` 元数据校验，以及按照“快速开始”中 `dotnet tool exec` 形态运行打包工具的冒烟测试）。第三方 Actions 均已固定到 commit SHA 且工作流令牌为只读。独立的 [`codeql.yml`](.github/workflows/codeql.yml) 工作流会在每次 PR 与每周对 C# 源码运行 CodeQL；Dependabot 每周提交 NuGet 更新（按包家族分组）与 GitHub Actions 更新。

空白格式规则定义于根目录 [`.editorconfig`](.editorconfig)（LF 换行符、C# 4 空格缩进）；夹具与预研目录除外。CI 在 Ubuntu 上执行检查，你也可以在 LF 换行符检出目录上本地运行相同命令：

```bash
dotnet format whitespace . --folder --include src/ tests/DotNetMcp.Tests/ benches/ --exclude "**/bin/**" "**/obj/**" --verify-no-changes
```

每个 `--include` 目录末尾都必须带斜杠；缺少末尾斜杠时会被当作文件名匹配，导致零文件命中并在未检查任何文件的情况下直接通过。

产品基准测试见 [`docs/perf/benchmark.md`](docs/perf/benchmark.md)。任务级 A/B 评测记录（包含失败案例）见 [`docs/perf/agent-eval.md`](docs/perf/agent-eval.md)，该记录不关闭 `#528` 发布门禁。

```bash
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite fixtures
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite smoke
```

本仓库使用了 [mattpocock/skills](https://github.com/mattpocock/skills)；参见 `AGENTS.md` 与 `docs/agents/`。
