# ADR-0001: Core 接口采用 LLM 优先设计 + 可验证符号句柄

## 状态

Accepted（2026-08-02），**Amended（2026-08-02 Amendment 1；2026-08-07 Amendment 2 / Spike S1；2026-08-21 Amendment 3；2026-08-21 Amendment 4）** —— 决策方向不变，但原稿的句柄格式、归因模型、模块分解与生成器归因技术路线均被修正；S1 实证细化了 FilePath 启发式与 Adhoc/反射取舍。Amendment 3 兑现 §5 的 ILanguageAdapter 接缝。Amendment 4 把 MCP tool envelope 收成一处。以「决策」小节的现行内容为准。

## 上下文

DotNetMCP 的 Core 模块是 MCP 工具层与 Workspace/Roslyn 适配器之间的主接缝，其接口形状是最不可逆的架构决策。最终消费者是 AI 大模型（经 MCP 工具层）。约束见 Issue #1：P0 C# + 源生成器归因；P1 Avalonia XAML（消费 Core 符号模型）；P2 VB.NET；P3 F#（FCS 独立栈）/COM/dynamic（符号可能无源码声明）；纯读侧起步；~150 项目性能基准。

通过 Design-It-Twice 流程并行产生了 4 个截然不同的候选设计。

## 候选方案

| 方案 | 形状 | 淘汰原因 |
|------|------|---------|
| A. 极小查询接口 | 3 方法 + 多态 QueryRequest，`QueryResult.Data: object` | 类型安全丢失；接口面积只是转移到了请求类型发现性上，深度是假的 |
| B. 直通 Roslyn | ~15 方法直接返回 ISymbol/Compilation | `ISymbol` 绑定 Solution 快照，跨 MCP 调用不可靠；序列化/分页/归因在 N 个 MCP 工具中重复（违反局部性）；F# 用 `object?` 硬塞（假接缝） |
| C. LLM 优先 | ~15 方法，SymbolHandle、PagedResult+游标、SymbolAttribution 一等公民、ILanguageAdapter 接缝 | **采纳为基础** |
| D. LSP 范式 | 位置导向请求族 + 文档生命周期 | 位置模型为编辑器光标设计，AI 手里是符号名/FQN 而非光标；OpenDocument/UpdateDocument 对纯读侧是死重 |

## 决策

以 **C（LLM 优先）** 为基础，吸收 D 的「生成文档纳入统一文档视图」与 B 的「内部紧贴 Roslyn 对象模型，不做无谓转译」。

### 1. 符号句柄（SymbolHandle）

格式：`{language}:{projectId}:{signatureQualifiedName}#{checksum}`

- **必须含 projectId**：checksum 的输入包含 projectId，若句柄串本身不含它，校验就需要先在全解决方案搜 FQN 才能算出 checksum——校验将不提供任何新信息（自我指涉，原稿缺陷）。
- **必须含签名**：仅 FQN 无法区分重载。采用签名限定名（含参数类型列表）。
- **不含 epoch**：句柄的价值正是语义上跨编辑稳定；把 workspace 代次塞进句柄会使用户每次保存都令 AI 手中全部句柄失效，与「跨调用稳定」自相矛盾。代次只绑定分页游标（见 §4）。
- checksum = 对上述全部字段的哈希前若干位，作用是拒绝 AI 编造的句柄，失败时返回带 `SuggestedAction` 的错误引导重新解析。
- 句柄解析失败分两类且必须可区分：**格式/校验失败**（编造）与**符号已不存在**（代码被改动）。

### 2. 归因模型（两轴，取代原稿的单枚举）

原稿的单一枚举把「COM 元数据符号」「`dynamic` 调用点」「生成器产物」混为一类，到 P3 必然失效。改为两个正交轴：

- **声明可得性**：`InSource` / `InMetadata` / `None`
- **来源**：`Handwritten` / `SourceGenerator(identity)`，其中 identity = 生成器程序集名 + 类型全名 + 版本

`dynamic` **不属于符号归因**——它是调用点/`IOperation` 层面的性质，P3 时单独建模，不塞进 SymbolAttribution。

partial 类型逐成员归因，字典键须含签名以区分重载。

### 3. Core 分两层（取代原稿「Core 是读侧唯一入口」的笼统表述）

- **内层：符号级 API**（`internal` / `InternalsVisibleTo`）——返回 Roslyn 符号对象，供**进程内**消费者使用，首要客户是 XAML 层（解析 `Binding Path` 需沿 `ITypeSymbol` 走属性类型链）。
- **外层：DTO facade**——LLM 优先的字符串句柄/摘要/分页表面，供 MCP 工具层使用。

原稿只有外层，会迫使 XAML 层为每个绑定路径段做一次「句柄→成员列表→再解析」的 N+1 往返，穿越一层为 token 优化的字符串 API。精确表述应是：**Core 的 DTO facade 是 MCP 工具的唯一入口**，而非一切消费者的唯一入口。

### 4. 摘要→详情逐级展开 + 分页

`GetSymbolSummaryAsync` 默认轻量；`GetSymbolDetailAsync(options)` 按需展开（成员/方法体/文档/基类型）。

**分页完全归 Core**（原稿在「后果」里又说 Tools 负责分页，属重复归属）。`PagedResult<T>` + 分页游标，游标**必须携带 workspace 代次**（一页结果本质是一个快照的切片，代次推进即失效），配 TTL。

### 5. ILanguageAdapter 接缝

今日仅 Roslyn 适配器（C#/VB）；F#（FCS）为 P3 第二适配器。XAML 层是 Core 内层 API 的调用者，不是适配器。`SymbolHandle.Language`（或 project language）选一次；Core query module 不各自 `if (fsharp:)`。

### 6. 生成器归因技术路线（重大修正，见 Amendment 1 / F1、F2）

- 生成器身份**没有公开 API**：`SourceGeneratedDocument` 公开面只有 `HintName`，`SourceGeneratedDocumentIdentity`（含 `SourceGeneratorIdentity`）是 `internal`（dotnet/roslyn#50546，已关为 speculative）。
- 原稿的「虚拟路径解析为辅」**不成立**：`GeneratedFiles/{GeneratorAssemblyName}` 是编译器 CLI 落盘格式，不是 workspace `Document.FilePath` 的契约；且官方文档明示 hint 只是 hint，编译器可加前后缀，**HintName 跨生成器不保证唯一**，故「按 HintName 关联回生成器」不可靠。
- 唯一全公开路径：`project.AnalyzerReferences` → `AnalyzerReference.GetGenerators(language)`（public）→ `GeneratorExtensions.GetGeneratorType()`（public）→ 自建 `CSharpGeneratorDriver` → `GetRunResult()`（public，逐生成器 `GeneratedSources` 含 HintName/SyntaxTree/诊断）。
- **陷阱**：`Project.GetCompilationAsync()` 已包含 workspace 自己跑出的生成树，直接在其上再跑 driver 会造成重复定义。必须先剔除生成树得到 base compilation 再跑，且需与 workspace 的生成文档对账。
- 该路线的可行性已由 **Spike S1** 确认（见 Amendment 2）；`Document.FilePath` 在 MSBuildWorkspace 下常编码生成器身份但是启发式而非契约；反射 internal Identity 可选作加速并须守护测试。

## 后果

- MCP 工具层是**薄适配器**（不宣称深度）：参数校验 + 句柄透传 + 序列化；存在理由是隔离 MCP SDK 依赖，使 Core 可独立测试、可被 XAML 层复用。分页与符号逻辑均不在其中。
- 接口方法数（~15）多于方案 A，但每个方法职责单一、返回类型扁平，AI 可发现性好。
- 句柄含 projectId + 签名 + checksum，token 代价高于原稿，换取可独立校验与重载可辨，判定值得。
- 生成器归因需自行驱动生成器 → 生成器实际运行两遍（workspace 一遍、我们一遍），存在结果分叉风险，须由 S1 量化。

## 模块分解（修订）

```
DotNetMcp.Server      — MCP 宿主 + 工具表面（原 Host + Tools 合并；薄适配器）
DotNetMcp.Core        — 符号模型与查询服务：内层符号 API + 外层 DTO facade（深模块）
DotNetMcp.Workspace   — 加载/缓存/FSW/生成器物化（Roslyn 适配器，见 ADR-0002）
DotNetMcp.Xaml        — XAML 语义分析，框架可插拔（Avalonia 首发；Core 内层 API 的调用者）
DotNetMcp.FSharp      — P3，FCS 栈（ILanguageAdapter 第二适配器）
```

原稿的 Host / Tools 分立过不了删除测试（Core 方法与 MCP 工具近 1:1，Tools 成纯透传），对 v0.1 只读服务器属过度结构化，故合并。

## 相关决策

- ADR-0002：Workspace 层接口
- ADR-0003：长耗时操作、会话、并发与取消模型（原稿完全缺失）
- ADR-0004：安全与路径策略（原稿完全缺失）

---

## Amendment 1（2026-08-02）：原稿被推翻的断言与证据

本 ADR 原稿在 spec 之前经过一轮事实核查与设计复审，以下断言被推翻或削弱，决策小节已相应修正。保留此记录以说明修改动因。

| # | 原稿断言 | 核查结论 | 证据 |
|---|---------|---------|------|
| F1 | 生成器身份可经「生成文档虚拟路径 `{程序集}/{生成器类型}/{HintName}`」解析（作为辅助路径） | **不成立**。`SourceGeneratedDocumentIdentity` / `SourceGeneratorIdentity` 均为 internal；`GeneratedFiles/{GeneratorAssemblyName}` 是编译器 CLI 落盘格式而非 workspace `Document.FilePath` 契约；官方文档明示 HintName 跨生成器可被编译器加前后缀区分，故不保证唯一 | roslyn `SourceGeneratedDocument.cs`（`// TODO: make this public` + #50546）、`SourceGeneratedDocumentIdentity.cs`、`docs/features/source-generators.md` |
| F2 | 用 `GeneratorDriver.GetRunResult()` 归因是干净的公开路径 | **部分成立**：API 确实公开，但意味着生成器要跑第二遍——workspace 内部已运行生成器且 `Project.GetCompilationAsync()` 已含生成树，在其上再跑 driver 会重复定义；须先剔除生成树 | roslyn `Workspace_SourceGeneration.cs`、`GeneratorDriver.GetRunResult` 源码 |
| B1 | 句柄 `{language}:{FQN}#{checksum}`，checksum = SHA256(FQN\|language\|projectId) 可防幻觉 | **设计缺陷**：projectId 不在句柄串内 → 校验自我指涉；FQN 不含签名 → 重载不可辨 | 见 §1 |
| B2 | 句柄可携带 workspace 代次以拒绝过期句柄（ADR-0002 表述） | **设计缺陷**：会导致每次保存令全部句柄失效，与「跨调用稳定」矛盾；且与本 ADR 原稿句柄格式（无代次字段）互相冲突。代次改绑分页游标 | 见 §1、§4 |
| B4 | 归因单枚举含 `MetadataGenerated`，为 COM/dynamic 预留 | **建模错误**：混同「元数据声明」「动态调用点」「生成器产物」；`dynamic` 非符号性质。改两轴 | 见 §2 |
| B5 | Core 是读侧唯一入口，XAML 层是其调用者 | **形状错误**：LLM 优先的字符串 DTO 面不适合进程内类型遍历，XAML 绑定解析会被迫 N+1。改 Core 内外两层 | 见 §3 |
| B6 | 模块分解为 Host / Tools / Core / Workspace / Xaml / FSharp 六项 | Tools 过不了删除测试（与 Core 近 1:1 透传）；分页归属重复声明。合并为 Server | 见「模块分解」 |

---

## Amendment 2（2026-08-07）：Spike S1 对 §6 / F1 的实证细化

证据：`spikes/s1-generator-attribution/`（Roslyn 5.6.0，9/9 验证测试通过）。**不推翻** §6「公开归因须自建 driver」与 F1「HintName 非跨生成器唯一 / Identity 仍 internal」。

| # | 原表述 | S1 观测 | 对实现的影响 |
|---|--------|---------|--------------|
| S1-F1 | `GeneratedFiles/{GeneratorAssemblyName}` 不是 workspace `Document.FilePath` 契约 | **部分细化**：MSBuildWorkspace 下 FilePath *当前*形如 `obj/.../generated/{Assembly}/{TypeFullName}/{HintName}`（Windows `\`）；AdhocWorkspace 则为无 `obj` 前缀的 `{Assembly}\{Type}\{HintName}`。布局**经常**编码生成器身份，但跨 workspace 不一致，仍**不是**可依赖契约 | 允许作启发式加速；真相仍以 driver 内容对账或反射 Identity 为准 |
| S1-F2 | 须剔除生成树再跑 driver | **确认**：生成文档树与 `compilation.SyntaxTrees` 引用相等；`RemoveSyntaxTrees` 可得干净 base；SampleApp 上 driver 与 workspace 文档 **10/10 内容对账** | ADR-0002 的 `GetCompilationWithoutGeneratedTreesAsync` / `GetGeneratorRunResultAsync` 形状成立 |
| S1-F3 | HintName 关联不可靠 | **确认**：两生成器同 HintName 时 driver/workspace 均保留双份；仅 HintName 无法区分 | 归因键必须含生成器身份 |
| S1-F4 | AdhocWorkspace 生成行为待验 | **可用**：Adhoc + `AnalyzerReferences` 会跑生成器，`GetSourceGeneratedDocumentsAsync` 有结果 | 单测可用 Adhoc；保留纯 driver 后备 |
| S1-F5 | 反射 Identity 取舍未决 | **可行但私有**：`Identity` 非公开；可读 Assembly/Type/Version；须守护测试 | 主路径用公开 driver；反射可选加速，文档化私有 API 风险 |

决策不变：主路径 = strip + 自建 driver + 内容对账；FilePath / 反射为辅助。细节与耗时见 spike `CONCLUSIONS.md`。


## Amendment 3（2026-08-21）：兑现 ILanguageAdapter

§5 名称与两个 adapter 的决定不变。代码长出 `ILanguageAdapter`：`LanguageAdapters` 按 `SymbolHandle.Language` / project language 选一次；Roslyn adapter（C#/VB）与 FCS adapter（F#）是两个真实 adapter。XAML 仍不是 adapter。F# 仍从 `WorkspaceSession` 快照读项目（移出 session 是后续项）。

## Amendment 4（2026-08-21）：收拢 MCP tool envelope

「后果」里 Tools 是薄适配器（参数校验 + 句柄透传 + 序列化）的决定不变。ready session 与 JSON 信封（`TryGetReadySession` / `OkResult` / `ErrorResult` / `ToPolicyError`）收成 `McpToolEnvelope` 一处；六个 tool class 只留 MCP 工具名与参数映射。不把 tool 并进 Core。
