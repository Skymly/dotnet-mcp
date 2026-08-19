# Spike S5: MAUI XamlC / CompiledBindings 技术路线

Issue [#96](https://github.com/Skymly/dotnet-mcp/issues/96)（阻断 Spec [#84](https://github.com/Skymly/dotnet-mcp/issues/84) P2）。验证 MAUI `.xaml` 能否挂在现有 `xaml_*` 工具面上，以及 XamlC / SourceGen 与 Avalonia NameGenerator 的归因差异。

本 spike **不**改产品 MCP 工具面。

## 如何跑

```powershell
cd spikes/s5-maui-xaml
dotnet build fixtures/MauiPage/MauiPage.csproj
dotnet test src/S5.Verification/S5.Verification.csproj
```

需要本机 MAUI workload（已测：`maui-windows` 10.0.20）。

## 布局

| 路径 | 作用 |
|------|------|
| `fixtures/MauiPage` | `UseMaui` + `MainPage.xaml`（x:Class / x:Name / x:DataType Binding） |
| `src/S5.Verification` | MSBuildWorkspace 观测 SourceGen 树与字段 |

结论见 [CONCLUSIONS.md](CONCLUSIONS.md)。
