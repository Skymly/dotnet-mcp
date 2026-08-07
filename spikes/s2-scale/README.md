# Spike S2: .slnx 加载与 150 项目规模实测

Issue [#3](https://github.com/Skymly/dotnet-mcp/issues/3) 的测量与结论。回答 ADR-0002/0003 中留待实测的参数。

## 依赖

- .NET SDK 8+（本机验证用 SDK 10）
- NuGet：`Microsoft.CodeAnalysis.*` **5.6.0**、`Microsoft.Build.Locator`
- 规模基准：本机 `Observables.slnx`（默认路径见下；可用环境变量覆盖）

## 如何跑

```powershell
cd spikes/s2-scale

# Fixture 接缝测试（常跑）
dotnet test src/S2.Tests/S2.Tests.csproj --filter "Trait!=Scale"

# 全量 Observables 测量（热启动）
$env:OBSERVABLES_SLNX = "C:\Code\Skymly\Observables\Observables\Observables.slnx"
dotnet run --project src/S2.Bench -- --mode all

# 冷启动（清理方案下 bin/obj）
dotnet run --project src/S2.Bench -- --mode load --cold
```

原始结果写入 `data/`。结论文档见 [CONCLUSIONS.md](CONCLUSIONS.md)。

## 布局

| 路径 | 作用 |
|------|------|
| `fixtures/MultiTfm` | 多 TFM（net8.0;net9.0）Name 形态 |
| `fixtures/SampleFilter` | `.slnf` 自解析 + 逐项目加载 |
| `src/S2.Core` | 加载 / 解析 / 指标 / LRU / 引用作用域 |
| `src/S2.Bench` | Observables 长跑测量控制台 |
| `src/S2.Tests` | xUnit 接缝测试 |
| `data/` | 原始 JSON 结果 |
