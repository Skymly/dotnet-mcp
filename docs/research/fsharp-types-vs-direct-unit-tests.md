# F# 类型与现有直接单测对照

Snapshot: `origin/main` @ `b61596a4959ea45718971b05df26f4bdd3c0d7e6` (`Add DynamicInvocationQueryService unit tests (#185)`).

Scope: every `public` / `internal` type declared in `src/DotNetMcp.FSharp/*.cs`. Private nested types are listed only under Exclusions.

`DotNetMcp.FSharp.csproj` has `InternalsVisibleTo` for `DotNetMcp.Tests`.

`FSharpWorkspaceSnapshot` / `FSharpProjectSnapshot` / `FSharpDocumentSnapshot` live in `src/DotNetMcp.Core/FSharpWorkspaceSnapshot.cs` and are **not** in this inventory (see [Core 类型与现有直接单测对照](https://github.com/Skymly/dotnet-mcp/issues/157)). This file only records how F# code **consumes** `session.FSharpSnapshot`.

## Criterion

A **direct** test is a test method that constructs or names the F# type **without** `InProcessMcpFixture` and **without** MCP `CallToolAsync` / `tools/call`.

Counted as direct:

- `new TypeName(...)` (including target-typed `new`)
- static / factory members on the type (`TypeName.CompileLibrary`, …)
- `Assert.IsType<TypeName>(...)` in such a method

Not counted as direct:

- MCP SeamTests that only go through `InProcessMcpFixture` / `CallToolAsync`
- source-text scans that `File.ReadAllText` a `.cs` file
- constructing the type inside Server (`WorkspaceSession`) or as a constructor argument to `InProcessMcpFixture`
- `FakeSolutionLoader` calling a static helper (test infrastructure, not a test method)

Some `*SeamTests.cs` files are mixed: they contain MCP tests **and** one or more fixture-free methods that `new` F# types. Those files are listed for the types the fixture-free methods construct; a dagger (†) marks mixed files.

## Direct-test files (whole file has no `InProcessMcpFixture`)

None for `DotNetMcp.FSharp` types.

Related files that are fixture-free MCP **but do not `new` an F# type**:

| File | What it actually does |
| --- | --- |
| `tests/DotNetMcp.Tests/FsharpBesideWorkspaceSessionSeamTests.cs` | `File.ReadAllText` on `src/DotNetMcp.FSharp/*.cs` (asserts no `session.Solution` / `GetCompilationAsync`); reflection on Core `FSharpWorkspaceSnapshot` |
| `tests/DotNetMcp.Tests/FsharpSnapshotDiskSeamTests.cs` | `new AdhocWorkspace` + disk `fixtures/MixedCsharpVb/FsLib/FsLib.fsproj`; `new WorkspaceSession` (Server); reads `session.FSharpSnapshot` (Core) |

## Mixed files (†): MCP + at least one fixture-free `new`

| File | Fixture-free method | F# `new` / call |
| --- | --- | --- |
| `tests/DotNetMcp.Tests/LanguageAdapterSeamTests.cs` | `wrong_language_handle_does_not_silently_use_the_other_adapter` | `new FSharpSymbolQueryService()`; session is `new WorkspaceSession(FakeSolutionLoader.CreateFsharpSymbolsLoaded(root), epoch: 1)` (Server) |
| `tests/DotNetMcp.Tests/FsharpReadNoDiskWriteSeamTests.cs` | `fsharp_injected_soft_budget_truncates_find_refs_and_callers_as_continuable` | `new FSharpSymbolQueryService(new SoftBudgetOptions { FindRefsScoped = TimeSpan.Zero, FindRefsEntireSolution = TimeSpan.Zero })`; dispatched via `new LanguageAdapters([roslyn, fsharp])`; same Server `WorkspaceSession` + `CreateFsharpSymbolsLoaded` |

There is no `FSharpSymbolQueryServiceTests.cs`.

## Inventory (1 public / internal type)

| Type | Visibility | Source | Role | Direct tests |
| --- | --- | --- | --- | --- |
| `FSharpSymbolQueryService` | public sealed partial class, `ILanguageAdapter` | `src/DotNetMcp.FSharp/FSharpSymbolQueryService.cs` (+ `.Analysis.cs` / `.Diagnostics.cs` / `.Rename.cs`) | adapter | 仅 SeamTests† — mixed fixture-free `new` in `LanguageAdapterSeamTests.cs` and `FsharpReadNoDiskWriteSeamTests.cs`. MCP: `FsharpSymbolSeamTests.cs`, `FsharpAnalysisSeamTests.cs`, `FsharpRenameSeamTests.cs`, `FsharpReadNoDiskWriteSeamTests.cs`, `LanguageAdapterSeamTests.cs`, `WorkspaceListFsharpProjectsSeamTests.cs` |

## Exclusions (private nested)

| Type | Source | Notes |
| --- | --- | --- |
| `FSharpCatalogItem` | `FSharpSymbolQueryService.cs` (~line 687) | `private sealed record`; catalog/handle resolution helper. Not independently `new`-able from tests without InternalsVisibleTo + nested access. |

No other `class` / `record` / `struct` / `interface` / `enum` in `src/DotNetMcp.FSharp`.

## Consumption of Core snapshot (not an F# type)

`FSharpSymbolQueryService` reads `session.FSharpSnapshot` only (`FSharpProjects`, `FindProject`). It does not read `session.Solution` or `GetCompilationAsync` (`FsharpBesideWorkspaceSessionSeamTests` source scan).

`WorkspaceSession` (Server) fills the snapshot in `CaptureFSharp(loaded.Solution, epoch)` (`src/DotNetMcp.Server/WorkspaceSession.cs`).

## Appendix 1 — `FSharpSymbolQueryService` public 入口

Implements `DotNetMcp.Core.ILanguageAdapter` (`src/DotNetMcp.Core/ILanguageAdapter.cs`) plus one extra static helper.

| Member | File:line | Signature (abbrev.) |
| --- | --- | --- |
| ctor | `FSharpSymbolQueryService.cs:32` | `FSharpSymbolQueryService(SoftBudgetOptions? softBudgets = null)` — creates `FSharpChecker` with custom `DocumentSource` over in-memory snapshot texts |
| `OwnsLanguage` | `FSharpSymbolQueryService.cs:21` | `bool OwnsLanguage(string languageToken)` — true iff `LanguageAdapters.FSharpLanguage` |
| `OwnsProject` | `FSharpSymbolQueryService.cs:24` | `bool OwnsProject(Project project)` — `LanguageNames.FSharp` or `.fsproj` path |
| `SupportsCodeRefactoring` | `FSharpSymbolQueryService.cs:28` | `false` |
| `SupportsDiagnosticFix` | `FSharpSymbolQueryService.cs:30` | `false` |
| `ResolveByNameAsync` | `FSharpSymbolQueryService.cs:53` | `(session, name, projectId?, ct)` → `SymbolResolveSuccess` / `SymbolNotFoundError` / `SymbolAmbiguousError` |
| `GetSummaryAsync` | `FSharpSymbolQueryService.cs:102` | `(session, handle, ct)` |
| `GetDefinitionAsync` | `FSharpSymbolQueryService.cs:112` | `(session, handle, ct)` — may `DefinitionNotFoundError` |
| `GetMembersAsync` | `FSharpSymbolQueryService.cs:134` | `(session, handle, limit?, cursor?, ct)` — pages via `SoftBudgetPage.Page` |
| `FindReferencesAsync` | `FSharpSymbolQueryService.Analysis.cs:12` | `(session, handle, entireSolution = false, limit?, cursor?, softBudget?, ct)` |
| `FindImplementationsAsync` | `FSharpSymbolQueryService.Analysis.cs:67` | `(session, handle, limit?, cursor?, ct)` |
| `GetTypeHierarchyAsync` | `FSharpSymbolQueryService.Analysis.cs:106` | `(session, handle, limit?, cursor?, ct)` |
| `FindCallersAsync` | `FSharpSymbolQueryService.Analysis.cs:155` | `(session, handle, limit?, cursor?, softBudget?, ct)` |
| `GetProjectDiagnosticsAsync` | `FSharpSymbolQueryService.Diagnostics.cs:8` | `(session, projectId, limit?, cursor?, softBudget?, ct)` — `session.FSharpSnapshot.FindProject` |
| `BuildRenamePreviewAsync` | `FSharpSymbolQueryService.Rename.cs:9` | `(session, handle, newName, ct)` → `RenamePreviewDraft` / `InvalidRenameNameError` / handle errors |
| `CompileLibrary` | `FSharpSymbolQueryService.cs:222` | `static string CompileLibrary(string outputDll, IReadOnlyList<string> sourceFiles)` — runs `FSharpChecker.Compile`, writes DLL to disk |

No F# type defines error DTOs; errors are Core `SymbolQueryError` records.

## Appendix 2 — F# SeamTests / `FakeSolutionLoader` 如何构造 F# 输入

### `FakeSolutionLoader` (`tests/DotNetMcp.Tests/FakeSolutionLoader.cs`)

All F# factories use `new AdhocWorkspace()` + `ProjectInfo` with `LanguageNames.FSharp`. Return `LoadedSolution`. None return `FSharpWorkspaceSnapshot` directly.

| Method | Line | Disk `.fs` / `.fsproj` | `CompileLibrary` | Notes |
| --- | --- | --- | --- | --- |
| `ImmediateWithFsharpDiagnostics` | 177 | default `C:\fake\BrokenFs.fsproj`; source **not** written to the `Broken.fs` path (path is only `Document.FilePath`) | no | `CreateFsharpDiagnosticsLoaded` |
| `CreateFsharpDiagnosticsLoaded` | 181 | `Path.GetTempPath()/dotnet-mcp-broken-fs/Broken.fs` as FilePath; Adhoc document text in memory | no | type-error F# source |
| `ImmediateWithFsharpSymbols` | 246 | `root/FsLib/Widget.fs`, `Uses.fs`, `FsLib.fsproj`; also C# `CsLib` | **yes** | `CreateFsharpSymbolsLoaded` |
| `CreateFsharpSymbolsLoaded` | 249 | `File.WriteAllText` Widget/Uses/Caller; `FsLib.fsproj` / `CsLib.csproj` FilePaths | `FSharpSymbolQueryService.CompileLibrary(fsDir/FsLib.dll, [widgetPath, usesPath])` then C# `MetadataReference`; catch swallows compile failure | mixed F#+C# |
| `ImmediateWithFsharpCollidingFileNames` | 344 | `root/A/Widget.fs`, `root/B/Widget.fs`, `CollideFs.fsproj` | no | `CreateFsharpCollidingFileNamesLoaded` |
| `CreateFsharpCollidingFileNamesLoaded` | 347 | writes both Widget.fs files | no | same filename different dirs |
| `ImmediateWithFsharpAndCSharp` | 389 | default fake `.csproj` / `.fsproj` paths | no | empty F# placeholder |
| `CreateFsharpAndCSharpLoaded` | 394 | via `AddEmptyFsharpProject` | no | list-projects fixture |
| `AddEmptyFsharpProject` (private) | 1877 | in-memory `Placeholder.fs` (`module Placeholder`); no disk write | no | |

`CompileLibrary` has **no** test method that calls it. The only caller in `tests/` is `CreateFsharpSymbolsLoaded` (best-effort, exception swallowed).

### How tests obtain `FSharpWorkspaceSnapshot`

| Site | How |
| --- | --- |
| MCP F# SeamTests | `InProcessMcpFixture` + `FakeSolutionLoader.ImmediateWithFsharp*` → Server `WorkspaceSession` captures snapshot on load |
| Mixed fixture-free methods above | `new WorkspaceSession(loaded, epoch: 1)` where `loaded` is `CreateFsharpSymbolsLoaded` — snapshot via Server `CaptureFSharp` |
| `FsharpSnapshotDiskSeamTests` | Adhoc project with real `fixtures/.../FsLib.fsproj`; `new WorkspaceSession`; asserts snapshot document paths include `Widget.fs` |
| Core unit tests (out of F# scope) | several `*Tests.cs` set `FSharpSnapshot = new FSharpWorkspaceSnapshot(epoch, [])` on a fake `IWorkspaceSession` — empty snapshot, not an F# SUT test |

### MCP F# SeamTests (no direct `new FSharpSymbolQueryService`)

| File | Loader |
| --- | --- |
| `FsharpSymbolSeamTests.cs` | `ImmediateWithFsharpSymbols` |
| `FsharpAnalysisSeamTests.cs` | `ImmediateWithFsharpSymbols` |
| `FsharpRenameSeamTests.cs` | `ImmediateWithFsharpSymbols` |
| `FsharpReadNoDiskWriteSeamTests.cs` | `ImmediateWithFsharpSymbols` / `ImmediateWithFsharpCollidingFileNames` (+ mixed method above) |
| `WorkspaceListFsharpProjectsSeamTests.cs` | `ImmediateWithFsharpAndCSharp` |
| `LanguageAdapterSeamTests.cs` | `ImmediateWithFsharpSymbols` / `CreateFsharpSymbolsLoaded` |

No test constructs `FSharpWorkspaceSnapshot` by hand and passes it into `new FSharpSymbolQueryService()` without going through Server `WorkspaceSession`.