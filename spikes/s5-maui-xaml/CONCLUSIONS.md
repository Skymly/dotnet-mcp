# Spike S5 结论：MAUI XAML 技术路线

验证环境：.NET 10、`maui-windows` **10.0.20**、`Microsoft.Maui.Controls` 10.0.20、Roslyn MSBuildWorkspace 5.6.0、Windows。测试：`dotnet test` 于本目录（2/2 通过）。

## 总览推荐

| 路径 | 角色 | 风险 |
|------|------|------|
| **文档识别** | `.xaml` **且** 根 xmlns = `http://schemas.microsoft.com/dotnet/2021/maui`（可辅以项目 `UseMaui`） | 任意 WPF `.xaml`（`.../xaml/presentation`）不得当 MAUI |
| **x:Name** | Roslyn **`Microsoft.Maui.Controls.SourceGen`**（`XamlGenerator`）在编译期生成 `TitleLabel` 字段 + `InitializeComponent` | 与 Avalonia NameGenerator **同形**：生成树在 compilation 上，可归因 |
| **Binding** | `x:DataType` + `Microsoft.Maui.Controls.BindingSourceGen` | 无 x:DataType 时与 Avalonia 一样走 MissingDataType；P3 再做 code-behind DataContext |
| **XamlC** | **编译后 IL 织入**（MSBuild `XamlC` 任务碰 `XamlC.stamp`），替换 `LoadFromXaml` | **不要**当符号来源；句柄用 SourceGen 字段即可 |

**ADR / CONTEXT**：修订「XAML document = 仅 Avalonia `.axaml`」——MAUI 是第二个已注册框架，工具名不分裂。Avalonia 继续只吃 `.axaml`。

---

## 逐题结论

### Q1 — Document identity

`MainPage.xaml` 根：

- 元素 `ContentPage`
- xmlns = **`http://schemas.microsoft.com/dotnet/2021/maui`**
- `x:Class` = `MauiPage.MainPage`
- 扩展名 **`.xaml`**

对照：Avalonia = `.axaml` + `https://github.com/avaloniaui`；WPF = `.xaml` + `http://schemas.microsoft.com/winfx/2006/xaml/presentation`。

**产品规则**：`.xaml` 只有根 xmlns 是 MAUI URI（或项目标记 UseMaui 且 xmlns 匹配）才注册。WPF `.xaml` → 现有 `UnsupportedXamlDocument`。

### Q2 — x:Name fields

SourceGen 写出（FilePath 观测）：

```
obj/.../Microsoft.Maui.Controls.SourceGen/Microsoft.Maui.Controls.SourceGen.XamlGenerator/MainPage.xaml.sg.cs
```

内含：

- `private Label TitleLabel;` + `[GeneratedCode("Microsoft.Maui.Controls.SourceGen", "1.0.0.0")]`
- `InitializeComponent()` 先 `LoadFromXaml`，注释写明 **XamlC 会替换方法体**

`INamedTypeSymbol.GetMembers("TitleLabel")` → **Field, InSource**。

磁盘上**没有**独立的 `*.xaml.g.cs`（除 GlobalUsings）；生成树走 Roslyn generated documents，FilePath 编码 `{Assembly}/{Type}/{Hint}`，与 S1 Avalonia/自定义生成器同构。

**归因**：`Origin = SourceGenerator(Microsoft.Maui.Controls.SourceGen::Microsoft.Maui.Controls.SourceGen.XamlGenerator@…)`。Avalonia 是 `Avalonia.NameGenerator::NameGenerator`。产品应把「NameGenerator 字段」泛化为「已注册框架的 x:Name 生成字段」，不要写死 Avalonia 类型名。

XamlC 任务在 **csc 之后**跑：`Compiling Xaml, assembly: MauiPage.dll`。它改 IL，不提供新的 ISymbol。

### Q3 — CompiledBindings / x:DataType

Fixture：`x:DataType="local:MainViewModel"` + `{Binding Title}`。

分析器列表含 **`Microsoft.Maui.Controls.BindingSourceGen.dll`**，并生成 `GeneratedBindingInterceptorsCommon.g.cs`。

ViewModel 属性 `MainViewModel.Title` 是手写符号——现有 `xaml_resolve_binding` 的 x:DataType 路径可复用（解析类型 + 逐段成员）。不必解析 BindingSourceGen 拦截器。

无 `x:DataType`：沿用 `MissingDataType`；P3 再做静态 DataContext。

### Q4 — Diagnostics

本 spike 未跑完整 XamlC 诊断目录。可落地的产品路径：

- XML 良构 / 缺 x:Class / 未知 xmlns / 缺 x:Name —— 与 Avalonia 同一套 `xaml_diagnostics`
- 绑定属性不存在 —— 复用 Binding 路径错误
- XamlC 任务诊断在 MSBuild 日志，不进 Roslyn compilation diagnostics

P2 不要把 XamlC MSBuild 错误当 MCP 诊断主路径。

### Q5 — Cost

MSBuildWorkspace 打开 + 首次编译本 fixture：**~4234 ms**（含 SourceGen）。单页、单次完成，落在 5s 作用域预算附近；产品打开工作区已付过 MSBuild 加载。无需为 xaml_* 单开 Soft budget。

---

## 给实现票的合同（冻结）

1. `xaml_*` 工具名不变。
2. Avalonia：仅 `.axaml`。
3. MAUI：`.xaml` + MAUI xmlns；WPF `.xaml` 仍 Unsupported。
4. `xaml_resolve_class` / xmlns / binding / diagnostics 与 Avalonia 同形。
5. `xaml_resolve_name`：在 x:Class 类型上找 SourceGen 生成的字段（不要依赖 XamlC IL）。
6. CONTEXT「XAML document」改为「已注册框架的 UI 文档（Avalonia `.axaml` / MAUI `.xaml`）」。
