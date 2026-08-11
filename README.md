# dotnet-mcp

一个面向 .NET 的 MCP（Model Context Protocol）服务器，目标能力：

- **多语言**：C#、VB.NET、F#
- **互操作与 DLR**：COM Interop、`dynamic` 等场景的符号分析
- **XAML**：语义级 XAML 分析，与 code-behind 符号联动
- **源生成器特别支持**：区分每个成员由哪个 Source Generator 生成

## 状态

P0 产品骨架已落地（`DotNetMcp.Server`）：stdio MCP 宿主、受信根路径策略、只读工具面守护。完整工作区加载与符号工具见后续 issue。

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

## 安全说明

1. **受信根**：服务器只能读受信根内的路径。多仓库场景请通过 `--roots` 或 `DOTNET_MCP_TRUSTED_ROOTS` 显式配置额外根；越界路径会被拒绝。
2. **打开即执行**：`workspace_open` 加载解决方案时会运行 **MSBuild 求值**以及项目引用的 **analyzer / 源生成器**——等同于在该仓库执行构建逻辑。**不要对不受信任的代码库使用。** 这是 Roslyn 语义分析固有性质，无法仅靠技术手段消除，只能显式告知。
3. **只读**：当前工具面为纯读侧；不提供写文件、任意命令执行或网络请求类工具（有快照/守护测试约束）。
4. **日志**：计划仅记录操作与路径元数据、不记录源码正文；默认本地、可关闭；无外部遥测。当前骨架仅有进程控制台日志，尚未实现独立审计开关。

## 开发约定

本仓库使用 [mattpocock/skills](https://github.com/mattpocock/skills) 工程技能链，其仓库级配置见 `AGENTS.md` 的 `## Agent skills` 一节与 `docs/agents/` 目录（issue 跟踪方式、triage 标签、领域文档布局）。
