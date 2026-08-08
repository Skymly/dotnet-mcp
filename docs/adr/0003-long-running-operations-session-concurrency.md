# ADR-0003: 长耗时操作、工作区会话、并发与取消

## 状态

Accepted（2026-08-02），**Amended（2026-08-07，Spike S2 回填 §3 软预算推荐值）**，**Amended（2026-08-08，Spike S3 回填客户端超时/Tasks/手工模式实测）**

## 上下文

ADR-0001/0002 原稿均未处理一个产品级阻断问题：**加载 ~150 项目的解决方案需 1-2 分钟，而 MCP 客户端普遍强制 60 秒工具调用超时**。

事实核查结论（证据见文末）：

- MCP 规范**未强制**超时，仅建议客户端实现超时；提案 SEP-1539 建议协议默认 `tools/call` 60s，尚未成为规范。
- 客户端实际行为：Claude Desktop 硬编码 60s（配置文件的 timeout 字段被忽略）；TypeScript SDK `DEFAULT_REQUEST_TIMEOUT_MSEC` = 60s（Cursor 等基于它的客户端同样）；Claude Code CLI 可经 `MCP_TIMEOUT` 延长。
- **进度通知不是 keepalive**：规范只将 `notifications/progress` 定义为信息性；TS SDK 存在「进度不重置超时计时器」的已知缺陷（#245）。故 `IProgress<ProgressNotificationValue>` 无法规避超时。
- MCP 的 ping 在 2026-07-28 协议版本被移除（SEP-2575），活跃性交由传输层与请求级超时。C# SDK 服务端只有 `InitializationTimeout`，**无法延长工具调用超时**。
- 官方长耗时方案是 **Tasks 扩展（SEP-2663，Final 状态）**，C# SDK 提供 `ModelContextProtocol.Extensions.Tasks`（非 experimental，`.WithTasks(store)`），要求客户端协议版本 ≥ 2026-07-28 且显式 opt-in。
- **并发**：stdio 单通道支持并发请求；C# SDK 的分发是 fire-and-forget（`_ = ProcessMessageAsync()`），以 `ConcurrentDictionary<RequestId, CancellationTokenSource>` 跟踪在途请求。多数客户端目前串行发送，但不可依赖。
- **取消**：客户端发 `notifications/cancelled`，C# SDK 将其接到 handler 的 `CancellationToken`。

## 决策

### 1. 加载永不阻塞请求（基线：手工异步 + 状态查询）

`workspace_open` **立即返回**（加载在后台进行），返回当前状态；`workspace_status` 供轮询，返回阶段、已加载项目数/总数、警告、预计剩余。所有查询工具在工作区未就绪时返回结构化错误 + `SuggestedAction`（指向 `workspace_status`）。

选择手工模式为基线而非直接依赖 Tasks 扩展，理由：手工模式在**所有**客户端可用（含仅支持旧协议版本者），而 Tasks 需要新协议版本 + 客户端 opt-in。

**Spike S3 锁定的返回形状**（见 `spikes/s3-mcp-long-running/CONCLUSIONS.md` Q4）：`phase` / 进度计数 / `estimatedRemainingMs` / `suggestedAction`。loading 文案须明确「去调 status，不要重试 open」。S2 观测 ~15–20s 加载虽常低于 60s，**仍不得**改为阻塞 `tools/call`——60s 是常见硬顶且无余量。

### 2. Tasks 扩展作为增强（客户端支持时）

启用 `.WithTasks(...)`：当客户端声明支持 `io.modelcontextprotocol/tasks` 时，长耗时工具自动转为后台任务 + `tasks/get` 轮询，取消经 `tasks/cancel` 传播到 `CancellationToken`。基线的手工状态模型与之并存、不冲突。

**Spike S3**：C# SDK 2.1.0 下服务器可协商协议 **2026-07-28** 并广告 `io.modelcontextprotocol/tasks`；`CallToolAsTaskAsync` + `tasks/get` / `tasks/cancel` 端到端通过。未 opt-in 的客户端仍走同步调用并受 60s 约束 → Tasks **不能**取代 §1 基线。

### 3. 每个工具都有时间预算，超预算返回部分结果

不止加载会超时：全解决方案引用查找、大项目首次编译同样可能超过 60s。因此**所有工具遵守软性时间预算**（默认远低于 60s，留出余量，可配）：达到预算时返回**已得到的部分结果 + 分页游标 + 明确的截断说明**，而不是继续阻塞。这与 ADR-0001 的分页游标机制共用一套输出形状，对 AI 消费也更友好（宁可给一页真结果，不要给一次超时）。

**Spike S2 推荐默认软预算**（观测基线：`Observables.slnx` 热加载 ~17–19 s；单项目编译 p95 ≪ 1 s；全解决方案单符号 Find Refs ~0.1 s —— 见 `spikes/s2-scale/CONCLUSIONS.md`）：

| 工具类别 | 推荐软预算 | 备注 |
|----------|------------|------|
| `workspace_open` | **非阻塞**（§1） | 墙钟 ~20 s；不可占用一次 tools/call |
| 单项目编译 / 跳定义 | **5 s** | 观测 p95 ~0.2 s |
| 作用域内 Find References | **5 s** | 默认依赖闭包（ADR-0002 §6） |
| 全解决方案 Find References | **20 s** | opt-in；截断 + 游标 |
| 批量诊断（多项目） | **15 s** | 部分结果 + 游标 |

**Spike S3 客户端矩阵摘要**（详情见 `spikes/s3-mcp-long-running/CONCLUSIONS.md` Q8）：stdio C# 探针复现 **60s** 取消（90s sleep → ~60012ms `TaskCanceledException`）；进度报告不能延长该超时（59 次 progress 仍 60s 死）；Claude Code 2.1.224 已记录版本（交互式/`MCP_TIMEOUT` 需登录环境复测）；Desktop/TS 系默认仍按 60s + 进度非 keepalive 规划。软预算默认值保持本表（S2），S3 确认「部分结果 + nextCursor」续页形状可用。

### 4. 单一活动工作区（v0）

服务器进程同时只有一个活动工作区：`workspace_open` 设置它，其余工具不需要传 workspaceId——减少 AI 出错面（不必记住/编造 id）。多工作区并存推迟到有真实需求时再设计。

### 5. 并发与快照

- **加载互斥**：以 `SemaphoreSlim` 保护加载/重载，避免并发加载同一工作区（先行实践参见 darylmcd-Roslyn-Backed-MCP 的 load lock）。
- **请求级快照**：每个 MCP 请求在入口取得 `IWorkspaceSession`（ADR-0002 §1），请求内所有查询共用同一代次，避免跨代次拼接结果。
- 并发查询之间互不阻塞（Roslyn `Solution` 不可变，可安全并读）。

### 6. 取消

所有工具签名接收 `CancellationToken` 并逐层传递给 Roslyn 与 I/O；协作式取消，不做强杀。取消后不发送该请求的响应（符合规范）。

**Spike S3**：显式 `notifications/cancelled`（带正确 `RequestId`）与 `tasks/cancel` **可靠**触发工具 token。C# 客户端仅取消 `CallToolAsync` 的 `CancellationToken` 时，SDK 以 fire-and-forget 发送 cancelled，**存在竞态**——产品路径勿假设「CTS 取消 ⇒ 服务端一定收到」。

### 7. 进度通知

仍然发送（`IProgress<ProgressNotificationValue>`）用于 UI 反馈与可观测性，但**架构上不依赖它规避超时**。

**Spike S3 复证**：60s 窗口内持续 progress 仍超时；TS SDK `resetTimeoutOnProgress` 默认 false。

## 后果

- 加载 API 形状被此决策固定：`workspace_open` + `workspace_status` 两件套，`open` 是非阻塞语义。这必须在 spec 中体现。
- 所有列表型工具必须实现部分结果 + 游标（与 ADR-0001 分页机制统一）。
- 需要一个"就绪度"概念贯穿工具层：未就绪时的错误必须可操作（告诉 AI 去轮询而不是重试）。
- 文档需声明客户端兼容矩阵（哪些客户端可享 Tasks 扩展、哪些走手工轮询）。
- **Spike S3** 已实测并回填：Tasks 端到端、stdio 60s 超时、进度非 keepalive、手工模式形状、并发重叠、取消可靠性差异；软预算默认值仍以 **Spike S2** §3 表为准。

## 证据

- MCP 规范：progress（informational）、cancellation（`notifications/cancelled`）、stdio 传输单通道
- SEP-1539（超时协调，提案中）、SEP-2575（移除 ping）、SEP-2663（Tasks 扩展，Final）
- csharp-sdk：`McpServerOptions.InitializationTimeout`、`McpSessionHandler.cs`（fire-and-forget 分发、`_handlingRequests`、取消通知处理）、`TokenProgress.cs`、`ModelContextProtocol.Extensions.Tasks`
- 客户端行为：Claude Desktop / TypeScript SDK 60s 硬超时；TS SDK `resetTimeoutOnProgress` 默认 false（历史缺陷见 #245）
- Spike 实测：`spikes/s3-mcp-long-running/CONCLUSIONS.md` + `data/timeout-60.json` / `progress-60.json` / `server-caps.txt`

## 相关决策

- ADR-0001：分页游标（部分结果的输出形状）
- ADR-0002：`IWorkspaceSession` 快照、加载耗时来源
- ADR-0004：安全与路径策略（`workspace_open` 接受哪些路径）
