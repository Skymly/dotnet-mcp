# Spike S1 结论：源生成器归因技术路线

验证环境：Roslyn **5.6.0**、MSBuildWorkspace、Windows、CommunityToolkit.Mvvm 8.4。测试：`dotnet test` 于本目录（9/9 通过）。

## 总览推荐

| 路径 | 角色 | 风险 |
|------|------|------|
| **主路径（公开）** | 剔除生成树 → 自建 `CSharpGeneratorDriver` → 按 **SyntaxTree 内容**（辅以 HintName）映射到 `GetGeneratorType()` | 生成器跑两遍；须对齐 ParseOptions / AnalyzerConfig；内容对账成本 |
| **加速启发式（非契约）** | 解析 `Document.FilePath` 中的 `{Assembly}/{Type}/{HintName}` 段 | MSBuild 与 Adhoc 格式不同；SDK/Roslyn 升级可能变；**不得**作唯一真相 |
| **反射兜底** | 读 `SourceGeneratedDocument.Identity`（internal） | 私有 API；需形状守护测试；升级即可能失效 |

**ADR-0001 §6 总体确认**：公开归因仍须自建 driver；HintName 跨生成器不唯一（Q4 实证）。**细化**：MSBuildWorkspace 下 `FilePath` *当前*常编码生成器程序集与类型（见 Amendment 2），但是启发式而非契约。

---

## 逐题结论

### Q1 — `Document.FilePath` 实际格式

MSBuildWorkspace（SampleApp）示例：

```text
...\obj\Debug\net8.0\generated\CommunityToolkit.Mvvm.SourceGenerators\CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator\SampleApp.PersonViewModel.g.cs
```

- `Name` / `HintName` = `SampleApp.PersonViewModel.g.cs`（短名）
- Windows 分隔符为 `\`；路径段稳定地出现 `{GeneratorAssembly}\{GeneratorTypeFullName}\{HintName}`
- 大小写：Roslyn **保留**构造路径时的大小写（PascalCase 程序集/类型名），不是强制 lower；Windows 文件系统上比较应 OrdinalIgnoreCase
- AdhocWorkspace 则为相对虚拟路径（无 `obj/.../generated` 前缀），例如 `CommunityToolkit.Mvvm.SourceGenerators\...\AdhocSample.AdhocVm.g.cs`
- Fixture 同时可见 CommunityToolkit、System.Text.Json.SourceGeneration、CustomGenerator 三类文档

**结论**：FilePath **经常**编码生成器身份，但跨 workspace 实现不一致 → **不能**当作公开契约；可作缓存加速键的候选，须有 driver/反射校验。

### Q2 — 剔除生成树

- `GetSourceGeneratedDocumentsAsync()` 得到的树与 `compilation.SyntaxTrees` **引用相等**（本机 10/10 `ReferenceEquals`）
- `compilation.RemoveSyntaxTrees(thoseTrees)` 可得到干净 base compilation（strip 后匹配生成树数 = 0）
- 未发现比「收集生成文档树再 Remove」更直接的公开 API

### Q3 — 自建 driver 等价性

- 从 `AnalyzerReferences.GetGenerators(C#)` 建 `CSharpGeneratorDriver`，传入 `project.ParseOptions` 与 `AnalyzerConfigOptionsProvider`
- SampleApp：**workspace 10 篇 ↔ driver 10 源，内容 100% 对账成功**
- 差异风险点：未对齐的 additional files / 全局 analyzer config / 生成器依赖的 MSBuild 项；本 fixture 下未见分叉

### Q4 — HintName 冲突

- CollisionA/B 均输出 `SharedHint.g.cs`
- Driver：两条源，**HintName 字符串相同**，生成器类型不同
- Workspace：保留两篇文档，HintName 相同，但 FilePath 分别含 `CollisionA.CollisionGeneratorA` / `CollisionB.CollisionGeneratorB`

**结论**：按 HintName 关联 **必然失效**；须用（生成器身份 × HintName）或内容哈希。

### Q5 — 成本

| 场景 | 观测 |
|------|------|
| SampleApp `GetCompilationAsync` | ~900 ms（含首次生成） |
| 同项目 strip + driver | ~23 ms；GC Δmem 量级见测试输出（指示性，非 WorkingSet） |
| +200 个 `[ObservableProperty]` 类再跑 driver | ~399 ms，产出 210 个 generated sources；伴随明显 GC 增量 |

- 按需、按 **单项目** 跑 driver 可行，契合 ADR-0002 `(projectId, epoch)` 缓存
- **未跑** 150 项目 Avalonia 解；大型成本按「单项目 driver × 缓存未命中项目数」外推；内存需在产品实现中用 WorkingSet/分配剖析复核

### Q6 — 反射兜底

- `SourceGeneratedDocument.Identity` 仍为 **非公开**（`IsPublic=False`）
- 反射可读 `Generator.{AssemblyName,AssemblyPath,AssemblyVersion,TypeName}` + HintName/FilePath
- **可行**作为加速/对账，但必须有守护测试（本 spike 的 Q6）：属性缺失即失败
- 风险：Internals 重命名/变 record 布局 → 归因静默失败或抛错；产品若采用须文档化「私有 API 依赖」

### Q7 — 符号 → 归因链路

端到端已跑通：

1. `ISymbol`（如 `PersonViewModel.Name`）
2. `DeclaringSyntaxReferences` → `SyntaxTree`
3. `Location` 仅有 `IsInSource` / `IsInMetadata`（无 `IsInSourceGeneratedDocument`）→ 用生成文档集合判定
4. 内容匹配 driver `GeneratedSources` → `ObservablePropertyGenerator`

输出：`CommunityToolkit.Mvvm.SourceGenerators::CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator`

### Q8 — partial 逐成员归因

- `DisplayName` / `Format()` / `Format(string)` → Handwritten
- `Name` / `Age` → ObservablePropertyGenerator
- 重载键须含参数类型列表，否则 `Format` 撞键

### Q9 — AdhocWorkspace fixture

- **AdhocWorkspace + AnalyzerReferences 下源生成器会运行**，`GetSourceGeneratedDocumentsAsync()` 有效（本机返回 4 篇）
- 单测可直接用 AdhocWorkspace；若某 Roslyn 版本回归为 0 文档，退化为「对 compilation 直接跑 driver」

---

## 写入实现 spec 的约束

1. `GetCompilationAsync` 结果含生成树；自建 driver **必须先 strip**（Q2）
2. 归因主键：`(generatorTypeFullName, assemblyName, assemblyVersion?)` + 成员签名；**禁止**单独用 HintName
3. 对账优先 **源文本相等**，HintName / FilePath 仅辅助
4. 缓存键：`(projectId, epoch)`；按项目按需跑
5. FilePath 解析仅作启发式，且须区分 MSBuild vs Adhoc 形态
6. 若用反射 Identity：升级 Roslyn 的 CI 守护测试必跑
7. 单测接缝：AdhocWorkspace 可用；保留纯 driver 路径作后备
8. partial 成员字典键含方法签名

## 脆弱点（完成判据要求）

- Roslyn 升级可能改变 FilePath 布局、Identity 可见性、Adhoc 生成行为
- 生成器包升级可能改变 HintName / 输出拆分，内容对账仍应成立
- SDK 内置生成器（STJ、Regex、Interop）会出现在 `AnalyzerReferences` 中，归因实现须过滤或一并展示
