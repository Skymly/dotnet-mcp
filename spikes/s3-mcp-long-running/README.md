# Spike S3: MCP 长耗时操作与客户端超时实测

Issue [#4](https://github.com/Skymly/dotnet-mcp/issues/4)。验证 ADR-0003：在真实 MCP 超时约束下，加载交互应采用何种形状。

## 依赖

- .NET SDK 8+
- NuGet：`ModelContextProtocol` **2.1.0**、`ModelContextProtocol.Extensions.Tasks` **2.1.0**

## 如何跑

```powershell
cd spikes/s3-mcp-long-running

# 协议接缝测试（常跑）
dotnet test src/S3.Tests/S3.Tests.csproj

# In-process 场景（manual / tasks / timeout / progress / concurrent / cancel / soft-budget / all）
dotnet run --project src/S3.Harness -- all

# 真实 stdio 子进程（最接近 Cursor / Claude Code 的传输）
$dll = "src/S3.Server/bin/Debug/net8.0/S3.Server.dll"
dotnet build src/S3.Server
dotnet run --project src/S3.Harness -- stdio list $dll
dotnet run --project src/S3.Harness -- stdio timeout-60 $dll      # ~60s
dotnet run --project src/S3.Harness -- stdio progress-60 $dll     # ~60s
dotnet run --project src/S3.Harness -- stdio manual $dll
dotnet run --project src/S3.Harness -- stdio tasks $dll
```

## Claude Code CLI

配置样例：[`clients/claude-code.mcp.json`](clients/claude-code.mcp.json)。

```powershell
# 默认超时（通常 60s）下调用 sleep_long(90) 预期失败；手工模式应成功
$env:MCP_TIMEOUT = "120000"   # ms；验证可延长时
claude -p --strict-mcp-config --mcp-config clients/claude-code.mcp.json `
  "Call the MCP tool slow_open with seconds=5, then poll slow_status until phase is ready. Do not call sleep_long."
```

## 布局

| 路径 | 作用 |
|------|------|
| `src/S3.Core` | `SlowJobStore` / soft-budget / 观测日志 |
| `src/S3.Server` | stdio MCP 服务器 + `.WithTasks` |
| `src/S3.Harness` | in-process 场景 + stdio 探针 |
| `src/S3.Tests` | 协议接缝测试 |
| `clients/` | Claude Code / 通用 stdio 配置样例 |
| `data/logs/` | 探针原始输出 |

结论文档见 [CONCLUSIONS.md](CONCLUSIONS.md)。
