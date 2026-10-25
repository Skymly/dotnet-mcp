# ADR-0006: XAML 使用点索引作为 Core 接缝

## 状态

Accepted（2026-10-25）

## 上下文

XAML 侧已经能解析 `x:Class` / `x:Name` / `x:DataType` / binding path（`XamlDocumentService`），但每次都只对单点问题做一次性的「读文档 → 找符号」。`find_references` 和 `symbol_resolve_at` 要消费 XAML 使用点时，需要一张按项目扫描出来的 usage 清单：位置四元组 + 目标符号句柄 + 解析强度。若由每个工具各自复扫 XAML，namespace 规则与 DataContext 规则会在多处漂移。

XAML 仍是「Core 内层 API 的调用者」，不是 `ILanguageAdapter`（ADR-0001 §5）。本决策只建立接缝、索引、缓存与测试；消费侧随后分 chunk 落地——`symbol_find_references` 把 Exact 使用点并入同一分页流（cursor stage 1），`symbol_resolve_at` 对 `.axaml`/`.xaml` 位置经 `FindAtPositionAsync` 反查覆盖使用点。

## 决策

1. **Core 拥有抽象 `IXamlUsageSource`**（`src/DotNetMcp.Core/IXamlUsageSource.cs`）：`GetUsagesAsync(session, projectId)` 返回一个项目内全部注册 XAML 文档的使用点，`FindAtPositionAsync` 做位置反查，`IsXamlDocumentPath` 做扩展名门槛。`XamlUsagePoint` 携带 `FilePath` / `Start` / `Length` / 位置四元组 / `ProjectId` / `Kind` / `Confidence` / `SymbolKey` / `Handle` / `Snippet`。`SymbolKey` 与 `Handle` 内嵌签名同为 `SignatureQualified` 字符串，构造泛型符号归一化为 `OriginalDefinition`。

2. **`DotNetMcp.Xaml` 实现 `XamlUsageIndex`**：只扫项目 `AdditionalDocuments` 中已注册框架的文档（`.axaml` 即 Avalonia；`.xaml` 需根默认 xmlns 为 MAUI URI），element / attribute / binding 的解析复用 `XamlDocumentService` 的 xmlns、`x:DataType`、`x:Class`、静态 DataContext 规则，不复制一份命名空间逻辑。能经类型上下文解析的使用点标 `Exact`；只有名字匹配（无 `x:DataType`、`ElementName` / `RelativeSource` / `$parent` 场景）的 binding 段标 `Unverified`。解析不出目标符号的构造不出使用点。

3. **缓存按 `(Epoch, ProjectId)`，host 拥有**。`XamlUsageIndexCache` 仿 `FindHitCache`：同 epoch 替换、有界、线程安全；`IWorkspaceSessionCaches` 增 `XamlUsages`；`WorkspaceSession` / `WorkspaceHost`（含 warm session）共享同一个 cache，`AdvanceEpochUnlocked` 清空。索引实现通过 `session as IWorkspaceSessionCaches` soft-cast 取缓存——`IWorkspaceSession` 合同不变，不带缓存的 session 照常工作，只是每次重建。

4. **`DotNetMcp.Server` 接线。** `ServerHost.AddDotNetMcp` 把 `XamlUsageIndex` 以 `IXamlUsageSource` 注册为 singleton；消费侧经会话 `IXamlUsageSourceProvider` soft-cast 取用（`symbol_find_references`、`symbol_resolve_at`），不经 `LanguageAdapters` 分流。未接线的 Core 行为与 4.0 完全一致。

5. **细化 ADR-0001 §3 而非推翻**：使用点索引不是 adapter 上的新方法，是 Core 定义、Xaml 项目实现的可选接缝；依赖方向仍是 Xaml → Core。分页仍归 Core（ADR-0001 §4），索引返回完整 `IReadOnlyList`，分页游标留给消费侧。

## 考虑过的选项

| 选项 | 淘汰原因 |
|------|---------|
| XAML 做成第三个 `ILanguageAdapter` | 违反 ADR-0001 §5：XAML 是 Core 内层 API 调用者；adapter 接缝按 `SymbolHandle.Language` 分流，XAML 使用点没有独立语言句柄族 |
| 每个工具各自现扫 XAML | xmlns / DataContext 规则多份复制，必然漂移 |
| usage 挂在 `ILanguageAdapter` 上 | Roslyn/FCS adapter 不该懂 XAML；接口面积扩大且消费侧拿不到 seam |
| 缓存进 `IWorkspaceSession` | 合同膨胀；FakeSession 这类轻实现被迫认识 cache |
| usage 记 `ISymbol` 而不是句柄 | 跨 Epoch 不可靠，违反 ADR-0001 句柄可验证原则 |

## 后果

- 使用点语义（kind、confidence、位置四元组）在 `XamlUsageIndexTests` 锁定；消费侧只做 merge / dispatch——`symbol_find_references` 并入分页流（`XamlUsageReferenceTests`），`symbol_resolve_at` 反查使用点后复用同一「句柄→summary」路径，XAML 使用点恒为 `resolvedFrom=Reference`（`XamlResolveAtTests`）。
- `IWorkspaceSessionCaches` 增一员；`IWorkspaceSession` 不变。
- 未接线时 Core 与 4.0 行为一致，无回归面。

## 相关决策

- ADR-0001：细化 §3（XAML 不是 adapter、Xaml → Core 依赖方向）、§4（分页归 Core）；§5 adapter 决定不变
- ADR-0002：缓存随 host Epoch 推进失效，`(Epoch, ProjectId)` 键
- ADR-0005：Workspace Edit 模式无关；本接缝只服务读侧
