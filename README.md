# dotnet-mcp

<!-- mcp-name: io.github.skymly/dotnet-mcp -->

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
- F# source-generator attribution (`symbol_attribution` on an F# handle returns `GeneratorLanguageNotSupported`), generator queries, dynamic invocation queries, diagnostic fix, or code refactoring
- Cross-language F# reads and writes — F# handles search the defining project plus its F# dependents (or every F# project with `entireSolution`), but C#/VB consumers are named, not searched, and a visible F# symbol with C#/VB dependents cannot be renamed
- Extract-method / change-signature UIs
- Opening untrusted repositories safely — `workspace_open` **runs MSBuild evaluation and project analyzers/generators**

Tool names are locked by a snapshot test. Domain vocabulary: [`CONTEXT.md`](CONTEXT.md).

## Quick Start

Trusted roots are **required**. The process working directory is never an implicit trusted root.
On Windows, separate multiple roots with `;`. You can also set `DOTNET_MCP_TRUSTED_ROOTS`. When both are set, startup uses their union; neither replaces the other. `--roots=path` is the same as `--roots path`; an empty value is rejected. A missing or empty roots configuration exits with code 1 and writes one JSON object to stderr (`error`, `message`, `suggestedAction`) instead of a stack trace.

The NuGet package **`Skymly.DotNetMcp` is not published yet**. Use a source build or a local pack until it is.

### Development run

```bash
dotnet run --project src/DotNetMcp.Server -- --roots /path/to/repo
```

MCP client (stdio), from a clone of this repo:

```json
{
  "mcpServers": {
    "dotnet-mcp": {
      "command": "dotnet",
      "args": ["run", "--project", "src/DotNetMcp.Server", "--", "--roots", "/path/to/repo"]
    }
  }
}
```

### Local pack

```bash
dotnet pack src/DotNetMcp.Server -c Release -o ./artifacts
dotnet tool exec --source ./artifacts --yes Skymly.DotNetMcp -- --roots /path/to/repo
```

### From NuGet (after the package is published)

Once `Skymly.DotNetMcp` is on nuget.org, `dnx` can become the preferred install path again:

```bash
dnx Skymly.DotNetMcp --yes -- --roots /path/to/repo
```

Equivalent after publish: `dotnet tool exec Skymly.DotNetMcp --yes -- --roots /path/to/repo`, or `dotnet tool install -g Skymly.DotNetMcp` then `dotnet-mcp --roots /path/to/repo`.

stdio only. Framework-dependent .NET tool; NativeAOT is not required.

Typical agent loop: `workspace_open` (returns immediately) → poll `workspace_status` until `ready` → `symbol_resolve` → navigation / attribution. Prefer a `.slnf` or a single project for a large solution. `.slnf` entries already in the workspace (duplicates, or projects an earlier entry pulled in transitively) are skipped and named in the load warnings rather than failing the open.

For agent setup, [`docs/agent-guidance.md`](docs/agent-guidance.md) holds a copy-paste block for a consuming repo's `AGENTS.md` / `CLAUDE.md` — the loop, the 1-based position model, and the preview → apply write rule. The server also sends the same brief in the initialize result `instructions` and ships `trace_symbol` / `safe_rename` prompts that walk an agent through the two core workflows.

## MCP tools

| Group | Tools |
|------|--------|
| Workspace | `workspace_open` · `workspace_status` · `workspace_list_projects` · `workspace_check_drift` |
| Diagnostic fix | `diagnostics_list_fixes` · `diagnostics_preview_fix` · `diagnostics_apply_fix` |
| Symbol | `symbol_resolve` · `symbol_resolve_at` · `symbol_summary` · `symbol_goto_definition` · `symbol_source` · `symbol_members` · `symbol_find_references` · `symbol_find_implementations` · `symbol_find_callers` · `symbol_type_hierarchy` · `symbol_attribution` · `symbol_preview_rename` · `symbol_apply_rename` · `symbol_list_refactorings` · `symbol_preview_refactoring` · `symbol_apply_refactoring` |
| Project | `project_diagnostics` · `project_list_generators` · `project_list_generated_sources` · `project_list_generator_diagnostics` · `project_list_dynamic_invocations` |
| XAML | `xaml_resolve_class` · `xaml_list_xmlns` · `xaml_resolve_name` · `xaml_resolve_binding` · `xaml_diagnostics` |

`diagnostics_list_fixes` and `symbol_list_refactorings` return `epoch`. `fixIndex` and `refactoringIndex` are valid only for that epoch. After it advances, preview fails with `FixListEpochMismatch` or `RefactoringListEpochMismatch` instead of selecting another action.

If a code-action provider throws, `diagnostics_preview_fix` and `symbol_preview_refactoring` still return `FixApplyFailed` or `RefactoringApplyFailed`, but the message says the provider threw. That is not the same as a fix that produced no handwritten change. Fix all stops with `FixApplyFailed` instead of skipping that occurrence.

`project_diagnostics` without `projectId` reports a failed project as `error` plus `suggestedAction`, not as a `Severity=Error` diagnostic. Do not pass that row to `diagnostics_list_fixes`.

`project_diagnostics` sets `degraded` plus a `degradedReason` when the compilation reports CS0006 or CS0012 — metadata references that did not resolve — computed over the project's full diagnostic set before paging. Listed errors may then be workspace artifacts rather than real code defects; verify with `dotnet build` before trusting fix previews. Both fields are omitted otherwise.
On paged results (`items` / `truncated` / `nextCursor`), `message` is optional and omitted when there is nothing to add.

`symbol_find_references` also merges Exact XAML usage points (bindings, `x:Class`, element types, `x:Name` fields, event handlers) for C#/VB handles. Source hits page first; XAML hits follow in the same paged stream and carry `xamlUsage` naming the usage kind — source hits omit the field. Name-matched `Unverified` points are never included, and the XAML scan covers the same scope as source references (defining project plus dependents, or every project with `entireSolution`). One soft budget covers both lanes and the cursor walks both stages.

`symbol_resolve_at` accepts positions inside registered XAML documents too: a covering usage point resolves to its target handle with `resolvedFrom` = `Reference` and `matched` set to the exact usage span, while a position outside every usage returns `NoSymbolAtPosition`.

`symbol_preview_rename` keeps XAML in sync for C#/VB handles: `Exact` usage points in registered XAML documents (bindings, `x:Class`, element/attribute names, `x:DataType`, markup type refs, `x:Name` values, event-handler values) become ordinary old/new slices in the same preview — each edits only the symbol's simple-name occurrence inside its span — and `symbol_apply_rename` writes them through the same pipeline (WriteSuppression, encoding/BOM preserved, no files added or removed). Name-matched `Unverified` usages are reported under `unverifiedXamlMentions` without being written. The `Origin = SourceGenerator` refusal stays, with one exception: a generated `x:Name` field is renamed through its XAML value plus handwritten references, and the generator re-emits the field on its next run. Diagnostic fix and code refactoring previews never slice XAML documents.

All located outputs share one position model: `line` and `column` are 1-based, columns count UTF-16 code units (a tab is one column), and `endColumn` is exclusive — the same `file(line,col)` convention the compilers print. `start`/`length` remain as UTF-16 offsets for slicing. Locations without a source position (`InMetadata`, `None`) omit the position fields, and positions in source-generated documents are coordinates inside the generated text. A position reported by one tool feeds the next tool verbatim — for example a `project_diagnostics` row's `line`/`column`/`endLine`/`endColumn` are exactly the locator arguments of `diagnostics_list_fixes` and `diagnostics_preview_fix`.

`symbol_attribution` reports a third axis beside declaration availability and generator identity: `triggers[]` on the root symbol and every member name the handwritten code that caused a generated member — a `PartialDefinition` counterpart bound by the compiler (C# and VB), an `Attribute` declaration such as `[ObservableProperty]`, `[RelayCommand]`, or `[JsonSerializable]`, or a `XamlElement` (`x:Name` field or the `x:Class` document root, resolved through the XAML usage index). Each trigger carries `confidence` (`Exact` for compiler-bound evidence, `Convention` for unique name-inverse lookups), the unified position quad, `detail`, and a `handle` that re-resolves through `symbol_summary` for code triggers. Handwritten symbols return `triggers: []`; a generated member whose generator matches no rule and has no partial counterpart returns `triggers: []` plus a `triggerMessage` instead of a guessed location.

An empty or whitespace `path` to `workspace_open` or an XAML tool is an empty-path error (`InvalidWorkspacePath` or `XamlDocumentNotFound`), not `PathOutsideTrustedRoots`.
`workspace_open` returns the same progress fields as `workspace_status`, including `errorCode` when the open fails before the background load. A null `errorCode` is omitted.

A source generator or analyzer that references a newer `Microsoft.CodeAnalysis` than the server bundles is refused by Roslyn, and its output silently drops out of `project_list_generators`, `project_list_generated_sources`, and `symbol_attribution`. Once the workspace is ready, `workspace_status.warnings` carries an `AnalyzerReferencesNewerCompiler` entry that names the file (not its path). It is read from the assembly's metadata, so the analyzer is not loaded to find out. The fix is a dotnet-mcp release that bundles a newer Roslyn.

An analyzer path MSBuild declared but that resolves to no loadable assembly — an `OutputItemType="Analyzer"` project reference whose output is not built, or a DLL missing on disk — is not a silent miss either: `project_list_generators` lists it under `unresolvedAnalyzerReferences` with a `message` saying to build the analyzer project and reopen the workspace. Generator identities are only ever reported for assemblies that actually loaded.

## Security

1. **Trusted roots** — every path is canonicalized (including parent reparse points). Unresolvable links fail closed. Loaded project graphs and apply-paths are re-checked. Configure `--roots` / `--roots=path` or `DOTNET_MCP_TRUSTED_ROOTS`. When both are set, the trusted set is their union. Path comparison ignores case on Windows and respects it on Linux and macOS, including a default macOS volume that does not, so write a path with the same casing as the configured root. Startup failure uses `TrustedRootsConfigurationFailed` on stderr, not a stack trace.
2. **Open means execute** — loading a solution runs MSBuild and referenced analyzers / source generators. Do not point this server at untrusted trees. `.slnf` project entries are checked before MSBuild opens them; `.sln` / `.slnx` / single-project graphs are checked after load, so an out-of-root `ProjectReference` is evaluated before it is rejected. Loaded documents and analyzer references may also resolve under toolchain roots — the NuGet package cache and the dotnet install directory — because packages like xunit v3 inject package-cache sources into the compile and builds already execute code from those roots; project paths and the whole write set still require trusted roots.
3. **Default read + named writes** — only rename / diagnostic fix / refactoring preview-apply. No generic write, command, or network tools. Apply refuses, and does not write, a document that has no BOM and is not valid UTF-8 (`SourceEncodingRefused`).
4. **Audit** — local process logs (stderr under stdio). Tool name and path metadata only; no source text; no telemetry. Disable with `DOTNET_MCP_AUDIT=0`. Logging configuration comes from environment variables and the server's own install directory. An `appsettings.json` in the directory the MCP client launched the server from is not read, so an opened repository cannot reformat or silence the audit log.

See [ADR-0004](docs/adr/0004-security-and-path-policy.md).

## Soft budgets and long-running load

These tools stop at the soft budget ([ADR-0003](docs/adr/0003-long-running-operations-session-concurrency.md)) and return partial results + `nextCursor` instead of hanging past the common ~60s client `tools/call` cap: `project_diagnostics`, `xaml_diagnostics`, `project_list_dynamic_invocations`, `symbol_find_references`, `symbol_find_callers`. For F# handles the budget is checked between project scans — a spent budget yields the partial page plus a `nextCursor` that resumes at the next project in the scope; the per-project FCS check itself is not budgeted. These run to completion (or to the client timeout) without a budget: `symbol_members`, `symbol_type_hierarchy`, `symbol_find_implementations`, `project_list_generated_sources`, `project_list_generator_diagnostics`. `symbol_resolve` is bounded by the same budget, but a budget hit fails with `SoftBudgetExceeded` rather than a partial page. Progress notifications are **not** a keepalive.

| Environment variable | Default | Use |
|----------------------|---------|-----|
| `DOTNET_MCP_BUDGET_SINGLE_PROJECT_MS` | 5000 | Single-project compile (e.g. `project_diagnostics`) |
| `DOTNET_MCP_BUDGET_FIND_REFS_SCOPED_MS` | 5000 | Scoped find-references |
| `DOTNET_MCP_BUDGET_FIND_REFS_ENTIRE_MS` | 20000 | Entire-solution find-references |
| `DOTNET_MCP_BUDGET_BATCH_DIAGNOSTICS_MS` | 15000 | project_diagnostics when projectId is omitted |
| `DOTNET_MCP_BUDGET_FIXALL_PROJECT_MS` | 15000 | Project Fix all; over budget fails the preview |

Document-scope Fix all applies at most 32 fixes. Project-scope Fix all also stops at a fixed application cap (default 64). Neither application cap is an environment variable.

`0` means the budget is already spent — list tools return a truncated page right away (possibly empty) with `nextCursor`, and project Fix all fails the preview; `symbol_find_references` and `symbol_find_callers` still finish the current document (and `symbol_find_references` finishes the current project on its XAML lane) so the cursor advances, so a page may contain items; negative or unparseable values fall back to the defaults.

`workspace_open` never blocks the MCP request. Clients that do not opt into MCP Tasks should poll `workspace_status`. `workspace_status.watcher` is `ok`, `lost`, or `off`; `lost` means file watching failed and a restart was attempted. Details: `spikes/s3-mcp-long-running/CONCLUSIONS.md`. That note separates the spike `jobId` shape from the product DTO, which has no `jobId`.

## Development / CI

Product and tests target **net10.0**. Fixtures include net8.0 / net9.0 projects (needed for `MsBuildWorkspaceIntegrationTests`).

```bash
dotnet restore DotNetMcp.slnx
dotnet build DotNetMcp.slnx -c Release --no-restore
dotnet test DotNetMcp.slnx -c Release --no-build --verbosity normal
```

There is no `packages.lock.json`, and `RestorePackagesWithLockFile` is not set. Direct PackageReference versions are pinned centrally in `Directory.Packages.props`; the test fixtures and spikes opt out and pin their own. Transitive dependency resolution is not locked, so a later restore can select a different transitive version.

CI: [`.github/workflows/ci.yml`](.github/workflows/ci.yml) (Ubuntu, Windows **and** macOS; product SDK via [`global.json`](global.json) 10.0.x roll-forward, fixture SDKs 8.0.x/9.0.x, a C# whitespace-format check, a known-vulnerable-package check, pack + `McpServer` metadata check, and a smoke test that runs the packed tool with the `dotnet tool exec` form from Quick Start). Third-party actions are pinned to commit SHAs and the workflow token is read-only. A separate [`codeql.yml`](.github/workflows/codeql.yml) workflow runs CodeQL on the C# sources for every pull request and weekly, and Dependabot proposes weekly NuGet updates (grouped by package family) and GitHub Actions updates.

The whitespace rules live in the root [`.editorconfig`](.editorconfig) (LF line endings, 4-space indent for C#); fixtures and spikes are exempt. CI runs this on Ubuntu, and you can run the same command locally on a checkout with LF line endings:

```bash
dotnet format whitespace . --folder --include src/ tests/DotNetMcp.Tests/ benches/ --exclude "**/bin/**" "**/obj/**" --verify-no-changes
```

Each `--include` folder needs its trailing slash. Without it the value is read as a file name, nothing matches, and the check passes without looking at a file.

Product benches: [`docs/perf/benchmark.md`](docs/perf/benchmark.md).

```bash
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite fixtures
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite smoke
```

This repo uses [mattpocock/skills](https://github.com/mattpocock/skills); see `AGENTS.md` and `docs/agents/`.

---

## 中文

面向 Agent 的 .NET MCP 服务器：C# / VB / F# 符号导航、源生成器归因、Avalonia/MAUI XAML、以及受限 Workspace Edit（rename / Diagnostic fix / Code Refactoring）。**不是**通用写文件、shell 或 LSP 代理。

NuGet 包 id 为 **`Skymly.DotNetMcp`**（命令名仍是 `dotnet-mcp`）。必须通过 `--roots`、`--roots=path` 或 `DOTNET_MCP_TRUSTED_ROOTS` 配置受信根，**不再默认使用进程工作目录**。两者同时设置时取并集，而不是互相覆盖。配置缺失或空值时进程以退出码 1 结束，并在 stderr 写一行 JSON，而不是异常堆栈。`workspace_open` 会运行 MSBuild 与 analyzer/源生成器，不要对不受信任的仓库使用。

安装（包尚未上架，请先源码运行或本地 pack）：

```bash
dotnet run --project src/DotNetMcp.Server -- --roots /path/to/repo
```

本地 pack：

```bash
dotnet pack src/DotNetMcp.Server -c Release -o ./artifacts
dotnet tool exec --source ./artifacts --yes Skymly.DotNetMcp -- --roots /path/to/repo
```

NuGet 上架后可用：`dnx Skymly.DotNetMcp --yes -- --roots /path/to/repo`。

Agent 接入：[docs/agent-guidance.md](docs/agent-guidance.md) 提供可直接粘贴到消费方 `AGENTS.md` / `CLAUDE.md` 的片段；服务器 initialize 结果携带相同的 `instructions`，并附带 `trace_symbol` / `safe_rename` 两个 prompt。
