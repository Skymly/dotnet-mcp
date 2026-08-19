# Spike S6 结论：CodeFix 宿主技术路线

验证环境：Roslyn **5.6.0**、`Microsoft.CodeAnalysis.CSharp.Features` + `VisualBasic.Features`、AdhocWorkspace、Windows、.NET 10。测试：`dotnet test` 于本目录（3/3 通过）。

## 总览推荐

| 路径 | 角色 | 风险 |
|------|------|------|
| **发现** | `Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features")` / `VisualBasic.Features`，反射无参构造的 `CodeFixProvider` | 带 MEF 依赖的 provider 会被跳过；对 CS0246 / BC30002 足够 |
| **列出** | `FixableDiagnosticIds` 过滤 → `CodeFixContext` + `RegisterCodeFixesAsync`；展开 `NestedActions` | 标题对 LLM 可读；`fixIndex` 按 Title+TypeName 排序后稳定 |
| **Preview** | `CodeAction.GetOperationsAsync` → `ApplyChangesOperation.ChangedSolution` → `GetChanges` 切片；**不写盘** | 与 rename 同形 |
| **生成树** | 变更若含 source-generated document 或新增/删除文档 → 整次拒绝 | 与 2.0 rename 拒绝生成声明同一哲学 |
| **禁止** | 完整 IDE MEF `CodeFixService`；手写「看起来像官方」的补丁；下载额外分析器 | 宿主必须只用已引用 Features + 项目 AnalyzerReference 上能无参构造的 provider |

**产品含义**：P0/P1 走 Features 反射宿主，不引入 VisualStudio.Workspace。VB 用同一合同、另一程序集。F# 无此宿主 → `FixLanguageNotSupported`。

## 逐题结论

### Q1 — 能否不靠 VS 列出并应用 first-party fix

**可以。** C# `List<int>` 缺 using 产生 **CS0246**；反射宿主能列出 FullyQualify / Add Import 等 action。对 action 调用 `GetOperationsAsync` 得到新 `Solution` 后，诊断消失且文本含 `System.Collections.Generic`。Preview 前后磁盘 **相等**。

VB 缺 `Imports` 产生编译错误，同一发现规则能列出含 `System.Collections.Generic` / `Imports` 的 action。

### Q2 — 完整 MEF 是否必要

**不必。** `CSharpFullyQualifyCodeFixProvider` 等类型是 **internal**，但 `GetTypes()` + 无参构造即可实例化。需要 `ImportingConstructor` 的 provider 自然被跳过。P0 演示环不依赖它们。

### Q3 — 诊断定位

继续用现有 `DiagnosticItem` 合同：`projectId` + `diagnosticId` + path + 1-based line / 0-based character。不要新 Handle。0 条 = `DiagnosticNotFound`；>1 条 = `DiagnosticAmbiguous`。

### Q4 — 工具形状

锁定：`diagnostics_list_fixes` → `diagnostics_preview_fix(fixIndex[, scope])` → `diagnostics_apply_fix(previewId)`。`scope=occurrence|document`（P3）。空列表是成功。

## 推荐包引用

- `Microsoft.CodeAnalysis.CSharp.Features` 5.6.0
- `Microsoft.CodeAnalysis.VisualBasic.Features` 5.6.0
