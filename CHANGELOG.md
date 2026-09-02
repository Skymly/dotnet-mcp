# Changelog

All notable product changes are recorded here. Version numbers match `src/DotNetMcp.Server/DotNetMcp.Server.csproj` and git tags `vMAJOR.MINOR.PATCH`.

## 4.0.0 - 2026-09-02

First tagged 4.0 line. Shipping identity is **`Skymly.DotNetMcp`** (`dotnet-mcp` remains the tool command). The previous NuGet id `dotnet-mcp` is occupied by an unrelated unlisted package.

### Added

- Code Refactoring tools: `symbol_list_refactorings` / `symbol_preview_refactoring` / `symbol_apply_refactoring`
- `diagnostics_preview_fix(scope=project)` (project Fix all)
- `McpServer` package type and embedded `.mcp/server.json`
- Windows CI job; pack step verifies package id, `McpServer` type, and `server.json`
- MSBuild fixture sampling for VB resolve, F# attribution, Avalonia `xaml_resolve_class`, and source-generator attribution
- `CHANGELOG.md` and an English Quick Start

### Changed

- Trusted roots are mandatory (`--roots` or `DOTNET_MCP_TRUSTED_ROOTS`). Process CWD is no longer an implicit root
- Path canonicalization is fail-closed on unresolvable reparse points; loaded graphs and apply paths are re-checked
- `ILanguageAdapter` now owns `GetAttributionAsync`; MCP tools no longer take `RoslynLanguageAdapter` directly
- `RoslynLanguageAdapter` split into query / finders / attribution / diagnostics / rename partials

### Security

- See ADR-0004 Amendment 4

## 3.0.0

Diagnostic fix tools (`diagnostics_list_fixes` / `diagnostics_preview_fix` / `diagnostics_apply_fix`) and F# rename.

## 2.0.0

Restricted Workspace Edit (C# / VB rename preview-apply), VB.NET read-side, MAUI XAML, F# / COM / `dynamic` read-side.

## 1.0.0

P0 C# read-side: stdio host, trusted roots, MSBuild workspace load, symbol navigation, project diagnostics, source-generator list/source/diagnostics/attribution. GitHub release tag `v1.0.0`.
