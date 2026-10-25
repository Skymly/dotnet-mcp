# Spike S9 结论：源生成器触发点回溯

验证环境：Roslyn **5.9.0**（与仓库 Directory.Packages.props 一致）、MSBuildWorkspace + `Microsoft.Build.Locator` 1.9.1、**CommunityToolkit.Mvvm 8.4.2**、.NET 10 SDK 10.0.303（内置 STJ / Regex / Interop 生成器）、Windows。测试：`dotnet test src` 于本目录（**23/23 通过**）。Fixture：`fixtures/ToolkitApp`（net10.0，引用 CommunityToolkit.Mvvm 8.4.2；STJ/Regex 生成器由框架 ref pack 自动提供）。

## 总览

| 问题 | 结论 | 可信度 |
|------|------|--------|
| Q1 通用 partial 配对 | `IMethodSymbol`/`IPropertySymbol`/`IEventSymbol` 的 `PartialDefinitionPart`/`PartialImplementationPart` 在 Roslyn 5.9 全部可用且**双向链接**；`GetMembers` 对每个 partial 对只呈现**定义部**这一个符号 | **Exact**（编译器绑定的符号关系，非命名猜测） |
| Q2 CommunityToolkit.Mvvm | `[ObservableProperty]` 字段→属性命名规则实测（`_`/`m_` 前缀剥除+首字母大写）；partial 属性走 Q1 配对；`OnXxx*` 回调是**生成侧 partial 方法定义**（每回调两个重载）；`[RelayCommand]` 剥 `On` 前缀与 `Async` 后缀 | 命名逆查 **Convention**（需特性消歧）；partial 对 **Exact** |
| Q3 System.Text.Json | 生成成员写在同名 partial 类里、本身**不是** partial 成员；触发点 = 手写声明上的 `[JsonSerializable]` 特性，`GetAttributes().ApplicationSyntaxReference` 直接命中特性语法节点 | **Exact** |
| Q4 XAML | MAUI `XamlGenerator` / Avalonia `NameGenerator` 同形：hint 名含 XAML 文件名，字段名 == `x:Name` 值 | **Convention**，文档级可 Exact；**未实测**（机制证据来自 S1/S5） |
| Q5 身份键 | `Assembly::Type` 对真实包稳定匹配且 fixture 内唯一；**版本必须剔除**（toolkit AssemblyVersion 钉在 8.4.0.0≠包 8.4.2；STJ 10.0.14.37416≠pack 10.0.11；同 (Asm,Type) 在 9.0.21 pack 中为 9.0.15.404） | 规则键 = `(AssemblyName, TypeFullName)` |

**产品含义**：触发点种类锁定 `PartialDefinition` / `Attribute` / `XamlElement` 三种；解析顺序——先查符号自身 partial 链（Exact），再按规则表反查特性载体（Convention，唯一命中才接受），否则降级为"仅生成器身份"。规则表按 `(Assembly, Type)` 前缀匹配 Origin 标签，`@Version` 仅作展示元数据。

---

## 逐题结论

### Q1 — 通用 partial 配对 API（`Q1_PartialPairingTests`，10/10）

Roslyn 5.9.0 下三类成员符号都有 `PartialDefinitionPart` / `PartialImplementationPart`，**双向可用**：

- **C# 方法**（`CSharp_PartialMethod_TwoParts_LinkBothDirections`）：定义符号 `PartialDefinitionPart=null`、`PartialImplementationPart=impl`、`IsPartialDefinition=True`；实现符号反之。非 partial 成员两个属性均 `null`（`CSharp_NonPartialMember_BothPropertiesNull`）；仅有定义的 partial 方法 `PartialImplementationPart=null`（`CSharp_PartialMethod_DefinitionOnly_HasNullImplementation`）。
- **C# 属性**（C# 13 partial 属性，`CSharp_PartialProperty_LinksBothDirections`）：同上形状，`IPropertySymbol` 无 `IsPartialDefinition`，用"`PartialDefinitionPart` 为空即定义部"判别。
- **C# 14 事件/构造器**（`CSharp14_PartialEvent_LinksBothDirections`、`CSharp14_PartialConstructor_LinksBothDirections`）：`IEventSymbol` 的同两个 API 已可用；partial 构造器复用 `IMethodSymbol` 的 API（`MethodKind=Constructor`）。需 LangVersion≥14（net10 默认满足；属性需 ≥13）。
- **VB partial 方法**（`VB_PartialMethod_LinksBothDirections`）：`Partial Private Sub M()`（定义）↔ `Private Sub M()`（实现），同一 API 同样双向。
- **关键行为**：`INamedTypeSymbol.GetMembers` 对 partial 对只返回**一个**符号——**定义部**（`DeclRefs=1`、`IsPartialDefinition=True`、`ImplPart=set`）。含义：按名查到的成员恒为定义部；若定义在生成树（如 toolkit 回调），呈现给产品的符号就带 SourceGenerator Origin，须沿 `PartialImplementationPart` 回手写实现。
- **生成↔手写两方向实测**：内存驱动器 `EmitPartialImplementationGenerator` 生成实现部 → `PartialDefinitionPart` 落回 `hand.cs`（`Generated_ImplementationPart_ResolvesToHandwrittenDefinition`）；`EmitPartialDefinitionGenerator` 生成定义部 → `PartialImplementationPart` 落回手写实现（`Generated_DefinitionPart_ResolvesToHandwrittenImplementation`）。
- **导航规则**（`Navigation_Rule_FromAnyPart_ToDefinition`）：`symbol.PartialDefinitionPart ?? symbol` 从任一部件得到定义声明。
- **坑**：自建 driver 时 `parseOptions` 必须与 compilation 语言版本一致，否则 `AddSyntaxTrees` 抛"不一致的语言版本"。

### Q2 — CommunityToolkit.Mvvm 8.4.2（`Q2_MvvmGeneratorTests`，7/7）

Fixture `AnalyzerReferences` 实测枚举到 **7 个 toolkit 生成器**（`Enumerate_All_Generators_In_Fixture`）。

**`[ObservableProperty]` 字段 → 属性命名**（`ObservableProperty_Generates_Named_Properties_In_Generated_Trees`，实测输出）：

| 手写字段 | 生成属性 | 规则 |
|---|---|---|
| `_name` | `Name` | `_` 前缀剥除 + 首字母大写 |
| `m_city` | `City` | `m_` 前缀剥除 + 首字母大写 |
| `plain` | `Plain` | 首字母大写 |
| `isActive` | `IsActive` | camelCase → PascalCase |

字段在手写树、属性在生成树（`ObservablePropertyGenerator/ToolkitApp.MainViewModel.g.cs`）；特性 `[ObservableProperty]` 只在字段上（生成属性不带它）。即 **触发点 = 带特性的字段声明**。

**`[ObservableProperty]` 用在 partial 属性**（`ObservableProperty_On_PartialProperty_Pairs_Via_Roslyn_Api`）：`public partial string Nick { get; set; }` 生成实现部。`GetMembers("Nick")` 呈现**手写定义部**（DeclRefs=1→ViewModels.cs），`PartialImplementationPart` → `ObservablePropertyGenerator\ToolkitApp.MainViewModel.g.cs` 中的实现。**走 Q1 配对，Exact**。

**`OnXxx` 回调**（`ObservableProperty_Callbacks_Are_Generated_Definitions`）：每个属性生成 `OnXxxChanging`/`OnXxxChanged` 各**两个重载**（实测：`OnNameChanged(string)` 与 `OnNameChanged(string?, string)`），全部是生成树中的 partial 方法**定义部**（`IsPartialDefinition=True`）。用户手写 `partial void OnNameChanged(string value)` 只与 1 参重载配对（`ImplPart`→ViewModels.cs）；其余 `ImplPart=null`。即：**生成定义部 → `PartialImplementationPart` → 用户实现，Exact**；未实现时落空，回退按字段特性规则。

**`[RelayCommand]`**（`RelayCommand_Generates_Command_Properties_With_Convention_Names`）：`Save`→`SaveCommand`（IRelayCommand）+字段 `saveCommand`；`LoadAsync`→`LoadCommand`（IAsyncRelayCommand，**剥 `Async` 后缀**）；`OnRefresh`→`RefreshCommand`（**剥 `On` 前缀**）。命令属性与后备字段均在生成树（`RelayCommandGenerator/...*.g.cs`）。

**逆查可行性**（`Convention_Inverse_Rules_Find_Unique_Triggers`）：属性名 → 字段候选 `{camel, _camel, m_camel, m_Pascal}` + 要求携带 `ObservablePropertyAttribute` → 4/4 唯一命中；`XxxCommand` → `{stem, On+stem, stem+Async, On+stem+Async}` + `RelayCommandAttribute` → 3/3 唯一命中。**命名逆查成立但属 Convention**：必须要求候选带触发特性消歧，多命中或不命中即降级。

### Q3 — System.Text.Json（`Q3_StjGeneratorTests`，2/2）

net10.0 无需包引用，ref pack 自带 `System.Text.Json.SourceGeneration.JsonSourceGenerator`。

- `AppJsonContext` 实测 **7 个 `DeclaringSyntaxReferences`（1 手写 + 6 生成）**，29 个生成成员（`Default`、`Poco`、`GetTypeInfo`、`Create_*` 等）全部在 `JsonSourceGenerator` 的生成树中（`JsonContext_Generated_Members_Live_In_Generated_Trees`）。
- 生成成员**不是** partial 成员（`PartialDefinitionPart=null` 全数成立）→ 成员级 Q1 配对不适用。
- 触发点 = 手写 partial 类声明上的 `[JsonSerializable]`：`context.GetAttributes()` 跨声明合并 → `ApplicationSyntaxReference.GetSyntax()` 返回**特性语法节点本身**（`JsonSerializable(typeof(Poco))` @ JsonContext.cs `[165..195)`，`JsonSerializable_Attribute_Resolves_To_Handwritten_Declaration`）。**Exact**，且定位到特性级而非仅类级。

### Q4 — XAML（**未实测**，机制结论基于 S1/S5 实测证据）

- **MAUI**：`Microsoft.Maui.Controls.SourceGen::Microsoft.Maui.Controls.SourceGen.XamlGenerator`（S5 实测身份）。`x:Name` 字段 + `InitializeComponent` 生成在 hint 为 `{文档名}.xaml.sg.cs` 的生成树中。**文档级**对应：hint 文件名 ↔ `.xaml` 文档；**元素级**对应：字段名 == `x:Name` 值。
- **Avalonia**：`Avalonia.NameGenerator` 的 `NameGenerator`（S5 记录 `Avalonia.NameGenerator::NameGenerator`，类型全名待实测核对）。同一形态：生成字段回 code-behind partial 类 + `.axaml` 文档。
- 可行性高：机制 = 「hint 名/文档名对应」+「成员名 == x:Name」。XamlC 的 IL 织入不进 Roslyn，不要当符号来源（S5 已定）。
- 规则条目按 `XamlElement` 触发点登记，文档级可升 Exact（若按 hint↔文档一一核对），元素级 Convention。

### Q5 — 规则表身份键（`Q5_IdentityTests`，4/4 + `BuiltinGeneratorTests`）

**真实项目（net10.0 fixture）枚举到的全部身份串**（`Enumerate_All_Generators_In_Fixture` / `Identity_Table_Fixture_Generators`）：

```text
CommunityToolkit.Mvvm.SourceGenerators::CommunityToolkit.Mvvm.SourceGenerators.IMessengerRegisterAllGenerator@8.4.0.0
CommunityToolkit.Mvvm.SourceGenerators::CommunityToolkit.Mvvm.SourceGenerators.INotifyPropertyChangedGenerator@8.4.0.0
CommunityToolkit.Mvvm.SourceGenerators::CommunityToolkit.Mvvm.SourceGenerators.ObservableObjectGenerator@8.4.0.0
CommunityToolkit.Mvvm.SourceGenerators::CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator@8.4.0.0
CommunityToolkit.Mvvm.SourceGenerators::CommunityToolkit.Mvvm.SourceGenerators.ObservableRecipientGenerator@8.4.0.0
CommunityToolkit.Mvvm.SourceGenerators::CommunityToolkit.Mvvm.SourceGenerators.ObservableValidatorValidateAllPropertiesGenerator@8.4.0.0
CommunityToolkit.Mvvm.SourceGenerators::CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator@8.4.0.0
Microsoft.Interop.ComInterfaceGenerator::Microsoft.Interop.ComClassGenerator@10.0.14.37416
Microsoft.Interop.ComInterfaceGenerator::Microsoft.Interop.ComInterfaceGenerator@10.0.14.37416
Microsoft.Interop.ComInterfaceGenerator::Microsoft.Interop.VtableIndexStubGenerator@10.0.14.37416
Microsoft.Interop.JavaScript.JSImportGenerator::Microsoft.Interop.JavaScript.JSExportGenerator@10.0.14.37416
Microsoft.Interop.JavaScript.JSImportGenerator::Microsoft.Interop.JavaScript.JSImportGenerator@10.0.14.37416
Microsoft.Interop.LibraryImportGenerator::Microsoft.Interop.LibraryImportGenerator@10.0.14.37416
System.Text.Json.SourceGeneration::System.Text.Json.SourceGeneration.JsonSourceGenerator@10.0.14.37416
System.Text.RegularExpressions.Generator::System.Text.RegularExpressions.Generator.RegexGenerator@10.0.14.37416
```

- `(Assembly, Type)` 在真实项目生成器集合内**唯一**（断言成立）。
- **版本不可作键**：toolkit 程序集 `AssemblyVersion=8.4.0.0` 而 NuGet 包 8.4.2、FileVersion 8.4.2.1（`Identity_AssemblyVersion_Differs_From_NuGet_Package_Version`）；STJ 生成器 `10.0.14.37416` 来自 ref pack 10.0.11（信息版本为真值）；手工加载 9.0.21 pack 得到同 `(Asm,Type)` 的 `@9.0.15.404`（`Identity_Framework_BuiltIn_Generators`）——同键异版实证。
- Origin 标签 `SourceGenerator(Asm::Type@Version)` 前缀解析回 `(Asm,Type)` 键成立（`Identity_Matches_Product_Origin_Format`）。
- **内置生成器同机制**：`[GeneratedRegex]` 手写定义 `partial Regex Foo()` ↔ 生成实现部经 `PartialDefinitionPart`/`PartialImplementationPart` 配对（`GeneratedRegex_Handwritten_Definition_Links_To_Generated_Implementation`）——内置生成器与第三方包走同一张规则表。
- FilePath 布局随 SDK 变：net10 为 `obj/{cfg}/{tfm}/{asm}/{type}/{hint}`（S1 net8 有 `generated/` 段）——只作启发式，不做契约（与 S1 一致）。

---

## 锁定（5.0 初始规则表）

**触发点种类**：`PartialDefinition`（符号级 partial 链）、`Attribute`（特性载体声明）、`XamlElement`（XAML 文档/元素）。

**可信度**：`Exact`（编译器符号关系或特性语法节点直达）、`Convention`（命名逆查 + 特性消歧，唯一命中才接受；0 或 >1 命中降级）。

| # | 生成器身份键（Assembly::Type） | 触发点 | 匹配规则 | 可信度 |
|---|---|---|---|---|
| 1 | `CommunityToolkit.Mvvm.SourceGenerators::CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator` | `PartialDefinition` | 生成实现部/定义部 → `PartialDefinitionPart`/`PartialImplementationPart` → 手写声明（partial 属性、`OnXxx*` 回调均覆盖） | Exact |
| 2 | 同上 | `Attribute` | 属性名 → 字段候选 `{camel,_camel,m_camel,m_Pascal}`，同类型内带 `ObservablePropertyAttribute` 的手写字段唯一命中 | Convention |
| 3 | `CommunityToolkit.Mvvm.SourceGenerators::CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator` | `Attribute` | `XxxCommand` → stem ∈ `{name, On+name, name+Async, On+name+Async}` 中带 `RelayCommandAttribute` 的手写方法唯一命中 | Convention |
| 4 | `System.Text.Json.SourceGeneration::System.Text.Json.SourceGeneration.JsonSourceGenerator` | `Attribute` | 生成成员 → `ContainingType.GetAttributes()` 中 `JsonSerializableAttribute` → `ApplicationSyntaxReference`（特性语法节点） | Exact |
| 5 | `System.Text.RegularExpressions.Generator::System.Text.RegularExpressions.Generator.RegexGenerator` | `PartialDefinition` | 生成方法实现部 → `PartialDefinitionPart` → 手写定义（触发特性在该声明上） | Exact |
| 6 | `Microsoft.Maui.Controls.SourceGen::Microsoft.Maui.Controls.SourceGen.XamlGenerator` | `XamlElement` | hint `{Doc}.xaml.sg.cs` ↔ `.xaml` 文档（文档级可 Exact）；字段名 == `x:Name` 值 → 元素 | Convention（未实测） |
| 7 | `Avalonia.NameGenerator::…NameGenerator`（类型全名待实测核对） | `XamlElement` | 同 #6 形态（`.axaml`） | Convention（未实测） |
| 8 | **兜底**：任意生成器 | `PartialDefinition` | 生成/手写任一部件 → `PartialDefinitionPart ?? PartialImplementationPart` 链 → 对侧手写声明；VB 同理 | Exact |

**匹配协议**：Origin 标签按 `(AssemblyName, TypeFullName)` 前缀（`@` 之前）匹配规则键，版本仅展示。规则查找顺序：#8 兜底 partial 链 → 表内规则 → 无命中时返回"生成器身份 + 生成位置，触发点未知"（诚实降级，不猜）。

**不可行 / 推迟**：

- 未登记规则的生成器 → 仅能报身份与生成位置；不得编造触发点。
- STJ 等生成成员非 partial → 成员级 partial API 不适用，走类型级特性（已在表中区分）。
- toolkit `OnXxx*` 定义无用户实现时 `PartialImplementationPart=null` → 回退 #2 字段规则或直接报"未实现"。
- 逆查产生歧义（同名候选多个带特性）→ 降级 `ambiguous`，列出候选。
- XAML 两条规则基于 S1/S5 机制证据，**未在本 spike 实测**；落地前需以真实包复跑本测试矩阵。
- VB 仅支持 partial 方法/类型（无 partial 属性/事件/构造器），规则表中成员级条目按语言门控。
