# Spike S2 结论：.slnx 加载与规模实测

验证环境：Roslyn **5.6.0**、`MSBuildLocator` → SDK **10.0.302**（按 `dotnet\sdk` 目录选择；net8 宿主下默认发现可能只露出 8.x）、Windows。基准：`Observables.slnx`（加载后 **190** 个 `ProjectId`，含多 TFM 展开）。原始摘要：[`data/summary.json`](data/summary.json)。Fixture 测试：`dotnet test` 8/8 通过。

## 总览推荐

| 项 | 推荐 | 依据 |
|----|------|------|
| `.slnx` | **支持**（Roslyn 5.6.0 稳定包） | Q1–Q2：`OpenSolutionAsync` 成功 |
| preview 依赖 | **不需要** | 5.6.0 已稳定；`SolutionPersistence` 为传递依赖 |
| `.slnf` | **v0 自解析支持** | Q3：fixture 解析 + `OpenProjectAsync` |
| Compilation LRU 默认 | **50**（可配 25–无上限；避免 ≤10） | Q6：cap=10 抖动明显 |
| 引用查找默认作用域 | **依赖闭包**；全解决方案 opt-in | Q7：窄作用域会漏位置；全量仍远低于 60s |
| 软预算 | 见下表 / ADR-0003 | Q4–Q8 |

---

## 逐题结论

### Q1 — `.slnx` 加载可行性

- **可以打开**：`MSBuildWorkspace.OpenSolutionAsync(Observables.slnx)` 成功。
- 包组合：`Microsoft.CodeAnalysis.CSharp.Workspaces` **5.6.0** + `Microsoft.CodeAnalysis.Workspaces.MSBuild` **5.6.0**（传递 `Microsoft.VisualStudio.SolutionPersistence`）+ `Microsoft.Build.Locator` **1.9.1**。
- MSBuild：在 net8.0 宿主下 `QueryVisualStudioInstances()` 可能只露出 8.x SDK；spike 改为按 `dotnet\sdk` 目录选最新（本机 **10.0.302**）。产品实现应同样偏好与解决方案 `global.json` / 最新 SDK 对齐。
- `WorkspaceFailed`：约 **27–28** 条。类别：
  - **`.shproj`**：无语言关联（预期，shared project）
  - **NuGet audit**（`build/_build.csproj` 高危包）
  - 个别 package 项目缺 `obj/.../GeneratedMSBuildEditorConfig`
  - 生成器宿主「有项目引用无匹配元数据引用」警告  
  不阻断其余 ~190 个 ProjectId 进入 workspace。

### Q2 — preview 依赖可接受性

- Issue 撰写时 Roslyn 5.0 尚为 preview；**现 5.6.0 已稳定**，v0 **可直接依赖**，无需 preview 风险答辩。
- 备选（自解析 `.slnx` via SolutionPersistence + 逐 `OpenProjectAsync`）**未再作为主路径实测**——因 `OpenSolutionAsync` 已成功。`.slnf` 自解析路径已验证，若未来需绕过 workspace 的 `.slnx` 读取，可复用同一「列项目 + OpenProjectAsync」加载器。

### Q3 — `.slnf` 降级方案

- 公开 API 仍不支持 `.slnf`（roslyn#73105）。
- 自解析 JSON（`solution.path` + `projects[]`）→ 绝对路径 → `OpenProjectAsync`：**可行**，fixture 测通。
- **v0 决策：自解析支持**（成本低、实现面小）。不声称「原生 MSBuildWorkspace 支持」。

### Q4 — 首次加载实测（同一 SDK 10.0.302）

| 场景 | 墙钟 | 峰值 WorkingSet | 文档数 | 失败数 |
|------|------|-----------------|--------|--------|
| `--cold` 清 bin/obj | ~15.1 s | ~126 MiB | — | 28 |
| 紧随其后的暖加载 | ~14.2 s | ~127 MiB | 1509 | 28 |
| 早期有 obj 的热加载（SDK 8，参考） | ~19.0 s | ~249 MiB | 2512 | 27 |

- 加载墙钟 **~15–19 s** → **确认 ADR-0003：`workspace_open` 必须非阻塞**。
- 冷/暖墙钟接近：OpenSolutionAsync 成本主要在 MSBuild 评估与图构建。
- 已有生成物时 WorkingSet/Document 更高（早期热跑 ~249 MiB / 2512 docs）。

### Q5 — 惰性编译

- 样本 40 项目：`GetCompilationAsync` **p50 ≈ 3 ms，p95 ≈ 143 ms**（SDK 10）。
- 全量 190 项目连续编译：**~3.0 s**，峰值 WorkingSet **~245 MiB**。
- 对「单次工具调用编译当前项目」：远低于 60 s；全量编译仍建议后台/分批。

### Q6 — LRU 阈值

固定序列（触碰 80+回访 20，并含 goto-def / 依赖闭包 FindRefs / 成员 / 诊断）下：

| 上限 | 序列耗时 | 淘汰次数 | 峰值 WS |
|------|----------|----------|---------|
| 10 | ~687 ms | 101 | ~273 MiB |
| 25 | ~13 ms | 76 | ~267 MiB |
| 50 | ~12 ms | 51 | ~267 MiB |
| 无上限 | ~14 ms | 0 | ~268 MiB |

- **≤10 明显抖动**。**推荐默认 50**；配置区间 **25–无上限**。

### Q7 — 引用查找作用域

对探测出的较广引用公共类型（本机一次运行，EntireSolution 32 源位置）：

| 作用域 | 耗时 | 源位置数 |
|--------|------|----------|
| CurrentProject | ~5 ms | 2 |
| DependencyClosure | ~2 ms | 2 |
| EntireSolution | ~2 ms | 32 |

全解决方案查找在 LRU 预压（先编译 60 个项目）下：

| LRU | 耗时 | 预压淘汰 | 源位置 |
|-----|------|----------|--------|
| 10 | ~2 ms | 50 | 32 |
| 50 | ~3 ms | 10 | 32 |
| 无上限 | ~2 ms | 0 | 32 |

- 窄作用域会 **漏源位置**；全量补全且仍 ≪ 60 s。
- LRU=10 在预压阶段淘汰多，但单次 FindRefs 墙钟仍很低（本符号）；默认仍建议 **依赖闭包 + 全量 opt-in**，LRU 默认 50 以避免与多查询序列叠加时的重编译抖动（见 Q6）。

### Q8 — 软时间预算（服务 ADR-0003 §3）

| 工具类别 | 推荐软预算 | 说明 |
|----------|------------|------|
| `workspace_open` | **非阻塞**（墙钟 ~15–20 s） | 立即返回 + status 轮询 |
| 单项目 `GetCompilation` / 跳定义 | **5 s** | 观测 p95 ≪ 1 s |
| 作用域内 Find References | **5 s** | 观测数 ms–数百 ms |
| 全解决方案 Find References | **20 s** | 观测数 ms；大符号时截断+游标 |
| 批量诊断（多项目） | **15 s** | **外推**自编译/LRU 序列，非独立批次实测 |

### Q9 — 多 TFM 行为

- Observables 中多 TFM csproj 展开为多个 `ProjectId`，`Project.Name` 形如 `Observables.Grpc(net8.0)` / `(net9.0)` / `(net10.0)` / `(netstandard2.0)`。
- Fixture `MultiTfm`（net8.0;net9.0）：同路径两个项目，Name 带 `(netX.Y)`。
- 若 `OpenProjectAsync` 并强制 `TargetFramework` 属性，Name **可能不含** TFM 后缀——列表工具应以 `(Name, TFM/Id)` 去重展示。

---

## 写入 ADR / 实现 spec 的约束

1. v0 依赖 Roslyn **5.x 稳定**（本 spike 钉 5.6.0）；`.slnx` 一等公民。
2. `.slnf`：自解析 + 逐项目加载；不声称 workspace 原生支持。
3. MSBuild 注册须能选到解决方案所需的 SDK 主版本（勿死绑宿主 TFM 发现结果）。
4. Compilation LRU 默认 **50**；引用查找默认 **依赖闭包**，全解决方案 opt-in。
5. 加载非阻塞；查询类软预算见上表。
6. 项目列表：同文件多 TFM = 多行，Name 通常含 `(tfm)`。
7. 容忍并分类报告 `.shproj` / audit 等 `WorkspaceFailed`，勿当作致命错误中止加载。

## 脆弱点

- Observables 演进会改变项目数与失败类别；数字是数量级指导而非契约。
- Find References 样本依赖选符；极端广泛符号（如 `string` 包装扩展）可能逼近软预算。
- SDK / Roslyn 小版本升级可能改变 `Project.Name` 与文档计数（生成物）。
