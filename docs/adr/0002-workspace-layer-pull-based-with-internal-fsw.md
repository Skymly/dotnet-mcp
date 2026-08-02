# ADR-0002: Workspace 层采用拉取式接口 + 请求级快照

## 状态

Accepted（2026-08-02），**Amended（2026-08-02，见文末 Amendment 1）** —— 拉取式方向不变，但接口签名、新鲜度语义、快照一致性与解决方案格式支持均被修正。以「决策」小节的现行内容为准。

## 上下文

Workspace 层是 Core（ADR-0001）背后的模块，职责：加载解决方案（MSBuildWorkspace + MSBuildLocator）、多 TFM、生命周期与缓存、新鲜度保证、源生成器物化、~150 项目性能与内存控制（Issue #1）。需内部测试接缝使单测可用 AdhocWorkspace fixture 替换 MSBuildWorkspace。通过 Design-It-Twice 产生 4 个候选。

## 候选方案

| 方案 | 形状 | 结论 |
|------|------|------|
| A. 极简拉取式 | 5 方法，读时校验新鲜度，FSW 仅作失效标记 | **采纳为基础**（但 FSW 定位与快照语义被修正） |
| B. 事件驱动 | `WorkspaceChanged` 事件 + 代次快照 + CheckDrift/ForceSync | 事件在 MCP 请求-响应模型下两次调用之间无人消费；但其 FSW 防抖/批量合并/漂移检测被吸收为实现机制 |
| C. 显式快照+资源管理 | epoch 快照 + 句柄引用计数 + 调用者可见内存预算 | 引用计数与内存预算暴露给 Core 是浅模块气味；**但快照本身被吸收**（原稿误将其一并舍弃，见 Amendment F6） |
| D. 持久化索引（SQLite） | 索引落盘、二次启动近乎即时 | 索引过期=错误答案，对 v0 读侧产品不可接受；实现面过大。v1.x 重估 |

## 决策

以 **A（极简拉取式）** 为基础，接口修订为：

```csharp
public interface IWorkspaceProvider : IAsyncDisposable
{
    // 打开/取得工作区会话；加载是长耗时操作，其对外形态见 ADR-0003
    Task<IWorkspaceSession> OpenAsync(string solutionOrProjectPath, CancellationToken ct = default);
}

/// 一次 MCP 请求取得一个会话快照；同一请求内的所有查询都基于同一代次，保证结果自洽
public interface IWorkspaceSession : IDisposable
{
    long Epoch { get; }
    Solution Solution { get; }

    /// 惰性编译；返回的 compilation 已包含 workspace 自身运行的源生成树
    Task<Compilation> GetCompilationAsync(ProjectId projectId, CancellationToken ct = default);

    /// 剔除源生成树后的 compilation，供自建 GeneratorDriver 归因使用（见 ADR-0001 §6）
    Task<Compilation> GetCompilationWithoutGeneratedTreesAsync(ProjectId projectId, CancellationToken ct = default);

    /// 逐生成器归因结果（自建 driver），按 (projectId, epoch) 缓存
    Task<GeneratorDriverRunResult> GetGeneratorRunResultAsync(ProjectId projectId, CancellationToken ct = default);
}
```

关键语义：

1. **一次请求一个快照（吸收 C）**：C# MCP SDK 的请求分发是并发 fire-and-forget，且拉取式在一次逻辑请求内会多次访问 workspace。若不固定快照，一次请求可能跨越一次 reload、混用两个代次的数据（例如引用位置指向已不存在的行号）。故 Core 在请求开始处取得 `IWorkspaceSession`，请求内所有查询走同一会话。**仅暴露快照与代次，不暴露引用计数/内存预算/显式淘汰**（C 的浅模块部分不采纳）。

2. **`ProjectId` 已隐含 TFM，接口不接受 targetFramework 参数**：MSBuildWorkspace 对多 TFM 项目为每个 TFM 创建独立 Project/ProjectId（`Project.Name` 形如 `Foo(net9.0)`）。原稿的 `string? targetFramework` 参数冗余且会在与 ProjectId 不符时产生无定义行为。

3. **FSW 是必需机制，不是可选优化**：MSBuildWorkspace 不会自动感知磁盘文本变更；而仅校验 `.sln`/`.csproj` 的 mtime **检测不到 `.cs` 源文件编辑**——那恰是最常见场景。因此：
   - FSW（防抖 + 自写抑制 + 批量合并 git checkout 风暴）监视源文件与项目文件，是新鲜度的主机制；
   - 变更必须显式回填进 workspace（源文件文本 → `Solution.WithDocumentText`；项目文件/引用变化 → 重载项目），此回填语义属本层职责；
   - mtime/内容哈希校验降级为**漂移兜底**（FSW 漏检时的兜底），并暴露为 MCP 诊断工具（check-drift）。
   - 每次变更推进代次（epoch）。代次用于失效分页游标（ADR-0001 §4），**不用于失效符号句柄**。

4. **生成器归因（配合 ADR-0001 §6）**：`GetCompilationAsync` 返回的 compilation 已含 workspace 运行的生成树；自建 driver 必须用 `GetCompilationWithoutGeneratedTreesAsync`，否则生成类型重复定义。两套生成结果需对账（HintName 非跨生成器唯一，见 ADR-0001）。归因结果按 `(projectId, epoch)` 缓存。技术路线由 Spike S1 定论。

5. **解决方案格式支持（修正）**：
   - `.sln`：支持。
   - `.slnx`：需 **Roslyn 5.0+**（PR #77326；4.12 明确不支持）并依赖 `Microsoft.VisualStudio.SolutionPersistence`。**这是硬约束**——性能基准方案（Observables，145 项目）本身是 `.slnx`。由 Spike S2 验证 preview 依赖的可接受性。
   - `.slnf`：公开 API **不支持**（issue #73105）。要支持须自行解析过滤器并逐项目加载；v0 明确记为不支持。

6. **引用查找作用域**：`SymbolFinder.FindReferencesAsync(symbol, solution)` 不限定 `documents` 时扫描全解决方案。150 项目对上默认 50 项目的 Compilation LRU 会抖动（每次全局查找触发重编译风暴）。故默认作用域为项目/依赖闭包，全解决方案需显式 opt-in 并提示成本；LRU 上限可配。具体阈值由 Spike S2 实测确定。

7. **测试接缝**：内部工厂接口，生产返回 MSBuildWorkspace 实现，测试返回 AdhocWorkspace fixture 实现。注意 AdhocWorkspace 下源生成器行为需在 S1 一并验证。

8. **F# 预留**：本接口返回 Roslyn 类型；P3 时 F#（FCS）经 Core 的 `ILanguageAdapter` 独立成栈，不经由此接口。

## 后果

- Core 在一次请求内看到自洽且新鲜的状态，无需理解失效/缓存/淘汰。
- 首次加载耗时（150 项目量级）无法通过本层消除，其对外形态必须由 ADR-0003 处理（MCP 客户端 60s 超时）。
- 无事件订阅生命周期，无「忘记退订」误用面。
- 会话对象引入了「必须释放」的轻度约束（`using`），换取一致性；不引入引用计数复杂度。
- `.slnx` 依赖 Roslyn 5.0 preview 是 v0 的既定风险；`.slnf` v0 不支持。
- 持久化索引（方案 D）未被预先兼容，v1.x 若要引入需重新评估接口——判定可接受。

## 相关决策

- ADR-0001：Core 接口（句柄、归因、分页游标代次）
- ADR-0003：长耗时加载、会话、并发与取消
- ADR-0004：安全与路径策略

---

## Amendment 1（2026-08-02）：原稿被推翻的断言与证据

| # | 原稿断言 | 核查结论 | 证据 |
|---|---------|---------|------|
| F3 | 加载入口兼容 `.sln` / `.slnx` / `.slnf` | **部分不成立**：`.slnx` 需 Roslyn 5.0+ 且需 `Microsoft.VisualStudio.SolutionPersistence`；`.slnf` 公开 API 不支持。且性能基准方案本身是 `.slnx`，构成硬约束 | roslyn PR #77326、issue #78097、issue #73105 |
| F4 | 接口方法带 `string? targetFramework` 以支持多 TFM | **冗余/不连贯**：多 TFM 已由独立 ProjectId 表达（`ProjectMap` 源码注释、issue #56806），参数与 ProjectId 冲突时行为无定义。已删除 | roslyn `ProjectMap.cs` |
| F2 | 「源生成器按需物化」= 调用 `GeneratorDriver.GetRunResult()` | **措辞掩盖了关键事实**：workspace 已自行运行生成器且 `GetCompilationAsync()` 已含生成树，自建 driver 需先剔除生成树，且生成器实际运行两遍。已拆分为两个显式方法 | roslyn `Workspace_SourceGeneration.cs` |
| F6 | 舍弃方案 C 的显式快照（判为浅模块气味） | **过度舍弃**：批评只对「暴露引用计数/内存预算」成立；快照本身解决的一致性问题真实存在（SDK 并发 fire-and-forget + 拉取式多次访问）。已吸收为 `IWorkspaceSession` | csharp-sdk `McpSessionHandler.cs`（`_ = ProcessMessageAsync()`） |
| B3 | 「读时校验 `.sln`/`.csproj` mtime，读到即正确」；FSW 仅作可选的快速失效标记 | **过度声明**：检测不到 `.cs` 源文件编辑（最常见场景），且 MSBuildWorkspace 不自动感知文本变更。FSW 升为必需机制，并补充「变更文本如何回填 workspace」语义 | MSBuildWorkspace 行为；见 §3 |
| B6 | 未考虑引用查找与 LRU 的相互作用 | **遗漏**：全解决方案引用查找需大量 Compilation，与 LRU 50/150 抖动。已补默认作用域策略 | roslyn `SymbolFinder_FindReferences_Current.cs`、issue #34562 |
| — | 未提及长耗时加载在 MCP 下的可行性 | **严重遗漏**：客户端普遍 60s 硬超时。移交 ADR-0003 | 见 ADR-0003 |
