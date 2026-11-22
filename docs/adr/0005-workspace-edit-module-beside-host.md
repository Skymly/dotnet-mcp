# ADR-0005: Workspace Edit module 与 WorkspaceHost 并排，preview 带 kind

## 状态

Accepted（2026-08-20），**Amended（2026-10-25 Amendment 1 Rename preview 的 XAML 切片）**

## 上下文

4.0 已有三类命名写（Rename preview、Diagnostic fix、Code Refactoring），CONTEXT.md 里它们同属一份 **Workspace Edit** 合同。实现却是三个 shallow module 各产出切片，再泄漏进 RenamePreviewStore / ApplyRenamePreview：Diagnostic fix 把 oldHandle=fix:…、Code Refactoring 把 
ewName=Title 塞进 Rename 形状。WorkspaceHost 同时拥有 ADR-0002 的新鲜度职责和 preview 生命周期。三个 apply 工具共用一个字典，跨族 apply 会成功且无测试。

Architecture review #125 第 1 项 grilling 后结晶。不重开 ADR-0004 的三个 MCP 工具族，也不把 apply 改成 drift-repair（ADR-0002 Amendment 3）。

## 决策

1. **加深一个 Workspace Edit module**，只拥有：draft 入库、previewId + Epoch + TTL、kind、Trusted root 策略、apply。Rename preview / Diagnostic fix / Code Refactoring 仍是外面的 adapter，只生产路径切片 + 失效 SymbolHandle + 各自的 preview 响应字段。

2. **MCP 工具面不动。** symbol_*_rename / diagnostics_*_fix / symbol_*_refactoring 三个工具族留下。禁止收成通用 workspace_apply_edit。

3. **与 WorkspaceHost 并排。** Host 不再暴露 StoreRenamePreview / ApplyRenamePreview。Host 只做机械动作：WriteSuppression → 写已声明路径 → 回填文本 → Epoch++。不认 previewId。策略（kind、Epoch/TTL、Trusted root、目标文件存在）在 Edit module。

4. **一个 store，记录带 kind**（Rename preview / Fix preview / Refactoring preview）。apply 必须匹配 kind，否则可区分错误。newName / Title / EquivalenceKey / scope **不入库**。

5. **不进本决策：** Roslyn CodeAction → DiffAsync 去重（#125 第 4 项）；ILanguageAdapter；MCP envelope 仪式。F# rename 仍是一个 adapter。

## 考虑过的选项

| 选项 | 淘汰原因 |
|------|---------|
| 三条 list/preview 并进 mega module | interface 膨胀，语言 / CodeFix / Renamer 复杂度进 Workspace Edit，变 shallow |
| 折叠成一个 MCP apply 工具 | 重开 ADR-0004 的通用写口子 |
| 只在 Host 上把 Rename* 改名 | Host interface 更宽，删除测试失败 |
| module 放进 Core | Core 持有 TTL 时钟和写盘副作用，拧 ADR-0001 |
| kind-blind store | 三个工具族在 apply 上是假区分 |
| 三个 store | 拒绝加深，locality 拆碎 |

## 后果

- 种仓外 preview、跨族 apply 的测试打 Edit module 的 seam，不再偷看 Host。
- MCP 合同测试（含 ToolSurfaceGuard）保持工具名。
- CONTEXT.md 的 Workspace Edit 已收紧：preview 含 kind；apply 必须匹配 kind。

## 相关决策

- ADR-0001：Core 是查询 / draft facade，不持有写盘与 TTL 字典
- ADR-0002：intentional apply 仍是 WriteSuppression + 回填 + 推进 Epoch 一次；preview 生命周期不再属于 Host
- ADR-0004：三个命名写工具族仍是允许名单，不是一个通用写工具

## Amendment 1（2026-10-25）：Rename preview 的切片可含已注册 XAML 文档

证据：#525；ADR-0006 的 `IXamlUsageSource` 索引。

Rename preview 产出的文档切片不再只含解决方案手写文档：`symbol_preview_rename` 把受信根内已注册 XAML 文档（Avalonia `.axaml`、根默认 xmlns 为 MAUI URI 的 `.xaml`）的旧/新文本并入同一份 Workspace Edit——索引里 `Exact` 使用点按符号键匹配改写，`Unverified` 同名提及只经 `unverifiedXamlMentions` 报告、不写入。改写规则与 Roslyn Renamer 同级：每个使用点只替换符号简单名在 span 内的一处出现，span 不落在简单名上的 `Exact` 使用点跳过而非猜测。`x:Name` 生成字段是唯一例外入口：定义项目内存在匹配 `XName` 使用点时允许改名——XAML 值 + 手写引用一起改，生成声明本身不写入（下次生成器运行重新产出）。Diagnostic fix 与 Code Refactoring 的 preview 不碰 XAML 文档；store、previewId、kind、Epoch/TTL 语义不变。

## Amendment 2（2026-11-22）多文件 apply 的进程中断恢复

三种 Workspace Edit 的共用 writer 在写第一个多文件切片前，将规范化工作区路径和每个目标的原始字节、原始与预期的 SHA-256 写入用户本地状态目录。先刷盘，再以原子重命名发布记录。写源文件时逐文件做最终路径与内容检查，用同目录暂存文件原子替换现有文件。记录发布、apply 与启动恢复共用跨进程互斥锁。发布过的记录也会阻止其他入口对同一文件或工作区发起覆盖写。

启动时只处理工作区仍在当前受信根中的记录。恢复先检查记录里的全部路径与磁盘字节。若全部已写完则保留，若落在写入中途则恢复每个已写切片的原始字节。遇到手改、缺失或重定向则保留证据，`workspace_status.recovery` 返回冲突与允许披露的文件。恢复操作可重试。previewId、TTL 和 epoch 仍限产生 preview 的进程使用。
