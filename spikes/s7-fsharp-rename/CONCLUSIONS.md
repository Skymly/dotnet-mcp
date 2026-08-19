# Spike S7 结论：F# rename 技术路线

验证环境：现有 FCS 栈（`FSharpSymbolQueryService` / `GetAllUsesOfAllSymbols`）、AdhocWorkspace F# fixture、.NET 10。

## 总览推荐

| 路径 | 角色 |
|------|------|
| **不要** | 把 F# 塞进 `Renamer.RenameSymbolAsync` |
| **Preview** | FCS 使用点 → 仅替换简单标识符文本 → 与 C# 同形 Workspace Edit |
| **拒绝** | 类型提供器（`IsProvided` / `IsFromTypeProvider`）、`obj/` 与 `.g.fs`、非简单标识符（运算符/active pattern） |
| **Apply** | 复用 2.0 `WriteSuppression` + previewId / Epoch / TTL |

产品落在现有 `symbol_preview_rename` / `symbol_apply_rename`，工具名不分裂。
