# Spike S8 结论：CodeRefactoring 宿主技术路线

验证环境：Roslyn **5.6.0**、`Microsoft.CodeAnalysis.CSharp.Features` + `VisualBasic.Features`、AdhocWorkspace、Windows、.NET 10。测试：`dotnet test` 于本目录（3/3 通过）。

## 总览推荐

| 路径 | 角色 | 风险 |
|------|------|------|
| **发现** | `Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features")` / `VisualBasic.Features`，反射无参构造的 `CodeRefactoringProvider` | 带 MEF 依赖的 provider 会被跳过；对 public 字段 Encapsulate 足够 |
| **列出** | 光标 = 符号 `Locations` 中第一条 in-source span；`CodeRefactoringContext` + `ComputeRefactoringsAsync`；展开 `NestedActions` | 标题对 LLM 可读；`refactoringIndex` 按 Title+EquivalenceKey 排序后稳定 |
| **Preview** | `CodeAction.GetOperationsAsync` → `ApplyChangesOperation.ChangedSolution` → `GetChanges` 切片；**不写盘** | 与 rename / Diagnostic fix 同形 |
| **生成树** | 变更若含 source-generated document 或新增/删除文档 → 整次拒绝 | 与 3.0 Diagnostic fix 同一哲学 |
| **禁止** | 完整 IDE MEF；手写「看起来像官方」的补丁；任意选区 extract method | 宿主只用已引用 Features + 无参可构造的 provider |

**产品含义**：P0/P1 走 Features 反射宿主，不引入 VisualStudio.Workspace。VB 用同一合同、另一程序集。F# 无此宿主 → `RefactoringLanguageNotSupported`。

**P0 演示环**：C# public 字段标识符处的 first-party Encapsulate field（或产生 `get`/`set`/PascalCase 属性的等价重构）。Preview 前后磁盘相等。

## 逐题结论

### Q1 — 能否不靠 VS 列出并应用 first-party refactoring

**可以。** `RefactorApp.Widget.count` public 字段上，无参 `CodeRefactoringProvider` 能列出至少一条可应用 action；应用到新 `Solution` 后文本引入属性形态，磁盘不变。

VB `Public count As Integer` 同一发现规则也能列出并应用至少一条会改文本的 action。

### Q2 — 完整 MEF 是否必要

**不必。** 与 S6 相同：`GetTypes()` + 无参构造即可；ctor 或 `ComputeRefactoringsAsync` 抛错的 provider 跳过。

### Q3 — 定位

锁定：**SymbolHandle**。宿主把光标放在该符号的 in-source identifier span。不要新 Handle，不要任意选区。

### Q4 — 工具形状

锁定：`symbol_list_refactorings` → `symbol_preview_refactoring(refactoringIndex)` → `symbol_apply_refactoring(previewId)`。空列表是成功。

## 推荐包引用

- `Microsoft.CodeAnalysis.CSharp.Features` 5.6.0（产品已引用）
- `Microsoft.CodeAnalysis.VisualBasic.Features` 5.6.0（产品已引用）
