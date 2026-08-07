# Spike S1: 源生成器归因技术路线验证

Issue [#2](https://github.com/Skymly/dotnet-mcp/issues/2) 的可运行验证。回答「如何把源生成成员稳定归因到生成器身份」的技术路线问题，供实现 spec 使用。

## 依赖

- .NET SDK 8+（本机验证用 SDK 10）
- NuGet：`Microsoft.CodeAnalysis.*` **5.6.0**、`Microsoft.Build.Locator`、`CommunityToolkit.Mvvm` 8.4

## 如何跑

```powershell
cd spikes/s1-generator-attribution
dotnet test src/S1.Verification/S1.Verification.csproj
```

测试按 Q1–Q9 分组；`ITestOutputHelper` 会打印 FilePath dump、driver 对账、归因结果与耗时。详细结论见 [CONCLUSIONS.md](CONCLUSIONS.md)。

## 布局

| 路径 | 作用 |
|------|------|
| `fixtures/SampleApp` | CommunityToolkit.Mvvm + STJ 源生成 + 自写 `CustomGenerator` |
| `fixtures/CustomGenerator` | 固定 HintName 的增量生成器 |
| `fixtures/CollisionA/B` + `CollisionHost` | 两个生成器输出相同 HintName |
| `src/S1.Verification` | xUnit 验证（接缝 = Roslyn 公开行为） |

## 完成判据（已满足）

对 `SampleApp.PersonViewModel.Name`（`[ObservableProperty]`）稳定输出：

`CommunityToolkit.Mvvm.SourceGenerators::CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator`
