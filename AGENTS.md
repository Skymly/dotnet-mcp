# dotnet-mcp

MCP server (`Skymly.DotNetMcp`, CLI `dnmcp`) providing compiler-accurate C#/VB (Roslyn) and F# (FCS) symbol navigation, source-generator attribution, Avalonia/MAUI XAML queries, and a restricted preview/apply Workspace Edit surface. Not a generic file writer, shell, or LSP proxy.

## Build & Verify

```powershell
dotnet build DotNetMcp.slnx -c Release
dotnet test DotNetMcp.slnx -c Release --no-build --verbosity normal
```

- Requires **.NET 10** SDK (`global.json` pins 10.0.100, `rollForward: latestFeature`) plus **.NET 8 / 9** SDKs for multi-TFM test fixtures.
- CI (`ci.yml`) also verifies:
  - Whitespace formatting on Linux (`dotnet format whitespace . --folder --include src/ tests/DotNetMcp.Tests/ benches/ --exclude "**/bin/**" "**/obj/**" --verify-no-changes`)
  - Version parity between `src/DotNetMcp.Server/DotNetMcp.Server.csproj`, `src/DotNetMcp.Server/.mcp/server.json`, and `CHANGELOG.md`
  - Known vulnerabilities (`dotnet list src/DotNetMcp.Server/DotNetMcp.Server.csproj package --vulnerable --include-transitive --format json`)
  - Packed tool verification (`./.github/scripts/VerifyMcpPackage.ps1` + `PackedToolSmokeTests` against `DOTNET_MCP_PACKED_TOOL_SOURCE`)

## Architecture & Invariants

- **Security & path policy** ([ADR-0004](docs/adr/0004-security-and-path-policy.md)): Trusted roots are explicit and mandatory (the process working directory is never an implicit trusted root). Toolchain roots (NuGet cache, SDK) are read-only; all writes stay inside trusted roots. Note `workspace_open` runs MSBuild evaluation and project analyzers/generators.
- **Restricted Workspace Edit** ([ADR-0005](docs/adr/0005-workspace-edit-module-beside-host.md)): Every write flows through explicit preview -> apply with matching edit kind, `Epoch`, and TTL. Never add generic write, patch, or shell tools. Generated declarations (`Origin = SourceGenerator`) are read-only except supported `x:Name` XAML renames.
- **Contract & vocabulary**: Tool names are locked by snapshot tests. Follow [`CONTEXT.md`](CONTEXT.md) strictly (`WorkspaceSession`, `Epoch`, `Trusted root`, `Toolchain root`, `Soft budget`, `Origin`, `Attribution`, `Generation trigger`, `Drift`, `Workspace Edit`).
- **Language boundaries**: F# runs via FCS beside Roslyn (no F# generator attribution, fix, or refactoring, and no cross-language F# -> C#/VB renames). XAML supports Avalonia `.axaml` and MAUI `.xaml` only (not WPF/WinUI).

## Docs & Agent Config

- Domain glossary: [`CONTEXT.md`](CONTEXT.md)
- Maintainer docs: [`docs/adr/`](docs/adr/), [`docs/agent-guidance.md`](docs/agent-guidance.md), [`CHANGELOG.md`](CHANGELOG.md)
- Issue routing & triage labels: [`docs/agents/`](docs/agents/)

## Conventions

- Commit messages, issues, and PR text: **English**.
- Do not commit, push, bump versions, or release unless explicitly requested.
