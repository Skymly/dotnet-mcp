# Spike S4: rename preview / apply 技术路线

Issue [#85](https://github.com/Skymly/dotnet-mcp/issues/85)（阻断 Spec [#84](https://github.com/Skymly/dotnet-mcp/issues/84) P0）。验证：Roslyn `Renamer` 能否产出不落盘的 Workspace Edit，再经 `WriteSuppression` 回填 WorkspaceSession，而不被 FSW 当成外部 Drift。

本 spike **不**改产品 MCP 工具面。

## 依赖

- .NET SDK 10
- 产品 `DotNetMcp.Core` / `DotNetMcp.Server`（句柄、归因、WriteSuppression、WorkspaceHost）
- Roslyn Workspaces **5.6.0**
- `tests/fixtures/CustomGenerator`（生成成员 Origin）

## 如何跑

```powershell
cd spikes/s4-rename
dotnet test src/S4.Verification/S4.Verification.csproj
```

测试按 Q1–Q5 分组；`ITestOutputHelper` 打印 diff 文件列表、Epoch、句柄与耗时。结论见 [CONCLUSIONS.md](CONCLUSIONS.md)。

## 布局

| 路径 | 作用 |
|------|------|
| `fixtures/RenameApp` | 跨文件手写 rename fixture（`Widget.Ping` ← `Caller`） |
| `src/S4.Verification` | xUnit 验证（接缝 = Roslyn Renamer + 产品 WorkspaceHost / SymbolQueryService） |
| `data/` | 成本观测 JSON |

## 完成判据

- CONCLUSIONS.md 用观测回答 Issue #85 全部 5 问
- 一次 fixture rename：先 preview（磁盘不变），再 apply（自写抑制），旧符号消失、新名字可 resolve
- 生成成员在调用 Renamer 之前即可识别 Origin
- 无产品工具 / 写 / 命令 / 网络面变更
