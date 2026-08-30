# XAML 类型与现有直接单测对照

Snapshot: `origin/main` @ `a1a9f9aac46efea133792064af4a62306e3e7d1a` (`Add FSharpSymbolQueryService representative-path unit tests (#194) (#195)`).

Scope: every `public` / `internal` type declared in `src/DotNetMcp.Xaml/*.cs`. Private nested types are listed only under Exclusions.

`DotNetMcp.Xaml.csproj` has **no** `InternalsVisibleTo`.

## Criterion

A **direct** test is a test method that constructs or names the XAML type **without** `InProcessMcpFixture` and **without** MCP `CallToolAsync` / `tools/call`.

Counted as direct: `new TypeName(...)`, static members on the type, `Assert.IsType<TypeName>(...)` in such a method.

Not counted as direct: MCP SeamTests; source-text scans; constructing the type only inside Server / `InProcessMcpFixture`.

## Direct-test files (whole file has no `InProcessMcpFixture`)

None for `DotNetMcp.Xaml` types. There is no `XamlDocumentServiceTests.cs`.

`XamlListXmlnsSeamTests` asserts `XamlXmlnsSource.Using` / `ClrNamespace` / `XmlnsDefinition` **after** MCP `CallToolAsync` — counted as 仅 SeamTests, not direct.

## Inventory (16 public + 1 internal)

| Type | Visibility | Source | Role | Direct tests |
| --- | --- | --- | --- | --- |
| `XamlDocumentService` | public class | `XamlDocumentService.cs` | service | 仅 SeamTests（无 `new XamlDocumentService`） |
| `XamlDocumentRoot` | internal record | `XamlDocumentService.cs` | other | 无（无 InternalsVisibleTo） |
| `XamlQueryError` | public abstract record | `XamlModels.cs` | DTO | 仅 SeamTests |
| `MissingXamlClassError` | public record | `XamlModels.cs` | DTO | 仅 SeamTests |
| `XamlDocumentNotFoundError` | public record | `XamlModels.cs` | DTO | 仅 SeamTests |
| `UnsupportedXamlDocumentError` | public record | `XamlModels.cs` | DTO | 仅 SeamTests |
| `UnknownXmlnsPrefixError` | public record | `XamlModels.cs` | DTO | 仅 SeamTests |
| `MissingXamlNameError` | public record | `XamlModels.cs` | DTO | 仅 SeamTests |
| `NameGeneratorNotRunError` | public record | `XamlModels.cs` | DTO | 仅 SeamTests |
| `BindingPropertyNotFoundError` | public record | `XamlModels.cs` | DTO | 仅 SeamTests |
| `BindingTypeMismatchError` | public record | `XamlModels.cs` | DTO | 仅 SeamTests |
| `MissingDataTypeError` | public record | `XamlModels.cs` | DTO | 仅 SeamTests |
| `XamlQueryErrorCodes` | public static class | `XamlModels.cs` | other | 仅 SeamTests |
| `XamlBindingSegment` | public record | `XamlModels.cs` | DTO | 仅 SeamTests |
| `XamlXmlns` | public static class | `XamlModels.cs` | xmlns | 仅 SeamTests |
| `XamlXmlnsSource` | public static class | `XamlModels.cs` | xmlns | 仅 SeamTests（常量出现在 MCP 断言里） |
| `XamlXmlnsMapping` | public record | `XamlModels.cs` | DTO | 仅 SeamTests |

## Exclusions (private nested)

| Type | Source | Notes |
| --- | --- | --- |
| `XmlnsDefinition` | `XamlDocumentService.cs` | `private sealed record` |

## Appendix 1 — `XamlDocumentService` public 入口

Ctor: `XamlDocumentService(LanguageAdapters languages, RoslynLanguageAdapter roslyn, SoftBudgetOptions? softBudgets = null)`.

| Member | File:line | Signature (abbrev.) |
| --- | --- | --- |
| `AvaloniaDocumentExtension` | `XamlDocumentService.cs:13` | `const string` = `.axaml` |
| `MauiDocumentExtension` | `XamlDocumentService.cs:14` | `const string` = `.xaml` |
| ctor | `XamlDocumentService.cs:20` | see above |
| `ResolveClassAsync` | `XamlDocumentService.cs:30` | `(session, path)` → `SymbolResolveSuccess` / `XamlQueryError` / `SymbolQueryError` |
| `ResolveNameAsync` | `XamlDocumentService.cs:53` | `(session, path, name)` |
| `ResolveBindingAsync` | `XamlDocumentService.cs:116` | `(session, path, bindingPath, dataType?)` → `IReadOnlyList<XamlBindingSegment>` |
| `GetDiagnosticsAsync` | `XamlDocumentService.cs:236` | `(session, path, limit?, cursor?, softBudget?)` → `PagedResult<DiagnosticItem>` |
| `ListXmlnsAsync` | `XamlDocumentService.cs:284` | `(session, path, prefix?)` → `IReadOnlyList<XamlXmlnsMapping>` |
| `ReadClassName` | `XamlDocumentService.cs:323` | `(session, path)` → `string? ClassName` |

Several methods return a **dual channel**: XAML `XamlQueryError` **and** Core `SymbolQueryError`.

## Appendix 2 — SeamTests / `FakeSolutionLoader` 如何构造 XAML 输入

XAML 文档**不**进 `FSharpWorkspaceSnapshot`。现有测试全部走 MCP + 下列 loader；磁盘 `.axaml` / `.xaml` 由各 SeamTest 写入临时目录，再 `workspace_open`。

| Method | File:line | What it builds |
| --- | --- | --- |
| `ImmediateWithAvalonia` | `FakeSolutionLoader.cs:786` | Adhoc C# code-behind (`MainWindow.axaml.cs`)；Avalonia-shaped x:Class 解析。不加载 WPF/MAUI/WinUI |
| `ImmediateWithAvaloniaXamlSnapshot` | `FakeSolutionLoader.cs:700` | 把给定 path 的 axaml 文本挂到 workspace 文档（snapshot 可与磁盘不同） |
| `AttachWorkspaceXamlDocuments` | `FakeSolutionLoader.cs:723` | 扫描解决方案旁 `.axaml` / `.xaml` 并 AddDocument |
| `ImmediateWithMaui` / `CreateMauiLoaded` | `FakeSolutionLoader.cs:790` / `995` | Adhoc `MainPage.xaml.cs`；MAUI xmlns + SourceGen x:Name 字段桩（Spike S5） |
| `ImmediateWithVbXaml` / `CreateVbXamlLoaded` | `FakeSolutionLoader.cs:798` / `961` | VB code-behind `MainWindow.axaml.vb` |

`FakeSolutionLoader` 工厂本身 **不** `new XamlDocumentService`。服务由 Server 在 MCP 工具路径构造（`LanguageAdapters` + `RoslynLanguageAdapter`）。

### MCP XAML SeamTests（无直接 `new XamlDocumentService`）

| File | Loader |
| --- | --- |
| `XamlResolveClassSeamTests.cs` | `ImmediateWithAvalonia` |
| `XamlResolveNameSeamTests.cs` | `ImmediateWithAvalonia` |
| `XamlResolveBindingSeamTests.cs` | `ImmediateWithAvalonia` |
| `XamlDiagnosticsSeamTests.cs` | `ImmediateWithAvalonia` |
| `XamlListXmlnsSeamTests.cs` | `ImmediateWithAvalonia` |
| `XamlWorkspaceSnapshotSeamTests.cs` | `ImmediateWithAvaloniaXamlSnapshot` |
| `MauiXamlSeamTests.cs` | `ImmediateWithMaui` |

No test constructs `XamlDocumentService` with a handwritten XAML string without going through MCP.