# Claude Desktop (user-level mcp config excerpt)

Replace the dll path after `dotnet build`.

```json
{
  "mcpServers": {
    "s3-long-running": {
      "command": "dotnet",
      "args": ["C:/Code/Skymly/DotNetMCP/dotnet-mcp/spikes/s3-mcp-long-running/src/S3.Server/bin/Debug/net8.0/S3.Server.dll"]
    }
  }
}
```

Notes:

- Claude Desktop historically hard-codes ~60s tool timeouts and ignores config `timeout` fields (ADR-0003 evidence).
- Prefer `slow_open` / `slow_status` over `sleep_long` when validating UX.
- Tasks extension requires protocol ≥ 2026-07-28 **and** client opt-in; Desktop may not advertise `io.modelcontextprotocol/tasks`.
