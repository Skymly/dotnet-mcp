# Core 类型与现有直接单测对照

Snapshot: `origin/main` @ `cbb1b64` (`Share CodeAction-to-handwritten-slices on CodeActionDocuments (#154) (#155)`).

Scope: every `public` / `internal` type declared in `src/DotNetMcp.Core/*.cs`. Private nested types are listed only under Exclusions.

`DotNetMcp.Core.csproj` has `InternalsVisibleTo` for `DotNetMcp.Tests` and `DotNetMcp.Xaml`.

## Criterion

A **direct** test is a test method that constructs or names the Core type **without** `InProcessMcpFixture` and **without** MCP `CallToolAsync` / `tools/call`.

Counted as direct:

- `new TypeName(...)` (including target-typed `new`)
- static / factory members on the type (`TypeName.Method`, `TypeName.Create`, `TypeName.FromEnvironment`, `TypeName.TryParse`, `TypeName.Encode` / `TryDecode`)
- `Assert.IsType<TypeName>(...)` in such a method
- implementing an interface type in such a method

Not counted as direct:

- MCP SeamTests that only go through `InProcessMcpFixture` / `CallToolAsync`
- source-text scans that `File.ReadAllText` a `.cs` file
- constructing the type inside Server (`WorkspaceSession`, `WorkspaceHost`, DI) or as a constructor argument to `InProcessMcpFixture`

Some `*SeamTests.cs` files are mixed: they contain MCP tests **and** one or more fixture-free methods that `new` Core types. Those files are listed for the types the fixture-free methods construct; a dagger (†) marks mixed files.

## Direct-test files (whole file has no `InProcessMcpFixture`)

| File | What it constructs / calls |
| --- | --- |
| `tests/DotNetMcp.Tests/CompilationLruTests.cs` | `new CompilationLru` |
| `tests/DotNetMcp.Tests/GeneratorQueryServiceTests.cs` | `new GeneratorQueryService`, `new GeneratorRunCache` |
| `tests/DotNetMcp.Tests/HandwrittenDocumentDiffTests.cs` | `HandwrittenDocumentDiff.FromDocumentPairsAsync` / `FromSolutionsAsync`; `new RenameDocumentSlice`; `RoslynLanguageAdapter.DefaultRenameOptions` |
| `tests/DotNetMcp.Tests/SoftBudgetOptionsTests.cs` | `SoftBudgetOptions.FromEnvironment`, `SoftBudgetOptions.Default` |
| `tests/DotNetMcp.Tests/SoftBudgetPageTests.cs` | `SoftBudgetPage.Page` / `PageFindRefs` / `PageGenerated`; `MemberPageCursor.TryDecode`; `FindRefsPageCursor.TryDecode`; `GeneratedSourcesPageCursor.Encode` / `TryDecode`; `Assert.IsType<StaleCursorError>` |
| `tests/DotNetMcp.Tests/TypeMemberLookupSeamTests.cs` | `new RoslynLanguageAdapter(new GeneratorQueryService())`; `SymbolHandle.Create` / `TryParse`; `LanguageAdapters.CSharpLanguage`; `Assert.IsType` on `MemberNotFoundError` / `InvalidSymbolHandleError` / `SymbolNotFoundError`; `SymbolQueryErrorCodes` |
| `tests/DotNetMcp.Tests/WorkspaceSessionCompilationSeamTests.cs` | `new GeneratorRunCache` (also `new WorkspaceSession` from Server, not Core) |
| `tests/DotNetMcp.Tests/FsharpSnapshotDiskSeamTests.cs` | no `new` of Core snapshot types; reads `WorkspaceSession.FSharpSnapshot` |
| `tests/DotNetMcp.Tests/CodeActionDocumentsSeamTests.cs` | source-text scan of `CodeActionDocuments.cs` (not a runtime `new` / call) |
| `tests/DotNetMcp.Tests/FsharpBesideWorkspaceSessionSeamTests.cs` | source/reflection scan of `FSharpWorkspaceSnapshot` (not `new`) |

## Mixed files (†): MCP + at least one fixture-free `new`

| File | Fixture-free method | Core `new` / call |
| --- | --- | --- |
| `tests/DotNetMcp.Tests/LanguageAdapterSeamTests.cs` | `language_adapters_select_once_so_a_third_adapter_is_reached_without_copied_ifs`; `wrong_language_handle_does_not_silently_use_the_other_adapter` | `new LanguageAdapters`, `new RoslynLanguageAdapter`, `new GeneratorQueryService`, `new SymbolResolveSuccess`, `new SymbolSummary`, `SymbolHandle.Create`, `Assert.IsType<InvalidSymbolHandleError>`; nested `FakeLanguageAdapter : ILanguageAdapter` |
| `tests/DotNetMcp.Tests/ProjectDiagnosticsSeamTests.cs` | `GetProjectDiagnosticsAsync_soft_budget_zero_truncates_with_continuation_message` | `new DiagnosticQueryService` |
| `tests/DotNetMcp.Tests/SymbolFindCallersSeamTests.cs` | `FindCallersAsync_soft_budget_zero_truncates_with_continuation_message` | `new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())])` |
| `tests/DotNetMcp.Tests/SymbolFindReferencesSeamTests.cs` | `FindReferencesAsync_soft_budget_zero_truncates_with_continuation_message` | `new LanguageAdapters([new RoslynLanguageAdapter(new GeneratorQueryService())])` |
| `tests/DotNetMcp.Tests/FsharpReadNoDiskWriteSeamTests.cs` | `fsharp_injected_soft_budget_truncates_find_refs_and_callers_as_continuable` | `new SoftBudgetOptions`, `new LanguageAdapters`, `new RoslynLanguageAdapter`, `new GeneratorQueryService` |

## Inventory (92 public / internal types)

| Type | Visibility | Source | Role | Direct tests |
| --- | --- | --- | --- | --- |
| `CodeActionDocuments` | public static class | `src/DotNetMcp.Core/CodeActionDocuments.cs` | other | 仅 SeamTests (`tests/DotNetMcp.Tests/CodeActionDocumentsSeamTests.cs` reads the source file; no runtime call) |
| `CodeRefactoringItem` | public record | `src/DotNetMcp.Core/CodeRefactoringModels.cs` | DTO | 仅 SeamTests |
| `CodeRefactoringListSuccess` | public record | `src/DotNetMcp.Core/CodeRefactoringModels.cs` | DTO | 仅 SeamTests |
| `CodeRefactoringPreviewDraft` | public record | `src/DotNetMcp.Core/CodeRefactoringModels.cs` | DTO | 仅 SeamTests |
| `RefactoringLanguageNotSupportedError` | public record | `src/DotNetMcp.Core/CodeRefactoringModels.cs` | DTO | 仅 SeamTests |
| `RefactoringIndexOutOfRangeError` | public record | `src/DotNetMcp.Core/CodeRefactoringModels.cs` | DTO | 仅 SeamTests |
| `GeneratedSymbolRefactoringRefusedError` | public record | `src/DotNetMcp.Core/CodeRefactoringModels.cs` | DTO | 仅 SeamTests |
| `GeneratedDocumentRefactoringRefusedError` | public record | `src/DotNetMcp.Core/CodeRefactoringModels.cs` | DTO | 仅 SeamTests |
| `RefactoringApplyFailedError` | public record | `src/DotNetMcp.Core/CodeRefactoringModels.cs` | DTO | 仅 SeamTests |
| `CodeRefactoringService` | public class | `src/DotNetMcp.Core/CodeRefactoringService.cs` | service | 仅 SeamTests (no `new`; type name appears as a filename string in `CodeActionDocumentsSeamTests.cs` / `LanguageAdapterSeamTests.cs` source scans; MCP: `CodeRefactoringSeamTests.cs`, `CodeRefactoringExitGateSeamTests.cs`) |
| `CompilationLru` | public class | `src/DotNetMcp.Core/CompilationLru.cs` | cache | `tests/DotNetMcp.Tests/CompilationLruTests.cs` (`new CompilationLru`) |
| `DiagnosticFixItem` | public record | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | DTO | 仅 SeamTests |
| `DiagnosticFixListSuccess` | public record | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | DTO | 仅 SeamTests |
| `DiagnosticFixPreviewDraft` | public record | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | DTO | 仅 SeamTests |
| `DiagnosticNotFoundError` | public record | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | DTO | 仅 SeamTests |
| `DiagnosticAmbiguousError` | public record | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | DTO | 仅 SeamTests |
| `FixLanguageNotSupportedError` | public record | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | DTO | 仅 SeamTests |
| `FixIndexOutOfRangeError` | public record | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | DTO | 仅 SeamTests |
| `GeneratedDocumentFixRefusedError` | public record | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | DTO | 仅 SeamTests |
| `FixApplyFailedError` | public record | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | DTO | 仅 SeamTests |
| `FixAllUnavailableError` | public record | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | DTO | 仅 SeamTests |
| `FixAllBudgetExceededError` | public record | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | DTO | 仅 SeamTests |
| `DiagnosticFixScopes` | public static class | `src/DotNetMcp.Core/DiagnosticFixModels.cs` | other | 无 |
| `DiagnosticFixService` | public class | `src/DotNetMcp.Core/DiagnosticFixService.cs` | service | 仅 SeamTests (no `new`; filename string in source scans; MCP: `DiagnosticFixSeamTests.cs`, `DiagnosticFixExitGateSeamTests.cs`, `ProjectFixAllSeamTests.cs`) |
| `DiagnosticItem` | public record | `src/DotNetMcp.Core/DiagnosticModels.cs` | DTO | 仅 SeamTests |
| `ProjectNotFoundError` | public record | `src/DotNetMcp.Core/DiagnosticModels.cs` | DTO | 仅 SeamTests |
| `CompilationUnavailableError` | public record | `src/DotNetMcp.Core/DiagnosticModels.cs` | DTO | 仅 SeamTests |
| `GeneratorNotFoundError` | public record | `src/DotNetMcp.Core/DiagnosticModels.cs` | DTO | 仅 SeamTests |
| `SoftBudgetExceededError` | public record | `src/DotNetMcp.Core/DiagnosticModels.cs` | DTO | 仅 SeamTests |
| `DiagnosticQueryService` | public class | `src/DotNetMcp.Core/DiagnosticQueryService.cs` | service | `tests/DotNetMcp.Tests/ProjectDiagnosticsSeamTests.cs`† (`new DiagnosticQueryService`) |
| `DynamicInvocationItem` | public record | `src/DotNetMcp.Core/DynamicInvocationModels.cs` | DTO | 仅 SeamTests |
| `DynamicInvocationQueryService` | public class | `src/DotNetMcp.Core/DynamicInvocationQueryService.cs` | service | 仅 SeamTests (no `new`; filename string in `SoftBudgetPageTests.cs` source scan; MCP: `DynamicInvocationSeamTests.cs`) |
| `FindHitCache` | public class | `src/DotNetMcp.Core/FindHitCache.cs` | cache | 无 (`new FindHitCache` is in `src/DotNetMcp.Server/WorkspaceSession.cs`, not tests) |
| `IWorkspaceSessionCaches` | public interface | `src/DotNetMcp.Core/FindHitCache.cs` | cache | 无 |
| `FindRefsPageCursor` | public static class | `src/DotNetMcp.Core/FindRefsPageCursor.cs` | cursor | `tests/DotNetMcp.Tests/SoftBudgetPageTests.cs` (`TryDecode`) |
| `FindRefsScopeKind` | public enum | `src/DotNetMcp.Core/FindRefsScopes.cs` | other | 无 |
| `FindRefsScopes` | public static class | `src/DotNetMcp.Core/FindRefsScopes.cs` | other | 无 |
| `FSharpWorkspaceSnapshot` | public class | `src/DotNetMcp.Core/FSharpWorkspaceSnapshot.cs` | other | 无 (`new` is in `src/DotNetMcp.Server/WorkspaceSession.cs`). `tests/DotNetMcp.Tests/FsharpBesideWorkspaceSessionSeamTests.cs` is source/reflection only; `tests/DotNetMcp.Tests/FsharpSnapshotDiskSeamTests.cs` reads `session.FSharpSnapshot` |
| `FSharpProjectSnapshot` | public class | `src/DotNetMcp.Core/FSharpWorkspaceSnapshot.cs` | other | 无 (`new` is in `src/DotNetMcp.Server/WorkspaceSession.cs`) |
| `FSharpDocumentSnapshot` | public class | `src/DotNetMcp.Core/FSharpWorkspaceSnapshot.cs` | other | 无 (`new` is in `src/DotNetMcp.Server/WorkspaceSession.cs`) |
| `GeneratedSourcesPageCursor` | public static class | `src/DotNetMcp.Core/GeneratedSourcesPageCursor.cs` | cursor | `tests/DotNetMcp.Tests/SoftBudgetPageTests.cs` (`Encode`, `TryDecode`) |
| `GeneratorDriverRunner` | public static class | `src/DotNetMcp.Core/GeneratorDriverRunner.cs` | other | 无 |
| `GeneratorIdentity` | public record | `src/DotNetMcp.Core/GeneratorModels.cs` | DTO | 无 (fields reached via `page.Identity` in `GeneratorQueryServiceTests.cs` without naming this type) |
| `GeneratedSourceItem` | public record | `src/DotNetMcp.Core/GeneratorModels.cs` | DTO | 无 (fields reached via `page.Items` in `GeneratorQueryServiceTests.cs` without naming this type) |
| `GeneratorDiagnosticItem` | public record | `src/DotNetMcp.Core/GeneratorModels.cs` | DTO | 无 |
| `GeneratorDiagnosticsPage` | public record | `src/DotNetMcp.Core/GeneratorModels.cs` | DTO | 无 |
| `GeneratorRunSources` | public record | `src/DotNetMcp.Core/GeneratorModels.cs` | DTO | 无 |
| `GeneratedSourceMatch` | public record | `src/DotNetMcp.Core/GeneratorModels.cs` | DTO | 无 |
| `DriverRunSnapshot` | public record | `src/DotNetMcp.Core/GeneratorModels.cs` | DTO | 无 |
| `SymbolAttribution` | public record | `src/DotNetMcp.Core/GeneratorModels.cs` | DTO | 仅 SeamTests |
| `SymbolAttributionSuccess` | public record | `src/DotNetMcp.Core/GeneratorModels.cs` | DTO | 仅 SeamTests |
| `GeneratorQueryService` | public class | `src/DotNetMcp.Core/GeneratorQueryService.cs` | service | `tests/DotNetMcp.Tests/GeneratorQueryServiceTests.cs` (`new`); `tests/DotNetMcp.Tests/TypeMemberLookupSeamTests.cs` (`new`); `tests/DotNetMcp.Tests/LanguageAdapterSeamTests.cs`† (`new`); `tests/DotNetMcp.Tests/SymbolFindCallersSeamTests.cs`† (`new`); `tests/DotNetMcp.Tests/SymbolFindReferencesSeamTests.cs`† (`new`); `tests/DotNetMcp.Tests/FsharpReadNoDiskWriteSeamTests.cs`† (`new`) |
| `GeneratorRunCache` | public class | `src/DotNetMcp.Core/GeneratorRunCache.cs` | cache | `tests/DotNetMcp.Tests/GeneratorQueryServiceTests.cs` (`new`); `tests/DotNetMcp.Tests/WorkspaceSessionCompilationSeamTests.cs` (`new`) |
| `HandwrittenDocumentDiff` | public static class | `src/DotNetMcp.Core/HandwrittenDocumentDiff.cs` | other | `tests/DotNetMcp.Tests/HandwrittenDocumentDiffTests.cs` (`FromDocumentPairsAsync`, `FromSolutionsAsync`) |
| `ILanguageAdapter` | public interface | `src/DotNetMcp.Core/ILanguageAdapter.cs` | adapter | `tests/DotNetMcp.Tests/LanguageAdapterSeamTests.cs`† (nested `FakeLanguageAdapter` implements it; cannot `new` the interface) |
| `IWorkspaceSession` | public interface | `src/DotNetMcp.Core/IWorkspaceSession.cs` | other | `tests/DotNetMcp.Tests/TypeMemberLookupSeamTests.cs` (parameter type; cannot `new`; production implementer is Server `WorkspaceSession`) |
| `LanguageAdapters` | public class | `src/DotNetMcp.Core/LanguageAdapters.cs` | adapter | `tests/DotNetMcp.Tests/LanguageAdapterSeamTests.cs`† (`new`); `tests/DotNetMcp.Tests/SymbolFindCallersSeamTests.cs`† (`new`); `tests/DotNetMcp.Tests/SymbolFindReferencesSeamTests.cs`† (`new`); `tests/DotNetMcp.Tests/FsharpReadNoDiskWriteSeamTests.cs`† (`new`); `tests/DotNetMcp.Tests/TypeMemberLookupSeamTests.cs` (`LanguageAdapters.CSharpLanguage` only) |
| `MemberPageCursor` | public static class | `src/DotNetMcp.Core/MemberPageCursor.cs` | cursor | `tests/DotNetMcp.Tests/SoftBudgetPageTests.cs` (`TryDecode`) |
| `RenameDocumentSlice` | public record | `src/DotNetMcp.Core/RenameModels.cs` | DTO | `tests/DotNetMcp.Tests/HandwrittenDocumentDiffTests.cs` (`new RenameDocumentSlice`) |
| `RenamePreviewDraft` | public record | `src/DotNetMcp.Core/RenameModels.cs` | DTO | 仅 SeamTests |
| `RoslynLanguageAdapter` | public class | `src/DotNetMcp.Core/RoslynLanguageAdapter.cs` | adapter | `tests/DotNetMcp.Tests/TypeMemberLookupSeamTests.cs` (`new`); `tests/DotNetMcp.Tests/LanguageAdapterSeamTests.cs`† (`new`); `tests/DotNetMcp.Tests/SymbolFindCallersSeamTests.cs`† (`new`); `tests/DotNetMcp.Tests/SymbolFindReferencesSeamTests.cs`† (`new`); `tests/DotNetMcp.Tests/FsharpReadNoDiskWriteSeamTests.cs`† (`new`); `tests/DotNetMcp.Tests/HandwrittenDocumentDiffTests.cs` (`DefaultRenameOptions`) |
| `SoftBudgetOptions` | public class | `src/DotNetMcp.Core/SoftBudgetOptions.cs` | options | `tests/DotNetMcp.Tests/SoftBudgetOptionsTests.cs` (`FromEnvironment`, `Default`); `tests/DotNetMcp.Tests/FsharpReadNoDiskWriteSeamTests.cs`† (`new SoftBudgetOptions`). `new SoftBudgetOptions` also appears as an `InProcessMcpFixture` argument in `ProjectFixAllSeamTests.cs` and `XamlDiagnosticsSeamTests.cs` (not direct) |
| `SoftBudgetPage` | public static class | `src/DotNetMcp.Core/SoftBudgetPage.cs` | other | `tests/DotNetMcp.Tests/SoftBudgetPageTests.cs` (`Page`, `PageFindRefs`, `PageGenerated`) |
| `SymbolDisplayFormats` | internal static class | `src/DotNetMcp.Core/SymbolDisplayFormats.cs` | other | 无 |
| `SymbolHandle` | public record | `src/DotNetMcp.Core/SymbolHandle.cs` | DTO | `tests/DotNetMcp.Tests/TypeMemberLookupSeamTests.cs` (`Create`, `TryParse`); `tests/DotNetMcp.Tests/LanguageAdapterSeamTests.cs`† (`Create`) |
| `InteropKinds` | public static class | `src/DotNetMcp.Core/SymbolModels.cs` | other | 无 |
| `SymbolSummary` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | `tests/DotNetMcp.Tests/LanguageAdapterSeamTests.cs`† (`new SymbolSummary` inside `FakeLanguageAdapter.GetSummaryAsync`) |
| `SymbolResolveSuccess` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | `tests/DotNetMcp.Tests/LanguageAdapterSeamTests.cs`† (`new SymbolResolveSuccess`) |
| `SymbolLocation` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `SymbolDefinitionSuccess` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `MemberListItem` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `ImplementationItem` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `HierarchyRelationKind` | public static class | `src/DotNetMcp.Core/SymbolModels.cs` | other | 无 |
| `HierarchyItem` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `ReferenceLocationItem` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `CallerLocationItem` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `ReferenceLocationKind` | public static class | `src/DotNetMcp.Core/SymbolModels.cs` | other | 无 |
| `PagedResult<T>` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 无 (return shape of `SoftBudgetPage.Page*` in `SoftBudgetPageTests.cs` without naming this type) |
| `SymbolQueryError` | public abstract record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 无 (`new` of the abstract type does not appear; derived errors listed below) |
| `InvalidSymbolHandleError` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | `tests/DotNetMcp.Tests/TypeMemberLookupSeamTests.cs` (`Assert.IsType`); `tests/DotNetMcp.Tests/LanguageAdapterSeamTests.cs`† (`Assert.IsType`) |
| `SymbolNotFoundError` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | `tests/DotNetMcp.Tests/TypeMemberLookupSeamTests.cs` (`Assert.IsType`); `tests/DotNetMcp.Tests/LanguageAdapterSeamTests.cs`† (target-typed `new` in nested `FakeLanguageAdapter.NotFound`) |
| `SymbolAmbiguousError` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `StaleCursorError` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | `tests/DotNetMcp.Tests/SoftBudgetPageTests.cs` (`Assert.IsType`) |
| `DefinitionNotFoundError` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `MemberNotFoundError` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | `tests/DotNetMcp.Tests/TypeMemberLookupSeamTests.cs` (`Assert.IsType`) |
| `GeneratedSymbolRenameRefusedError` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `RenameLanguageNotSupportedError` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `InvalidRenameNameError` | public record | `src/DotNetMcp.Core/SymbolModels.cs` | DTO | 仅 SeamTests |
| `SymbolQueryErrorCodes` | public static class | `src/DotNetMcp.Core/SymbolModels.cs` | other | `tests/DotNetMcp.Tests/TypeMemberLookupSeamTests.cs` (const strings) |
| `DeclarationAvailability` | public static class | `src/DotNetMcp.Core/SymbolModels.cs` | other | 无 (MCP SeamTests assert a JSON/DTO **property** named `DeclarationAvailability`, not this Core type) |
| `SymbolOrigin` | public static class | `src/DotNetMcp.Core/SymbolModels.cs` | other | 无 |
| `TypeMemberLookup` | internal record | `src/DotNetMcp.Core/TypeMemberLookup.cs` | other | `tests/DotNetMcp.Tests/TypeMemberLookupSeamTests.cs` (return of `LookupTypeMemberAsync` / `LookupTypeMember` on a directly constructed `RoslynLanguageAdapter`; tests never write `new TypeMemberLookup` or the type name) |

## Counts

| Bucket | Count |
| --- | --- |
| public / internal types in `src/DotNetMcp.Core` | 92 |
| at least one fixture-free `new` / static-or-factory call / `Assert.IsType` / interface implementer | 24 |
| 仅 SeamTests | 44 |
| 无 | 24 |

The 24 with a direct test file: `CompilationLru`, `DiagnosticQueryService`, `FindRefsPageCursor`, `GeneratedSourcesPageCursor`, `GeneratorQueryService`, `GeneratorRunCache`, `HandwrittenDocumentDiff`, `ILanguageAdapter`, `IWorkspaceSession`, `LanguageAdapters`, `MemberPageCursor`, `RenameDocumentSlice`, `RoslynLanguageAdapter`, `SoftBudgetOptions`, `SoftBudgetPage`, `SymbolHandle`, `SymbolSummary`, `SymbolResolveSuccess`, `InvalidSymbolHandleError`, `SymbolNotFoundError`, `StaleCursorError`, `MemberNotFoundError`, `SymbolQueryErrorCodes`, `TypeMemberLookup`.

Services/modules with **no** fixture-free `new`: `CodeRefactoringService`, `DiagnosticFixService`, `DynamicInvocationQueryService`, `FindHitCache`, `GeneratorDriverRunner`, `CodeActionDocuments`.

## Exclusions (private nested; not in the 92)

| Type | Source |
| --- | --- |
| `FindRefsPageCursor.Payload` | `src/DotNetMcp.Core/FindRefsPageCursor.cs` |
| `GeneratedSourcesPageCursor.Payload` | `src/DotNetMcp.Core/GeneratedSourcesPageCursor.cs` |
| `MemberPageCursor.Payload` | `src/DotNetMcp.Core/MemberPageCursor.cs` |
| `GeneratorDriverRunner.WorkspaceAdditionalText` | `src/DotNetMcp.Core/GeneratorDriverRunner.cs` |

## Appendix 1 — Core error DTOs

All of these inherit `SymbolQueryError` except the abstract base itself. Codes live in `SymbolQueryErrorCodes` in `src/DotNetMcp.Core/SymbolModels.cs`.

| Type | Definition file |
| --- | --- |
| `SymbolQueryError` | `src/DotNetMcp.Core/SymbolModels.cs` |
| `InvalidSymbolHandleError` | `src/DotNetMcp.Core/SymbolModels.cs` |
| `SymbolNotFoundError` | `src/DotNetMcp.Core/SymbolModels.cs` |
| `SymbolAmbiguousError` | `src/DotNetMcp.Core/SymbolModels.cs` |
| `StaleCursorError` | `src/DotNetMcp.Core/SymbolModels.cs` |
| `DefinitionNotFoundError` | `src/DotNetMcp.Core/SymbolModels.cs` |
| `MemberNotFoundError` | `src/DotNetMcp.Core/SymbolModels.cs` |
| `GeneratedSymbolRenameRefusedError` | `src/DotNetMcp.Core/SymbolModels.cs` |
| `RenameLanguageNotSupportedError` | `src/DotNetMcp.Core/SymbolModels.cs` |
| `InvalidRenameNameError` | `src/DotNetMcp.Core/SymbolModels.cs` |
| `ProjectNotFoundError` | `src/DotNetMcp.Core/DiagnosticModels.cs` |
| `CompilationUnavailableError` | `src/DotNetMcp.Core/DiagnosticModels.cs` |
| `GeneratorNotFoundError` | `src/DotNetMcp.Core/DiagnosticModels.cs` |
| `SoftBudgetExceededError` | `src/DotNetMcp.Core/DiagnosticModels.cs` |
| `DiagnosticNotFoundError` | `src/DotNetMcp.Core/DiagnosticFixModels.cs` |
| `DiagnosticAmbiguousError` | `src/DotNetMcp.Core/DiagnosticFixModels.cs` |
| `FixLanguageNotSupportedError` | `src/DotNetMcp.Core/DiagnosticFixModels.cs` |
| `FixIndexOutOfRangeError` | `src/DotNetMcp.Core/DiagnosticFixModels.cs` |
| `GeneratedDocumentFixRefusedError` | `src/DotNetMcp.Core/DiagnosticFixModels.cs` |
| `FixApplyFailedError` | `src/DotNetMcp.Core/DiagnosticFixModels.cs` |
| `FixAllUnavailableError` | `src/DotNetMcp.Core/DiagnosticFixModels.cs` |
| `FixAllBudgetExceededError` | `src/DotNetMcp.Core/DiagnosticFixModels.cs` |
| `RefactoringLanguageNotSupportedError` | `src/DotNetMcp.Core/CodeRefactoringModels.cs` |
| `RefactoringIndexOutOfRangeError` | `src/DotNetMcp.Core/CodeRefactoringModels.cs` |
| `GeneratedSymbolRefactoringRefusedError` | `src/DotNetMcp.Core/CodeRefactoringModels.cs` |
| `GeneratedDocumentRefactoringRefusedError` | `src/DotNetMcp.Core/CodeRefactoringModels.cs` |
| `RefactoringApplyFailedError` | `src/DotNetMcp.Core/CodeRefactoringModels.cs` |

## Appendix 2 — FakeSolutionLoader factory methods

Public static methods that return `FakeSolutionLoader` or `LoadedSolution`, plus the internal `AttachWorkspaceXamlDocuments` helper. Embedded fixture source inside string literals is omitted.

| Method | File |
| --- | --- |
| `ImmediateMultiTfm` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `DelayedMultiTfm` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithDynamic` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateDynamicLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithComInterop` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateComInteropLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithSymbols` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithSymbolsOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithRenameOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithVbRenameOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `DelayedWithSymbols` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithFindRefsGraph` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithFsharpDiagnostics` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateFsharpDiagnosticsLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithVbDiagnostics` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithDiagnostics` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `DelayedWithDiagnostics` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithVbAndCSharp` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateVbAndCSharpLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithFsharpSymbols` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateFsharpSymbolsLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithFsharpCollidingFileNames` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateFsharpCollidingFileNamesLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithFsharpAndCSharp` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateFsharpAndCSharpLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithVbSymbols` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateVbSymbolsLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithVbGenerators` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithGenerators` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `DelayedWithGenerators` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateMultiTfmLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateSymbolsLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateViewModelLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithAvaloniaXamlSnapshot` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `AttachWorkspaceXamlDocuments` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithAvalonia` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithMaui` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithDataContext` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithVbXaml` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateAvaloniaLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateDataContextLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateVbXamlLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateMauiLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateSymbolsLoadedOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateRenameLoadedOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateVbRenameLoadedOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateVbGeneratorsLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateGeneratorsLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateVbDiagnosticsLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateDiagnosticsLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateFindRefsGraphLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithHierarchy` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `DelayedWithHierarchy` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateHierarchyLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithCallersGraph` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateCallersGraphLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithCallers` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `DelayedWithCallers` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `CreateCallersLoaded` | `tests/DotNetMcp.Tests/FakeSolutionLoader.cs` |
| `ImmediateWithMissingUsingOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.DiagnosticFix.cs` |
| `ImmediateWithVbMissingImportOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.DiagnosticFix.cs` |
| `ImmediateWithFixAllOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.DiagnosticFix.cs` |
| `CreateMissingUsingLoadedOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.DiagnosticFix.cs` |
| `CreateVbMissingImportLoadedOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.DiagnosticFix.cs` |
| `CreateFixAllLoadedOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.DiagnosticFix.cs` |
| `ImmediateWithEncapsulateFieldOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.Refactoring.cs` |
| `ImmediateWithVbEncapsulateFieldOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.Refactoring.cs` |
| `ImmediateWithProjectFixAllOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.Refactoring.cs` |
| `ImmediateWithVbProjectFixAllOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.Refactoring.cs` |
| `CreateEncapsulateFieldLoadedOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.Refactoring.cs` |
| `CreateVbEncapsulateFieldLoadedOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.Refactoring.cs` |
| `CreateProjectFixAllLoadedOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.Refactoring.cs` |
| `CreateVbProjectFixAllLoadedOnDisk` | `tests/DotNetMcp.Tests/FakeSolutionLoader.Refactoring.cs` |

`OpenAsync` on `FakeSolutionLoader` (`tests/DotNetMcp.Tests/FakeSolutionLoader.cs`) is the `ISolutionLoader` instance method, not a factory.
