# ADR-0001: Core 接口采用 LLM 优先设计 + 可验证符号句柄

## 状态

Accepted（2026-08-02）

## 上下文

DotNetMCP 的 Core 模块是 MCP 工具层与 Workspace/Roslyn 适配器之间的唯一接缝，其接口形状是最不可逆的架构决策。最终消费者是 AI 大模型（经 MCP 工具层）。约束见 Issue #1：P0 C# + 源生成器归因；P1 Avalonia XAML（消费 Core 符号模型）；P2 VB.NET；P3 F#（FCS 独立栈）/COM/dynamic（符号可能无源码声明）；纯读侧起步；~150 项目性能基准；测试接缝 = MCP 工具边界（AdhocWorkspace fixture）。

通过 Design-It-Twice 流程并行产生了 4 个截然不同的候选设计。

## 候选方案

| 方案 | 形状 | 淘汰原因 |
|------|------|---------|
| A. 极小查询接口 | 3 方法 + 多态 QueryRequest，`QueryResult.Data: object` | 类型安全丢失；接口面积只是转移到了请求类型发现性上，深度是假的 |
| B. 直通 Roslyn | ~15 方法直接返回 ISymbol/Compilation | ISymbol 绑定 Solution 快照，FSW 刷新后跨调用失效；序列化/分页/归因在 N 个 MCP 工具中重复（违反局部性）；F# 用 `object?` 硬塞（假接缝）；partial 归因按成员名字典（重载撞键）；依赖 `SourceGeneratedDocument.GeneratorIdentity` 公开性（存疑，实为 internal） |
| C. LLM 优先 | ~15 方法，SymbolHandle(FQN+checksum)、PagedResult+游标、SymbolAttribution 一等公民、ILanguageAdapter 接缝 | **采纳为基础** |
| D. LSP 范式 | 位置导向请求族 + 文档生命周期 | 位置模型为编辑器光标设计，AI 手里是符号名/FQN 而非光标；OpenDocument/UpdateDocument 对纯读侧是死重 |

## 决策

以 **C（LLM 优先）** 为基础，吸收：

- **来自 D**：源生成文档作为虚拟文档纳入统一文档视图（归因是文档的属性而非外挂）；位置解析仅作为获取句柄的入口之一（`ResolveSymbolAtPositionAsync`），不作为主导航模型。
- **来自 B**：Core 内部实现紧贴 Roslyn 对象模型，不做无谓转译；生成器身份识别以 `GeneratorDriver.GetRunResult()`（公开 API，含逐生成器 GeneratedSources）为主、生成文档虚拟路径 `{程序集}/{生成器类型}/{HintName}` 解析为辅。

核心要素：

1. **SymbolHandle**：`{language}:{FQN}#{checksum}`，checksum = SHA256(FQN|language|projectId) 前 8 位。跨调用稳定、可校验，AI 编造句柄会被拒绝并收到 `SuggestedAction` 引导。
2. **SymbolAttribution 一等公民**：每个符号结果携带归因（Handwritten / SourceGenerated(generatorName) / MetadataGenerated / Unknown）；`MetadataGenerated` 为 P3 COM/dynamic 预留；partial 类型逐成员归因（键需含签名以区分重载——对方案 C 原稿的修正）。
3. **摘要→详情逐级展开**：`GetSymbolSummaryAsync` 默认轻量，`GetSymbolDetailAsync(options)` 按需展开（成员/方法体/文档/基类型）。
4. **分页游标**：`PagedResult<T>` + `PaginationToken`（服务端分页缓存 TTL，文件变更即失效）。
5. **ILanguageAdapter 接缝**：今日仅 Roslyn 适配器（C#/VB）；F#（FCS）为 P3 第二适配器。XAML 层是 Core 的调用者，不是适配器。

## 后果

- MCP 工具层保持薄：参数校验 + 句柄透传 + DTO 序列化，不含符号逻辑。
- 接口方法数（~15）多于方案 A，但每个方法职责单一、返回类型扁平，AI 可发现性好。
- 句柄带 FQN+checksum 有 token 代价，换取防幻觉校验，判定值得。
- 实现风险：生成器身份识别的公开 API 边界（GeneratorDriver 路线 vs 路径解析）需在 P0 原型中验证。

## 模块分解（随之确定）

```
DotNetMcp.Host       — MCP SDK 宿主：stdio、工具注册（薄）
DotNetMcp.Tools      — MCP 工具表面：校验、分页、DTO（薄适配器）
DotNetMcp.Core       — 统一符号模型 + 查询服务（本 ADR 的接口；深模块）
DotNetMcp.Workspace  — 加载/缓存/FSW/生成器物化（Core 背后的 Roslyn 适配器）
DotNetMcp.Xaml       — XAML 语义分析，框架可插拔（Avalonia 首发，Core 的调用者）
DotNetMcp.FSharp     — P3，FCS 栈（ILanguageAdapter 第二适配器）
```
