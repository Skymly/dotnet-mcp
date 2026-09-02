# dotnet-mcp

An MCP (Model Context Protocol) server for .NET workspaces. It gives coding agents **compiler-accurate** symbol navigation, source-generator attribution, Avalonia/MAUI XAML queries, and a **restricted** Workspace Edit surface (rename / diagnostic fix / code refactoring). It is not a generic file writer, shell, or LSP proxy.

Package id on NuGet: **`Skymly.DotNetMcp`** (CLI command remains `dotnet-mcp`). Requires .NET 10+.

## What it can do

- Load `.sln` / `.slnx` / `.slnf` / SDK-style `.csproj` / `.vbproj` / `.fsproj`
- C# and VB.NET via Roslyn; F# via an FCS stack beside the Roslyn snapshot
- Resolve a name to a checksummed `SymbolHandle`, then go to definition, members, references, implementations, callers, type hierarchy
- Attribute handwritten vs source-generated members (generator assembly + type + version)
- Project diagnostics; list generators / generated sources / generator diagnostics; list `dynamic` invocations
- Avalonia `.axaml` and MAUI `.xaml`: class, xmlns, `x:Name`, compiled-binding path, XAML diagnostics
- Restricted writes: preview then apply for rename, diagnostic fix (including project Fix all), and code refactoring

## What it does not do

- Generic write / patch / create / delete file tools
- Shell, process, HTTP, or package-download tools
- WPF / WinUI XAML
- F# source-generator attribution, F# diagnostic fix, or F# code refactoring
- Extract-method / change-signature UIs
- Opening untrusted repositories safely — `workspace_open` **runs MSBuild evaluation and project analyzers/generators**

Tool names are locked by a snapshot test. Domain vocabulary: [`CONTEXT.md`](CONTEXT.md).

## Quick Start

Trusted roots are **required**. The process working directory is never an implicit sandbox.

### From NuGet (.NET 10+)

```bash
dnx Skymly.DotNetMcp --yes -- --roots /path/to/repo
```

MCP client (stdio):

```json
{
  "mcpServers": {
    "dotnet-mcp": {
      "command": "dnx",
      "args": ["Skymly.DotNetMcp", "--yes", "--", "--roots", "/path/to/repo"]
    }
  }
}
```

Equivalent: `dotnet tool exec Skymly.DotNetMcp --yes -- --roots /path/to/repo`, or `dotnet tool install -g Skymly.DotNetMcp` then `dotnet-mcp --roots /path/to/repo`.

On Windows, separate multiple roots with `;`. You can also set `DOTNET_MCP_TRUSTED_ROOTS`.

### Local pack (before NuGet)

```bash
dotnet pack src/DotNetMcp.Server -c Release -o ./artifacts
dotnet tool exec --source ./artifacts --yes Skymly.DotNetMcp -- --roots /path/to/repo
```

### Development run

```bash
dotnet run --project src/DotNetMcp.Server -- --roots /path/to/repo
```

stdio only. Framework-dependent .NET tool; NativeAOT is not required.

Typical agent loop: `workspace_open` (returns immediately) → poll `workspace_status` until `ready` → `symbol_resolve` → navigation / attribution. Prefer a `.slnf` or a single project for a large solution.

## MCP tools

| Group | Tools |
|------|--------|
| Workspace | `workspace_open` · `workspace_status` · `workspace_list_projects` · `workspace_check_drift` |
| Diagnostic fix | `diagnostics_list_fixes` · `diagnostics_preview_fix` · `diagnostics_apply_fix` |
| Symbol | `symbol_resolve` · `symbol_summary` · `symbol_goto_definition` · `symbol_members` · `symbol_find_references` · `symbol_find_implementations` · `symbol_find_callers` · `symbol_type_hierarchy` · `symbol_attribution` · `symbol_preview_rename` · `symbol_apply_rename` · `symbol_list_refactorings` · `symbol_preview_refactoring` · `symbol_apply_refactoring` |
| Project | `project_diagnostics` · `project_list_generators` · `project_list_generated_sources` · `project_list_generator_diagnostics` · `project_list_dynamic_invocations` |
| XAML | `xaml_resolve_class` · `xaml_list_xmlns` · `xaml_resolve_name` · `xaml_resolve_binding` · `xaml_diagnostics` |

## Security

1. **Trusted roots** — every path is canonicalized (including parent reparse points). Unresolvable links fail closed. Loaded project graphs and apply-paths are re-checked. Configure `--roots` or `DOTNET_MCP_TRUSTED_ROOTS`.
2. **Open means execute** — loading a solution runs MSBuild and referenced analyzers / source generators. Do not point this server at untrusted trees.
3. **Default read + named writes** — only rename / diagnostic fix / refactoring preview-apply. No generic write, command, or network tools.
4. **Audit** — local process logs (stderr under stdio). Tool name and path metadata only; no source text; no telemetry. Disable with `DOTNET_MCP_AUDIT=0`.

See [ADR-0004](docs/adr/0004-security-and-path-policy.md).

## Soft budgets and long-running load

List/scan tools honor a soft time budget ([ADR-0003](docs/adr/0003-long-running-operations-session-concurrency.md)): they return partial results + `nextCursor` instead of hanging past the common ~60s client `tools/call` cap. Progress notifications are **not** a keepalive.

| Environment variable | Default | Use |
|----------------------|---------|-----|
| `DOTNET_MCP_BUDGET_SINGLE_PROJECT_MS` | 5000 | Single-project compile (e.g. `project_diagnostics`) |
| `DOTNET_MCP_BUDGET_FIND_REFS_SCOPED_MS` | 5000 | Scoped find-references |
| `DOTNET_MCP_BUDGET_FIND_REFS_ENTIRE_MS` | 20000 | Entire-solution find-references |
| `DOTNET_MCP_BUDGET_BATCH_DIAGNOSTICS_MS` | 15000 | Reserved batch diagnostics |
| `DOTNET_MCP_BUDGET_FIXALL_PROJECT_MS` | 15000 | Project Fix all; over budget fails the preview |

Invalid values fall back to the defaults.

`workspace_open` never blocks the MCP request. Clients that do not opt into MCP Tasks should poll `workspace_status`. Details: `spikes/s3-mcp-long-running/CONCLUSIONS.md`.

## Development / CI

Product and tests target **net10.0**. Fixtures include net8.0 / net9.0 projects (needed for `MsBuildWorkspaceIntegrationTests`).

```bash
dotnet restore DotNetMcp.slnx
dotnet build DotNetMcp.slnx -c Release --no-restore
dotnet test DotNetMcp.slnx -c Release --no-build
```

CI: [`.github/workflows/ci.yml`](.github/workflows/ci.yml) (Ubuntu **and** Windows, SDK 8/9/10, pack + `McpServer` metadata check).

Product benches: [`docs/perf/benchmark.md`](docs/perf/benchmark.md).

```bash
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite fixtures
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite smoke
```

This repo uses [mattpocock/skills](https://github.com/mattpocock/skills); see `AGENTS.md` and `docs/agents/`.

---

## 中文

面向 Agent 的 .NET MCP 服务器：C# / VB / F# 符号导航、源生成器归因、Avalonia/MAUI XAML、以及受限 Workspace Edit（rename / Diagnostic fix / Code Refactoring）。**不是**通用写文件、shell 或 LSP 代理。

NuGet 包 id 为 **`Skymly.DotNetMcp`**（命令名仍是 `dotnet-mcp`）。必须通过 `--roots` 或 `DOTNET_MCP_TRUSTED_ROOTS` 配置受信根，**不再默认使用进程工作目录**。`workspace_open` 会运行 MSBuild 与 analyzer/源生成器，不要对不受信任的仓库使用。

安装：

```bash
dnx Skymly.DotNetMcp --yes -- --roots /path/to/repo
```
