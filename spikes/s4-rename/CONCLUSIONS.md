# Spike S4 结论：rename preview / apply 技术路线

验证环境：Roslyn **5.6.0**、AdhocWorkspace + 产品 `WorkspaceHost` / `WriteSuppression` / `SymbolQueryService`、Windows。.NET 10。测试：`dotnet test` 于本目录（11/11 通过）。

## 总览推荐

| 路径 | 角色 | 风险 |
|------|------|------|
| **Preview** | `Renamer.RenameSymbolAsync` → `Solution.GetChanges` → 按文件 path + 旧/新全文切片；**不写盘** | 切片用全文即可（小文件）；大文件可再裁片段，但合同先按全文 |
| **previewId** | 不透明 id，绑定当前 Epoch + TTL（建议 5 min） | 过期 / Epoch 失配 / 伪造 id 必须三分 |
| **Apply** | `WriteSuppression` → 只写 preview 列出的已有文档 → `TryUpdateDocumentFromText` 回填 → **主动 AdvanceEpoch 一次** | 产品 `WorkspaceHost` 今天没有这条公开 API，#87 必须加 |
| **禁止** | 无 preview 写盘；只写盘等 FSW；只写盘再 `check_drift` | 后两者把 apply 伪装成 Drift，会话快照会抖 |

**ADR：**

- **修订 ADR-0004 §3**（#86）：从「工具面纯只读」改为「默认只读 + 显式 opt-in 的受限 Workspace Edit」。受信根 / 打开即执行 / 审计不记正文不变。
- **修订 ADR-0002**（#87）：§3 已预留「防抖 + 自写抑制 + 文本回填 + 推进 Epoch」。补一条 **intentional apply**：自写必须回填并主动推进 Epoch **一次**，不得把 apply 实现成 FSW/CheckDrift 的 drift-repair。句柄仍不随 Epoch 失效（旧句柄走「符号已不存在」）。

---

## 逐题结论

### Q1 — Preview without disk writes

**可以。** `Renamer.RenameSymbolAsync(solution, symbol, SymbolRenameOptions, newName)` 返回新 `Solution`；`GetChanges` 给出变更文档。

本机 fixture（`Widget.Ping` → `Pong`，`Caller` 引用）：

| 观测 | 值 |
|------|----|
| 变更文档 | **2**：`Widget.cs`（声明）+ `Caller.cs`（引用） |
| 新增 / 删除文档 | **0**（`RenameFile: false`） |
| 磁盘 | preview 前后 `SnapshotDisk` **相等** |
| 墙钟（该次） | **5.7 ms** |

生成树：**不会**进入 Workspace Edit 文档列表。挂上 `CustomGenerator` 后 compilation 有 7 棵树（2 手写 + 5 生成），preview 切片仍只有 2 个 `.cs`；`GetSourceGeneratedDocumentsAsync` 返回 5 篇，路径形如 `CustomGenerator\CustomGenerator.MarkerGenerator\CustomGenerator.Marker.g.cs`，不出现在 slices。

**产品含义**：preview 只声明解决方案内已有手写文档。生成树由下次编译再生，不要把它们写盘。

### Q2 — Self-write vs Drift

产品 `WorkspaceHost` 实测（`Debounce = 0` + `ManualWorkspaceFileWatcher`）：

| 操作 | Epoch |
|------|--------|
| 打开 ready | 1 |
| `WriteSuppression` 内写盘并 `Raise` | **仍为 1**（事件被丢弃；会话文本仍是旧的） |
| 无抑制写盘并 `Raise` | **1 → 2**（外部 Drift 回填） |
| 抑制写盘、不回填、再 `CheckDrift` | **1 → 2**，`ContentMismatchRepaired` on `Widget.cs` |

Spike 内推荐合同（`RenameApplyHost`）：preview 绑 Epoch=1；apply 抑制 + 写盘 + `TryUpdateDocumentFromText` + Epoch **1 → 2**。磁盘与新会话文本一致。二次 apply 同一 previewId → `unknown_preview`。

**产品缺口**：`WorkspaceHost` 只有 FSW/`ApplyChangedPaths`/`CheckDrift` 会推进 Epoch。#87 需要公开的 apply 入口（回填 + 主动 +1）。**不要**「写盘后靠 CheckDrift」——语义是 drift-repair，且 apply 期间若有人拿着旧 session，看起来像外部抖动。

### Q3 — Handle identity after rename

旧句柄：`csharp:{projectId}:RenameApp.Widget.Ping(int)#{checksum}`。

Apply 之后用现有 `SymbolQueryService.GetSummaryAsync`：

| 输入 | 错误码 | 文案要点 |
|------|--------|----------|
| 旧句柄（checksum 合法） | **`SymbolNotFound`** | `Symbol 'RenameApp.Widget.Ping(int)' no longer exists in project '…'`；SuggestedAction 指向用当前名字重新 `symbol_resolve` |
| 篡改 checksum | **`InvalidSymbolHandle`** | `Checksum does not match handle fields.` |
| `symbol_resolve("RenameApp.Widget.Pong")` | 成功 | 新句柄签名是 `RenameApp.Widget.Pong(int)`，checksum 不同 |
| `symbol_resolve("RenameApp.Widget.Ping")` | `SymbolNotFound` | 按名也找不到旧符号 |

**产品含义**：不必给句柄加 Epoch。旧句柄解析成功（非伪造）但签名消失 = 「符号已不存在」，与伪造三分。CONTEXT 新词条应写明这一点。

### Q4 — Generated members before rename

**可以，且必须在调用 Renamer 之前读。** 现有 `symbol_attribution` / `GetAttributionAsync`：

| 符号 | OriginKind | Generator |
|------|------------|-----------|
| `SampleApp.Generated.CustomMarker` | **SourceGenerator** | `CustomGenerator.MarkerGenerator` |
| `GeneratorHost.Host` | Handwritten | — |
| `GeneratorHost.PartialThing`（手写 partial 类型） | Handwritten | — |

#86 在存 preview 之前拒绝 `Origin = SourceGenerator`，带可区分错误 + SuggestedAction（去改生成器输入）。手写 partial 类型可 rename；生成半边不会出现在 Q1 的文档 diff 里。

### Q5 — Cost

`RenameApp` 两文件、预热 compilation 后 5 次 preview：

见 [`data/rename-cost.json`](data/rename-cost.json)。中位数约 **3–19 ms**；同进程第一次调用可到 **~800 ms**（JIT / Renamer 首次）。文档数恒为 2。

**产品含义**：C# rename preview **单次完成**，不要做成软预算分页。5s 作用域查询预算当硬顶足够。大解决方案再观测；本 spike 没有证据表明需要 Soft budget。

---

## 给实现票的合同（冻结）

```
symbol_preview_rename(handle, newName)
  → 先 attribution；SourceGenerator 则拒绝、不存 preview
  → Renamer + GetChanges
  → previewId（Epoch + TTL）+ 每文件 path/old/new + 将失效的 handles
  → 磁盘不变

symbol_apply_rename(previewId)
  → 校验存在 / 未过期 / Epoch 匹配
  → 路径必须已在 preview 且落在受信根、且是解决方案已有文档
  → WriteSuppression 下写盘 + WithDocumentText 回填 + Epoch++
  → 无 preview 不得写；二次 apply 拒绝
```

TTL 建议 **5 分钟**（与「助手看完 diff 再决定」同量级）。过期 vs Epoch 失配 vs 未知 id 三分。
