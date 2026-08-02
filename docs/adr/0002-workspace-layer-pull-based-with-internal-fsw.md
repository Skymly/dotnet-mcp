# ADR-0002: Workspace 层采用拉取式接口 + 内部 FSW/epoch

## 状态

Accepted（2026-08-02）

## 上下文

Workspace 层是 Core（ADR-0001）背后的模块，职责：加载 .sln/.slnx/.slnf（MSBuildWorkspace + MSBuildLocator）、多 TFM、生命周期与缓存、新鲜度保证、源生成器按需物化、~150 项目性能与内存控制（Issue #1）。需内部测试接缝使单测可用 AdhocWorkspace fixture 替换 MSBuildWorkspace。通过 Design-It-Twice 产生 4 个候选。

## 候选方案

| 方案 | 形状 | 淘汰原因 |
|------|------|---------|
| A. 极简拉取式 | 5 方法 `IWorkspaceProvider`，读时校验新鲜度，FSW 仅作失效标记 | **采纳为基础** |
| B. 事件驱动 | `WorkspaceChanged` 事件 + 代次快照 + CheckDrift/ForceSync | MCP 是请求-响应模型，两次调用之间无人消费事件；推送沦为提前失效优化，该价值可由 A 吸收 |
| C. 显式快照+资源管理 | epoch 快照 + 句柄引用计数 + 调用者可见内存预算 | 内存治理是实现细节，暴露给 Core 是浅模块气味（误用 = 泄漏或 null）；仅吸收 epoch 概念 |
| D. 持久化索引（SQLite） | 索引落盘、二次启动近乎即时、索引优先查询 | 索引过期=错误答案，对 v0 读侧产品不可接受；实现面过大；stdio 长驻进程每次会话只付一次首载成本。v1.x 可在不变接口下重估 |

## 决策

以 **A（极简拉取式）** 为基础：

```csharp
public interface IWorkspaceProvider
{
    Task<Solution> GetSolutionAsync(string solutionPath, CancellationToken ct = default);
    Task<Compilation> GetCompilationAsync(ProjectId projectId, string? targetFramework = null, CancellationToken ct = default);
    Task<GeneratorDriverRunResult> GetGeneratorRunResultAsync(ProjectId projectId, string? targetFramework = null, CancellationToken ct = default);
    Task<SyntaxTree?> ResolveGeneratedDocumentAsync(string virtualPath, ProjectId projectId, string? targetFramework = null, CancellationToken ct = default);
    ValueTask DisposeAsync();
}
```

**吸收 B（作为实现机制，非事件接口）**：FSW 主动失效标记 + 防抖/批量合并（git checkout 风暴）+ 漂移检测（check-drift 能力，暴露为 MCP 诊断工具而非事件）。

**吸收 C（内部化）**：workspace 内部维护代次（epoch），每次检测到变更推进代次；Core 的 SymbolHandle 校验可携带代次，过期句柄被拒并引导重新解析。引用计数/内存预算/`EvictLru` **不暴露**——LRU 淘汰策略与上限是 `WorkspaceProviderOptions` 的配置项，由实现自治。

关键语义：

1. **读时校验**：每次 `GetSolutionAsync` 校验 .sln/.csproj 的 mtime（FSW 标记作快速路径），读到即正确；`FreshnessStrategy`（MTime/Hash/None）可配。
2. **惰性编译**：`GetCompilationAsync` 首次访问才编译，LRU 缓存（默认上限 50 项目，可配）。
3. **生成器按需物化**：`GetGeneratorRunResultAsync` 走 `GeneratorDriver.GetRunResult()`（逐生成器 GeneratedSources + 诊断 + 身份，公开 API），结果按 (project, tfm) 缓存、随项目失效；`ResolveGeneratedDocumentAsync` 解析 `{程序集}/{生成器类型}/{HintName}` 虚拟路径作为补充。
4. **测试接缝**：内部工厂接口，生产返回 MSBuildWorkspace 实现，测试返回 AdhocWorkspace fixture 实现。
5. **F# 预留**：本接口返回 Roslyn 类型；P3 时 F#（FCS）经 Core 的 `ILanguageAdapter` 独立成栈，不经由此接口——届时若需共享加载/缓存机制再重估。

## 后果

- Core 永远读到新鲜状态，无需理解失效/缓存/淘汰；新鲜度复杂性局部化在 Workspace 层。
- 首次加载 ~1-2 分钟（MSBuildWorkspace 固有成本，150 项目），稳态查询毫秒级，编辑后首查亚秒到秒级（仅重编变更项目）。
- 无事件订阅生命周期，无误用面（无法忘记取消订阅）。
- 持久化索引（方案 D）未被预先兼容，v1.x 若要引入需重新评估接口——判定可接受。
