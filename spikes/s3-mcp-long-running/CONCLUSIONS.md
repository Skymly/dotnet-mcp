# Spike S3 结论：MCP 长耗时与客户端超时

验证环境：Windows、.NET SDK **10.0.302**、C# MCP SDK **ModelContextProtocol 2.1.0** + **Extensions.Tasks 2.1.0**、Claude Code CLI **2.1.224**（本机未登录，未能跑通交互式工具调用；stdio/协议层由 C# 客户端探针代替）。原始日志：[`data/`](data/)。测试：`dotnet test` **9/9** 通过；`S3.Harness all` 全绿。

## 总览推荐

| 问题 | 结论 |
|------|------|
| 阻塞式 `tools/call` 加载大方案会不会在用户客户端失败？ | **会（或即将会）**：默认/常见超时 ≈ **60s**；进度通知**不能**当作 keepalive。S2 观测加载 ~15–20s 虽暂低于 60s，但无余量且方案更大时必爆。 |
| v0 加载交互形状 | **确认 ADR-0003 §1**：`workspace_open`（立即返回）+ `workspace_status`（轮询）+ `SuggestedAction` |
| Tasks 扩展 | **可作为增强路径**（协议 **2026-07-28** + 客户端 opt-in）；**不能**替代手工基线 |
| 软预算 + 游标 | **可行**；文案须明确「带 nextCursor 续页，勿从头重试」 |

---

## 逐题结论

### Q1 — 超时实测

| 客户端 / 探针 | 版本 | 观测 |
|---------------|------|------|
| C# MCP Client via **stdio**（本 spike 主探针） | MCP 2.1.0 | `sleep_long(90)` + 60s `CancellationToken` → **`TaskCanceledException` @ ~60012 ms**（[`timeout-60.json`](data/timeout-60.json)） |
| C# in-process harness | 同上 | 短超时（~400ms）同样取消客户端等待 |
| Claude Code CLI | **2.1.224** | 支持 `--mcp-config` / `MCP_TIMEOUT`；本机 `claude -p` 因 **未登录** 未能完成工具调用。按 ADR/产品文档：默认仍应按 **60s** 规划；`MCP_TIMEOUT`（ms）可延长。 |
| Claude Desktop | （未在本机实测） | 沿用 ADR-0003：硬编码 ~60s，配置 timeout 常被忽略 |
| VS Code Copilot / Cursor（TS SDK 系） | TS SDK `DEFAULT_REQUEST_TIMEOUT_MSEC=60000` | 默认 60s；可用 per-request `timeout`，但产品 UI 未必暴露 |

**错误形态（C# 探针）**：客户端侧 `TaskCanceledException` / `OperationCanceledException`；服务端若收到 `notifications/cancelled` 则工具 `CancellationToken` 触发（见 Q6）。

### Q2 — 进度是否延长超时

**否（默认路径）。**

- stdio 探针：`sleep_with_progress(90)` 在 60s 内收到 **59** 次 progress，仍在 **~60018 ms** 被取消（[`progress-60.json`](data/progress-60.json)）。
- TS SDK：`resetTimeoutOnProgress` **默认 false**；仅当客户端显式开启（且正确注入 `progressToken`）时进度才重置计时器。不可假设 Cursor/Desktop 开启。
- 结论与 ADR-0003 一致：**架构上不依赖进度规避超时**；进度仅用于 UI/可观测性。

### Q3 — Tasks 扩展端到端

- 服务器 `.WithTasks(new InMemoryMcpTaskStore())` 后协商协议 **`2026-07-28`**，capabilities.extensions 含 **`io.modelcontextprotocol/tasks`**（[`server-caps.txt`](data/server-caps.txt)）。
- C# 客户端 `CallToolAsTaskAsync` → 立即 `CreateTaskResult` → `tasks/get` 轮询 → `CompletedTaskResult`（in-process + stdio 均通过）。
- `tasks/cancel` → `CancelledTaskResult`，并触发工具侧取消（测试覆盖）。
- **要求**：客户端协议 ≥ 2026-07-28 **且** opt-in。旧客户端 / 未 opt-in 时走同步 `tools/call`，仍受 60s 约束。
- **产品决策**：Tasks = **增强**；手工 `open/status` = **基线**（所有客户端可用）。

### Q4 — 手工模式（ADR-0003 §1）

最终形状（本 spike 工具名可直接映射产品）：

| 工具 | 行为 | 返回关键字段 |
|------|------|----------------|
| `slow_open` / 产品 `workspace_open` | **立即返回**；后台加载 | `jobId`, `phase` (`queued`/`loading`), `suggestedAction` |
| `slow_status` / 产品 `workspace_status` | 轮询 | `phase`, `completedUnits`/`totalUnits`, `estimatedRemainingMs`, `suggestedAction`, `error?` |

**SuggestedAction 文案（供产品采用；模型是否遵守未经本 spike 会话验证）**：

- loading：`Call slow_status with this jobId; do not retry slow_open.`
- ready：`Proceed with query tools.`
- not_found：`Unknown jobId. Call slow_open to start a new job.`

实测：`slow_open(1–3s)` 墙钟 ≪ 100ms 返回；轮询至 `ready` 成功（in-process + stdio）。

### Q5 — 并发

两个 `concurrent_probe(holdMs=400)` 并行：`overlapped=True`，不同 `ManagedThreadId`。确认 C# SDK **fire-and-forget** 分发 → ADR-0002「请求级快照」**仍有必要**（不可假设串行）。

### Q6 — 取消

| 路径 | 结果 |
|------|------|
| 显式 `notifications/cancelled` + 匹配 `RequestId` | **可靠**：工具日志出现 `cancelled`；客户端 `Wait` 以 OCE 结束 |
| `CallToolAsync(..., cancellationToken)`（CTS） | SDK 在 `CancellationToken.Register` 里 **fire-and-forget** `SendMessageAsync(cancelled)`；**存在竞态**，不保证服务端一定看到取消 |
| `tasks/cancel` | **可靠**（Tasks 路径） |
| 客户端超时后是否仍接收后续响应 | 规范/SDK：用户取消时服务端**不**再发该请求的成功/错误响应；客户端已放弃等待 |

产品含义：超时/Esc 后服务端应协作取消；但**不能**依赖「客户端 CTS 一定送达 cancelled」。长任务优先 Tasks cancel 或可恢复的手工 job 状态机。

### Q7 — 软预算观感

`soft_budget_page`：在预算内返回部分 `items` + `truncated=true` + `nextCursor` + 明确续页文案。第二次带 cursor 返回后续项。形状与 S2 软预算表兼容；**模型是否会按文案续页未做会话验证**（仅协议/API 层确认）。

### Q8 — 客户端兼容矩阵

| 客户端 | 版本（本 spike） | 协议 | 超时 | Tasks | 进度 keepalive | 证据级别 |
|--------|------------------|------|------|-------|----------------|----------|
| C# MCP Client（stdio 探针） | 2.1.0 | 2026-07-28 | 调用方 CTS；模拟 60s 实测成立 | 支持（opt-in API） | 否 | **本机实测** |
| Claude Code CLI | 2.1.224 | （未登录未测协商） | 默认按 60s 规划；`MCP_TIMEOUT` **未在本机验证** | 未知；勿依赖 | 勿依赖 | 版本已记录；交互未测 |
| Claude Desktop | — | — | ~60s 硬编码（既有 ADR 证据） | 通常无 | 否 | **既有证据，非本 spike 点击** |
| Cursor / VS Code Copilot（TS SDK） | TS 默认 60s | 视客户端 | 默认 60s；`resetTimeoutOnProgress` 默认 false | 视客户端是否 opt-in | 默认否 | **SDK/文档外推** |

---

## 写入 ADR / 实现 spec 的约束

1. **加载必须非阻塞**（`workspace_open` + `workspace_status`）；即使用户方案当前 <60s。
2. **Tasks 仅增强**；未 opt-in 时行为与手工模式一致。
3. **进度不保活**；可发 progress，但超时策略独立。
4. 所有列表/扫描工具：**软预算 + nextCursor + 截断说明**（S2 默认值保留）。
5. 未就绪错误必须带 **SuggestedAction** 指向 `workspace_status`，禁止暗示「再调一次 open」。
6. README 兼容性说明采用上表；注明 Desktop/部分 IDE 以既有证据为准、本 spike 以 stdio 探针为准。

## 完成判据回答

> 在用户实际使用的客户端上，打开一个 145 项目的方案会不会失败？

**协议层答案（本 spike 实测）**：默认/常见 **60s** `tools/call` 超时成立；进度不能保活；阻塞式长加载在该天花板下会失败。S2 显示部分方案 ~15–20s，看似「还能过」，但余量不足且不可移植。

**产品答案（外推至 Desktop/IDE，结合既有 ADR 证据）**：若 `workspace_open` 阻塞整次 `tools/call`，有真实失败风险。若采用本 spike 验证的手工形状（立即返回 + status 轮询，Tasks 作增强），**不会因 MCP 工具调用超时而失败**。Claude Code / Desktop / Copilot 的点击级复测仍建议补一次（见脆弱点）。

## 脆弱点

- Claude Desktop / VS Code / Cursor 的 Tasks opt-in 与 UI 超时未在本环境实机点击验证。
- Claude Code 交互式实测受登录限制；`MCP_TIMEOUT` 行为需在登录环境复测一次。
- CTS→cancelled 的 fire-and-forget 竞态是 SDK 实现细节，升级 SDK 后应回归。
