# Spike S10 结论：F# 跨项目符号查找（FCS）

验证环境：FSharp.Compiler.Service **43.12.402**（与产品同版）、Windows、.NET 10（SDK 10.0.303）。
测试：`dotnet test src/S10.Verification -c Release` —— **17/17 通过**。
fixture：`%TEMP%/s10-fsx` 下实体化 two/（FsLib←FsApp）与 three/（FsLib←FsMid←FsTop）两条链；
源拷自 `tests/fixtures/FsharpCrossProject` 并加了 `IShape`/`Coin`（跨项目接口实现）与参数化 caller。
产品逻辑逐字复制进 `ProductLogic.cs`（BuildCompilerArgs/CompilerReferences/签名格式化/SameSymbol/
FindEnclosingDefinition/rename 切片），`FcsHost` 复制 checker + DocumentSource + NotifyFileChanged 管线。

## 总览：A（内存项目引用）vs B（-r:dll）

| 维度 | A：`ReferencedProjects` + `FSharpReferencedProject.NewFSharpReference(key, depOptions)` | B：`-r:path/dep.dll` |
|------|----------------------------------------|----------------------|
| 前置条件 | 无 —— `-r:` 文本仅作 ReferencedProjects 的键，**dll 可不存在** | 必须先产出依赖 dll（`dotnet build` 或 `checker.Compile`） |
| 符号身份 | 依赖项目 use.Symbol 与定义侧 `IsEffectivelySameAs`=**true** | **同样 true**（dll 导入符号 vs 源检查符号判同；`ReferenceEquals`=false） |
| `check(dep).GetUsesOfSymbol(定义侧符号)` | 命中依赖项目内使用点 | **同样命中** |
| `SameSymbol`（签名串比较，产品现行规则） | 命中 | 命中（两侧 `FullName` 都是 `FsLib.Core.doubleThenBump`） |
| 依赖输出缺失时 | **0 诊断**（FSharpReference 替代程序集解析） | FS0078 + FS0039 级联（今日的 `DependencyOutputNotBuilt` 正解释它） |
| 非 F# 依赖（如 C# lib） | FSharpReference 装不了 → 缺失仍 FS0078 | 同 —— 都需要真文件 |
| 两项目耗时（本机粗测） | 冷 408ms（整图一次 check）；暖 lib=220ms → app=46ms | `dotnet build`(增量) ≈1017ms 或 `checker.Compile` ≈1373ms，+ app check 150–330ms |
| 三项目链（FsLib←FsMid←FsTop） | 逐项目暖 239+119+119=477ms；只查顶层冷启 173ms | ——（需先逐层 build；本 spike 未测） |

**核心结论：两条路线下符号匹配都成立** —— FCS 43 的 `IsEffectivelySameAs`/`GetUsesOfSymbol`
按逻辑身份判同，对「源 check 的符号 vs 依赖项目里 dll 导入/内存引用的符号」一视同仁。
差别只在 *如何获得依赖项目的 check*（要不要先构建）与 *缺输出时的诊断*。

## 逐题结论

### Q1 — 依赖项目里的符号能否匹配、能否找到使用点

**能，A、B 两路都验证过。**

- `RouteA_uses_resolve_and_symbols_match_via_IsEffectivelySameAs`：FsApp 中 `FsLib.Core.doubleThenBump`
  使用点（Use.fs:3:23）`IsEffectivelySameAs(libSymbol)=True`。
- `RouteB_uses_resolve_after_build_and_IsEffectivelySameAs_still_matches`：dll 经 `dotnet build`
  产出后，同点 `IsEffectivelySameAs=True` 且 `ReferenceEquals=False`。
- `RouteA/RouteB_GetUsesOfSymbol…`：把**定义项目 check 里的 FSharpSymbol** 直接传给
  `appCheck.GetUsesOfSymbol`，两路都返回 Use.fs 中的使用点。
- `Both_routes_signature_match_finds_uses`：产品现行 `SameSymbol`（catalog 签名串
  `FsLib.Core.doubleThenBump(System.Int32)`）两路同命中 —— 现有 handle/catalog 形状可复用。
- callers：把 `GetAllUsesOfAllSymbols` 的 def-use 收集后按**依赖项目自身 catalog** 过滤再取
  最近 enclosing，得 `FsApp.Use.doubled(System.Int32)`（两路同）。注意：产品现行顺序
  「先取最近定义再 `MatchCatalog`」会被参数 `n`（Use.fs:3:13 的 def-use）截获 →
  `MatchCatalog` 失败 → 调用者被丢。**这是单项目就存在的顺序问题**，跨项目前先修。
- implementations / type hierarchy：依赖项目 `Coin` 的 `DeclaredInterfaces` 解析出
  `FsLib.Core.IShape`（两路同）→ 跨项目实现/层级 = 把各项目 catalog 按 `SignatureQualifiedName`
  合并比较，无需新机制。

### Q2 — #330 诊断与 `FSharpWorkspaceSnapshot` 形状

- B 缺输出：`routeB-missing` 实测 → FS0078（找不到 spike-ref\FsLib.dll）+ FS0039
  「未定义…FsLib」×6 + FS0072/FS0859/FS0887 级联 —— `MissingDependencyOutputs` 解释成立。
- **A 缺输出：check 完全干净（0 诊断）** —— F#→F# 引用下 `DependencyOutputNotBuilt` 不再必要；
  `-r:` 文本降级为 ReferencedProjects 的键。
- A 缺 **非 F#** 输出：`SomeCsLib.dll` 缺失仍 FS0078 —— C#/VB 依赖仍需真 dll，诊断保留。
- 快照影响：现 `FSharpProjectSnapshot.References` 只装**已存在**的 dll 路径（`ReadCompilerReferences`）；
  route A 需要「`-r:` 键 → 依赖的 F# 项目」的边。改动点：快照加
  `ProjectReferences: IReadOnlyList<(string ProjectId, string OutputFile)>`，用
  `OutputFilePath` 作稳定键（不要求存在）；`DependentProjects`（#517）已提供反向边。
- `DocumentSource` 继续工作：依赖项目源经同一 custom source 供文本，不需要额外
  `NotifyFileChanged`（本 spike 只 notify 被 check 项目自己的文件，依赖源照常解析）。

### Q3 — 耗时与 soft budget

数字（`%TEMP%/s10-fsx/TIMING.txt`，本机单次粗测）：

```
two/A cold  : 408 ms（app check 内联检查 FsLib 整图）
two/A warm  : lib=220 ms → app=46 ms（同一 checker + 同一 options 实例命中缓存）
two/B       : dotnet build FsLib=1017 ms(增量) + app check=330 ms
two/B(fsc)  : checker.Compile FsLib=1373 ms + app check=151 ms
three/A warm: lib=239, mid=119, top=119（和=477 ms）
three/A cold: 只查 top 整图 = 173 ms
```

- A 没有构建步骤；成本随依赖源总量增长，且**一个 checker 实例内只付一次**（缓存按 options 键）。
- B 的成本外置到 msbuild/fsc；check 本身与 A 同量级。
- soft budget（`soft_budget_gates_sequential_cross_project_scan`）：一个 `Stopwatch` 跨
  「定义项目 → 各依赖项目」的顺序扫描共享，`clock.Elapsed >= budget` 即置 `truncatedByBudget`；
  `budget=0` 时第二个项目 0 命中 + truncated=True。check 本身不入预算 —— 与产品现行
  （FindReferencesAsync 在 check 完后才开表）一致。
- 性能注意：route A 要求把**同一个 `FSharpProjectOptions` 实例**放进 `FSharpReference`，
  否则 checker 缓存不命中（46ms vs 408ms 的差距）。

### Q4 — rename 跨项目文本改写

- 依赖项目使用点的 `use.Range` 覆盖**限定名整体**：slice = `'FsLib.Core.doubleThenBump'`
  （3:23–3:48，25 字符）；产品 `IsSimpleIdentifierUse` 的「`endswith .oldName`」分支原样通过，
  `ApplyReplacements` 改写尾部 → `FsLib.Core.renamedFn`（两路同，逐字验证输出）。
- 定义项目侧 slice 为裸标识符 `'doubleThenBump'` —— 与单项目规则完全一致。
- 唯一新增前提：使用点文件来自**依赖项目的快照文档**，`TryGetSnapshot` 需能找到它
  （按 path 键即可，现机制够）。

## 锁定

- **选用路线**：A 为主路径（F#→F# 免构建、消 FS0039 假象、符号直接可判同）；
  B 为自然回退（ReferencedProjects 组装失败/缺项时靠 `-r:` 文件）。
- **符号匹配规则**：主 `use.Symbol.IsEffectivelySameAs(targetSymbol)` —— 需要把解析到的
  `FSharpSymbol` 随行携带（check 生命周期内有效）；兜 `SameSymbol`/签名串比较
  （无符号句柄时，跨 check 复用现有 catalog）。
- **查找形状**：定义项目 check + 每个 F# 依赖项目 check（route A options）→
  `GetUsesOfSymbol` 或 `GetAllUsesOfAllSymbols`+匹配；callers 在**每个被扫项目的自身 catalog**
  上先过滤 def-use 再取最近 enclosing。
- **预算策略**：单一 Stopwatch 跨项目传递；check 不计入；扫描中断 → truncatedByBudget +
  scanIncomplete 分页语义沿用 `SoftBudgetPage`。
- **快照改动点**：`FSharpProjectSnapshot` 增 `ProjectReferences`（ProjectId + 稳定 OutputFile
  键）；`References`/`MissingDependencyOutputs` 收窄为非 F# 依赖语义。
- **诊断改动点**：`DependencyOutputNotBuilt` 仅对非 F# 依赖（或走 B 回退的 F# 依赖）发出；
  `project_diagnostics` 说明文案同步。
- **rename 解锁**：`DependentProjects` 全为 F# 时可放开 #517 的拒绝分支 —— 依赖项目文档走
  同一套切片规则；含 C#/VB 依赖继续拒绝（跨语言 rename 不在范围）。
- **推迟项**：`.fsi` 签名文件；从 fsproj/slnx 自动构造 options（本 spike 手工组）；
  `PEReference`/`ILModuleReference` 覆盖 C# 依赖的可能性；provided/generated 符号的跨项目语义；
  陈旧 dll（与源不一致）下 `IsEffectivelySameAs` 边界；并发 check 的线程安全；类型提供器。
