# dotnet-mcp

一个面向 .NET 的 MCP（Model Context Protocol）服务器。**路线图**目标包括：

- **多语言**：C#（P0）、VB.NET（P2）、F#（P3）
- **互操作与 DLR**：COM Interop、`dynamic` 等场景的符号分析（P3）
- **XAML**：语义级 XAML 分析，与 code-behind 符号联动（P1 Avalonia）
- **源生成器特别支持**：区分每个成员由哪个 Source Generator 生成（P0，已交付）

## 状态

P0 **读侧 1.0** 已可演示（`DotNetMcp.Server`）：stdio MCP 宿主、受信根路径策略、MSBuild 工作区加载（`.sln` / `.slnx` / `.slnf` / 项目）、C# 符号导航（定义/成员/引用/实现/层级/调用者）、项目诊断、源生成器列表/生成源/诊断与符号归因。P1 Avalonia `xaml_resolve_class`（`.axaml` → `x:Class` 符号）已交付；xmlns / x:Name / Binding / 语义诊断仍在后续 P1 票中。VB / F# 仍属后续分期。

### 当前 MCP 工具面（只读）

| 分组 | 工具 |
|------|------|
| Workspace | `workspace_open` · `workspace_status` · `workspace_list_projects` · `workspace_check_drift` |
| Symbol | `symbol_resolve` · `symbol_summary` · `symbol_goto_definition` · `symbol_members` · `symbol_find_references` · `symbol_find_implementations` · `symbol_find_callers` · `symbol_type_hierarchy` · `symbol_attribution` |
| Project | `project_diagnostics` · `project_list_generators` · `project_list_generated_sources` · `project_list_generator_diagnostics` |
| XAML | `xaml_resolve_class` · `xaml_list_xmlns` · `xaml_resolve_name` |

工具面由快照/守护测试约束为纯读侧（无写文件、任意命令或网络类工具）。领域词汇见根目录 [`CONTEXT.md`](CONTEXT.md)。

## 安装 / 启用

包发布到 NuGet 后，可用零安装一行启用（.NET 10+）：

```bash
dnx dotnet-mcp --yes
```

MCP 客户端（stdio）示例：

```json
{
  "mcpServers": {
    "dotnet-mcp": {
      "command": "dnx",
      "args": ["dotnet-mcp", "--yes"]
    }
  }
}
```

等价写法：`dotnet tool exec dotnet-mcp --yes`，或先 `dotnet tool install -g dotnet-mcp` 再直接跑 `dotnet-mcp`。

### 本地 pack（开发验证）

尚未上架 NuGet 时，从本仓库打包并运行：

```bash
dotnet pack src/DotNetMcp.Server -c Release -o ./artifacts
dotnet tool exec --source ./artifacts --yes dotnet-mcp
```

### 开发运行

```bash
dotnet run --project src/DotNetMcp.Server
```

MCP 客户端以 **stdio** 连接该进程。默认受信根为进程当前工作目录。本工具为 **framework-dependent** .NET tool，**不要求** NativeAOT。

### 受信根配置

| 来源 | 说明 |
|------|------|
| `--roots <paths>` | 命令行；多个根用 `Path.PathSeparator` 分隔（Windows 上为 `;`） |
| `DOTNET_MCP_TRUSTED_ROOTS` | 环境变量，分隔规则同上 |
| 缺省 | 当前工作目录 |

所有路径参数经规范化（含 `..` 与符号链接/junction 解析）后必须落在某个受信根之内，否则拒绝并返回带 `SuggestedAction` 的错误（不回显目标内容）。详见 [ADR-0004](docs/adr/0004-security-and-path-policy.md)。

### 软预算配置

列表/扫描类工具遵守软性时间预算（ADR-0003）：超预算返回部分结果 + `nextCursor`，而非硬错误。默认值见 ADR 表；可通过环境变量（毫秒整数）覆盖，无需重编译：

| 环境变量 | 默认 | 用途 |
|----------|------|------|
| `DOTNET_MCP_BUDGET_SINGLE_PROJECT_MS` | 5000 | 单项目编译（如 `project_diagnostics`） |
| `DOTNET_MCP_BUDGET_FIND_REFS_SCOPED_MS` | 5000 | 作用域内 Find References |
| `DOTNET_MCP_BUDGET_FIND_REFS_ENTIRE_MS` | 20000 | 全解决方案 Find References |
| `DOTNET_MCP_BUDGET_BATCH_DIAGNOSTICS_MS` | 15000 | 批量诊断（预留） |

非法或缺失值回退到上表默认。

### 审计日志配置

| 环境变量 | 默认 | 说明 |
|----------|------|------|
| `DOTNET_MCP_AUDIT` | 开启 | 设为 `0` / `false` / `off` / `no`（大小写不敏感）可关闭本地审计 |

审计写入进程本地日志（stdio 主机下为 stderr），**无外部遥测**。记录工具名与路径元数据（含路径策略拒绝），**不记录源码或生成源正文**。

### 长耗时与客户端兼容

加载与查询遵守 [ADR-0003](docs/adr/0003-long-running-operations-session-concurrency.md)：

- **基线（所有客户端）**：`workspace_open` **立即返回**，用 `workspace_status` 轮询至 `ready`；勿在 loading 时重试 `workspace_open`。
- **Tasks 增强**：服务器启用 MCP Tasks（`.WithTasks`）。仅当客户端协议 ≥ **2026-07-28** 且显式 opt-in `io.modelcontextprotocol/tasks` 时，可用 `tasks/get` / `tasks/cancel`；未 opt-in 仍走同步 `tools/call` + 手工 open/status。
- **超时**：常见客户端 `tools/call` 硬顶约 **60s**；**progress 不是 keepalive**，不能靠进度通知延长超时。

| 客户端 | Tasks | 建议路径 |
|--------|--------|----------|
| C# MCP Client（协议 2026-07-28 + opt-in） | 支持 | Tasks 或手工 open/status |
| Claude Desktop / Cursor / VS Code Copilot（TS SDK 系） | 通常未 opt-in | 手工 open/status；按 ~60s 规划 |
| Claude Code CLI | 勿默认依赖 | 手工 open/status；可用 `MCP_TIMEOUT`（ms）延长 |

证据与细节见 `spikes/s3-mcp-long-running/CONCLUSIONS.md`。

## 安全说明

1. **受信根**：服务器只能读受信根内的路径。多仓库场景请通过 `--roots` 或 `DOTNET_MCP_TRUSTED_ROOTS` 显式配置额外根；越界路径会被拒绝。
2. **打开即执行**：`workspace_open` 加载解决方案时会运行 **MSBuild 求值**以及项目引用的 **analyzer / 源生成器**——等同于在该仓库执行构建逻辑。**不要对不受信任的代码库使用。** 这是 Roslyn 语义分析固有性质，无法仅靠技术手段消除，只能显式告知。
3. **只读**：当前工具面为纯读侧；不提供写文件、任意命令执行或网络请求类工具（有快照/守护测试约束）。
4. **日志**：已实现本地审计（默认开启）：记录工具调用与路径策略拒绝的工具名/路径元数据，不记录源码或生成源正文；写入进程本地日志（stdio 下为 stderr），可用 `DOTNET_MCP_AUDIT=0`（或 `false`/`off`/`no`）关闭；无外部遥测。

## 开发 / CI

- **SDK**：产品与测试目标框架为 **net10.0**，需安装 .NET 10 SDK。集成测试夹具含 net8.0 / net9.0 项目，本地若跑 `MsBuildWorkspaceIntegrationTests` 需同时具备对应 SDK。
- **MSBuild**：真实加载路径经 `MSBuildLocator` 注册本机 SDK（优先 `DOTNET_ROOT` 下最新 SDK）。与 `dotnet build` 使用同一套求值环境。
- **测试**（产品 solution，不含 `spikes/`）：

```bash
dotnet restore DotNetMcp.slnx
dotnet build DotNetMcp.slnx -c Release --no-restore
dotnet test DotNetMcp.slnx -c Release --no-build
```

GitHub Actions 工作流见 [`.github/workflows/ci.yml`](.github/workflows/ci.yml)（ubuntu + SDK 8/9/10，同上命令）。

## 开发约定

本仓库使用 [mattpocock/skills](https://github.com/mattpocock/skills) 工程技能链，其仓库级配置见 `AGENTS.md` 的 `## Agent skills` 一节与 `docs/agents/` 目录（issue 跟踪方式、triage 标签、领域文档布局）。
