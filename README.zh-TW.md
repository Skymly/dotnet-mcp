# dotnet-mcp

[English](README.md) | [简体中文](README.zh-CN.md) | **繁體中文** | [日本語](README.ja.md)

面向 .NET 工作區的 MCP（Model Context Protocol）伺服器。為編碼代理提供**編譯器級精準**的符號導覽、原始碼產生器歸因、Avalonia/MAUI XAML 查詢，以及**受限的** Workspace Edit 表面（重新命名 / 診斷修正 / 程式碼重構）。它並非通用的檔案寫入工具、Shell 或 LSP 代理。

NuGet 套件 ID：**`Skymly.DotNetMcp`**（CLI 命令：`dnmcp`）。需要 .NET 10+。

## 核心能力

- 載入 `.sln` / `.slnx` / `.slnf` / SDK 風格 `.csproj` / `.vbproj` / `.fsproj`
- 透過 Roslyn 支援 C# 與 VB.NET；透過與 Roslyn 快照並存的 FCS 堆疊支援 F#
- 將名稱解析為附帶雜湊校驗碼的 `SymbolHandle`，再進行移至定義、成員、參考、實作、呼叫端與型別階層查詢
- 區分手寫成員與原始碼產生器產生之成員的歸因（產生器組件 + 型別 + 版本）
- 專案診斷；列出產生器 / 產生的原始碼 / 產生器診斷；列出 `dynamic` 叫用點
- Avalonia `.axaml` 與 MAUI `.xaml`：類別、xmlns、`x:Name`、已編譯繫結路徑、XAML 診斷
- 受限寫入：針對重新命名、診斷修正（含專案級 Fix all）與程式碼重構採用先預覽再套用流程

## 不提供的功能

- 通用寫入 / 修補 / 建立 / 刪除檔案工具
- Shell、處理序、HTTP 或套件下載工具
- WPF / WinUI XAML
- F# 原始碼產生器歸因（對 F# 控制代碼呼叫 `symbol_attribution` 會回傳 `GeneratorLanguageNotSupported`）、產生器查詢、動態叫用查詢、診斷修正或程式碼重構
- 跨語言 F# 讀寫 —— F# 控制代碼只搜尋定義專案及其 F# 相依專案（或透過 `entireSolution` 搜尋每個 F# 專案）；C#/VB 取用端僅會列出名稱而不會搜尋，且具備 C#/VB 相依方的可見 F# 符號無法重新命名
- 擷取方法（Extract-method） / 變更簽章（change-signature）介面
- 安全開啟未受信任的儲存庫 —— `workspace_open` **會執行 MSBuild 求值與專案分析器/產生器**

工具名稱由快照測試鎖定。領域詞彙見 [`CONTEXT.md`](CONTEXT.md)。

## 快速開始

**必須**設定受信任根目錄（trusted roots）。處理序工作目錄絕不會作為隱含的受信任根。  
在 Windows 上使用 `;` 分隔多個根路徑，亦可設定環境變數 `DOTNET_MCP_TRUSTED_ROOTS`。當兩者皆有設定時，啟動會使用其聯集，互不覆蓋。`--roots=path` 與 `--roots path` 相同；空值會遭拒絕。缺少或空白的根設定會以結束代碼 1 結束，並於 stderr 輸出一筆 JSON 物件（`error`、`message`、`suggestedAction`），而非堆疊追蹤。

`Skymly.DotNetMcp` 已發布於 NuGet。首選透過 `dnx` 安裝執行；從原始碼建置或本機封裝亦可使用。

```bash
dnx Skymly.DotNetMcp --yes -- --roots /path/to/repo
```

MCP 用戶端（stdio）：

```json
{
  "mcpServers": {
    "dnmcp": {
      "command": "dnx",
      "args": ["Skymly.DotNetMcp", "--yes", "--", "--roots", "/path/to/repo"]
    }
  }
}
```

等價用法：`dotnet tool exec Skymly.DotNetMcp --yes -- --roots /path/to/repo`，或 `dotnet tool install -g Skymly.DotNetMcp` 後執行 `dnmcp --roots /path/to/repo`。

### 開發執行

於本儲存庫複本目錄中執行：

```bash
dotnet run --project src/DotNetMcp.Server -- --roots /path/to/repo
```

```json
{
  "mcpServers": {
    "dnmcp": {
      "command": "dotnet",
      "args": ["run", "--project", "src/DotNetMcp.Server", "--", "--roots", "/path/to/repo"]
    }
  }
}
```

### 本機封裝

```bash
dotnet pack src/DotNetMcp.Server -c Release -o ./artifacts
dotnet tool exec --source ./artifacts --yes Skymly.DotNetMcp -- --roots /path/to/repo
```

僅限 stdio。為框架相依 .NET 工具；不需要 NativeAOT。

典型代理迴圈：`workspace_open`（立即傳回）→ 輪詢 `workspace_status` 直到 `ready` → `symbol_resolve` → 導覽 / 歸因。大型解決方案建議優先使用 `.slnf` 或單一專案。若 `.slnf` 項目已在工作區中（重複項目，或先前項目已透過傳遞相依帶入的專案），會予以略過並記於載入警告，而不會導致開啟失敗。

代理接入設定請參閱 [`docs/agent-guidance.md`](docs/agent-guidance.md)，其中提供可直接貼入取用方儲存庫 `AGENTS.md` / `CLAUDE.md` 的片段——包含呼叫迴圈、1-based 位置模型與「先預覽後套用」的寫入規則。伺服器亦會在 initialize 回應的 `instructions` 中傳送相同簡述，並內建 `trace_symbol` / `safe_rename` 兩個 prompt 引導代理完成兩項核心工作流程。

## MCP 工具

| 群組 | 工具 |
|------|------|
| Workspace | `workspace_open` · `workspace_status` · `workspace_list_projects` · `workspace_check_drift` |
| Diagnostic fix | `diagnostics_list_fixes` · `diagnostics_preview_fix` · `diagnostics_apply_fix` |
| Symbol | `symbol_resolve` · `symbol_resolve_at` · `symbol_summary` · `symbol_goto_definition` · `symbol_source` · `symbol_members` · `symbol_find_references` · `symbol_find_implementations` · `symbol_find_callers` · `symbol_type_hierarchy` · `symbol_attribution` · `symbol_preview_rename` · `symbol_apply_rename` · `symbol_list_refactorings` · `symbol_preview_refactoring` · `symbol_apply_refactoring` |
| Project | `project_diagnostics` · `project_list_generators` · `project_list_generated_sources` · `project_list_generator_diagnostics` · `project_list_dynamic_invocations` |
| XAML | `xaml_resolve_class` · `xaml_list_xmlns` · `xaml_resolve_name` · `xaml_resolve_binding` · `xaml_diagnostics` |

`diagnostics_list_fixes` 與 `symbol_list_refactorings` 會回傳 `epoch`。`fixIndex` 與 `refactoringIndex` 僅於該 `epoch` 內有效。當 `epoch` 推進後，預覽會失敗並回傳 `FixListEpochMismatch` 或 `RefactoringListEpochMismatch`，而非選取其他動作。

若程式碼動作提供者拋出例外狀況，`diagnostics_preview_fix` 與 `symbol_preview_refactoring` 仍會回傳 `FixApplyFailed` 或 `RefactoringApplyFailed`，但訊息會說明提供者拋出例外狀況，不同於未產生手寫變更的修正。Fix all 會以 `FixApplyFailed` 中止，而非略過該項目。

未指定 `projectId` 時，`project_diagnostics` 會將失敗的專案回報為 `error` 加上 `suggestedAction`，而非作為 `Severity=Error` 診斷回傳。請勿將該資料列傳遞給 `diagnostics_list_fixes`。

當編譯回報 CS0006 或 CS0012（未能解析的中繼資料參考）時，`project_diagnostics` 會在分頁前依專案完整診斷集合計算並設定 `degraded` 與 `degradedReason`。此時列出的錯誤可能是工作區產物而非真正程式碼缺陷；採信修正預覽前請先透過 `dotnet build` 驗證。其他情況則省略這兩個欄位。  
分頁結果（`items` / `truncated` / `nextCursor`）中的 `message` 為選用欄位，無補充說明時省略。

`symbol_find_references` 亦會為 C#/VB 控制代碼合併 Exact XAML 使用點（繫結、`x:Class`、項目型別、`x:Name` 欄位、事件處理常式）。原始碼命中優先分頁回傳；XAML 命中接著在同一分頁串流中回傳並帶有標明使用種類的 `xamlUsage` —— 原始碼命中則省略此欄位。依名稱比對的 `Unverified` 點永不納入，且 XAML 掃描涵蓋與原始碼參考相同的範圍（定義專案加相依專案，或使用 `entireSolution` 時為所有專案）。兩條通道共用同一軟預算，游標會依序走過兩個階段。

`symbol_resolve_at` 亦接受已註冊 XAML 文件內的位置：落在某使用點內的位置會解析為其目標控制代碼，設定 `resolvedFrom` = `Reference` 並將 `matched` 設為精確使用範圍；位於任何使用點之外的位置則回傳 `NoSymbolAtPosition`。

未限定範圍的 C#/VB `symbol_resolve` 若觸及軟預算，會回傳目前已驗證的候選項目與 `incompleteProjectIds`。可使用候選項目的 `summary.projectId` 縮小下次查詢範圍，或將原始碼位置傳入 `symbol_resolve_at`。候選清單僅涵蓋預算到期前已完成的工作。

`symbol_preview_rename` 會為 C#/VB 控制代碼保持 XAML 同步：已註冊 XAML 文件中的 `Exact` 使用點（繫結、`x:Class`、項目/屬性名稱、`x:DataType`、標記類型參考、`x:Name` 值、事件處理常式值）會在同一預覽中成為一般 old/new 切片——每個切片僅編輯其範圍內該符號的簡單名稱出現位置——而 `symbol_apply_rename` 透過同一管線寫入（遵循 WriteSuppression、保留編碼/BOM、不新增或刪除檔案）。依名稱比對的 `Unverified` 使用點會回報於 `unverifiedXamlMentions` 下而不會寫入。對 `Origin = SourceGenerator` 的拒絕原則依然保留，唯一例外是產生的 `x:Name` 欄位：它會透過其 XAML 值加上手寫參考進行重新命名，而產生器會在下次執行時重新產出該欄位。診斷修正與程式碼重構預覽絕不會對 XAML 文件進行切片修改。

重新命名預覽與套用會為擁有變更檔案的已載入編譯傳回 `affectedProjectIds`，涵蓋連結檔案、每個已載入目標架構及已註冊的 XAML。若要檢查是否出現新的編譯錯誤，請於套用前後分別以每個 ID 作為 `projectId` 呼叫 `project_diagnostics`，並使用 `nextCursor` 分頁。若專案回報 `degraded`，請透過定向建置驗證。此範圍僅涵蓋擁有編輯的專案；必要時請另行檢查未變動的取用端。

重新命名的寫入集合為編譯器可見參考加上上述 `Exact` XAML 使用點。它不會改寫中繼資料名稱字串（例如 `GetTypeByMetadataName` 或 `ForAttributeWithMetadataName` 引數）、編譯器未繫結的簡短屬性名稱、XML 文件註解、註解或 PublicAPI 清單——這些依名稱比對的命中會回報於 `unverifiedMentions`（文件類別命中僅於設定 `includeDocMentions` 時列出），因此在假定重新命名已完整涵蓋前請先檢視該清單。

所有含位置的輸出共用同一位置模型：`line` 與 `column` 皆為 1-based，欄位以 UTF-16 字碼單位計算（Tab 計為一欄），且 `endColumn` 為排除性右界——與編譯器列印的 `file(line,col)` 慣例相同。`start`/`length` 保留為用於切片的 UTF-16 位移量。無原始碼位置（`InMetadata`、`None`）會省略位置欄位；原始碼產生文件中的位置為產生文字內的座標。任一工具回報的位置皆可原封不動傳給下一個工具——例如 `project_diagnostics` 資料列的 `line`/`column`/`endLine`/`endColumn` 即是 `diagnostics_list_fixes` 與 `diagnostics_preview_fix` 的定位引數。

`symbol_attribution` 在宣告可用性與產生器身分之外提供第三軸資訊：在根符號及每個成員上的 `triggers[]` 會指明觸發產生成員的手寫程式碼——由編譯器繫結的 `PartialDefinition` 對應項（C# 與 VB）、`Attribute` 宣告（如 `[ObservableProperty]`、`[RelayCommand]` 或 `[JsonSerializable]`），或透過 XAML 使用索引解析的 `XamlElement`（`x:Name` 欄位或 `x:Class` 文件根）。每個觸發器包含 `confidence`（編譯器繫結證據為 `Exact`，唯一名稱反向慣例查詢為 `Convention`）、統一位置四元組、`detail`，以及針對程式碼觸發器可透過 `symbol_summary` 重新解析的 `handle`。手寫符號傳回 `triggers: []`；若產生成員的產生器不符合任何規則且無 partial 對應項，則傳回 `triggers: []` 與 `triggerMessage`，而非猜測位置。

傳給 `workspace_open` 或 XAML 工具的 `path` 若為空白或僅含空白字元，將視為空路徑錯誤（`InvalidWorkspacePath` 或 `XamlDocumentNotFound`），而非 `PathOutsideTrustedRoots`。  
`workspace_open` 會傳回與 `workspace_status` 相同的進度欄位，包括在背景載入前即失敗時的 `errorCode`（若為 null 則省略）。

若原始碼產生器或分析器參考了比伺服器內嵌版本更新的 `Microsoft.CodeAnalysis`，Roslyn 會拒絕它，其輸出也會靜默地從 `project_list_generators`、`project_list_generated_sources` 與 `symbol_attribution` 中消失。當工作區就緒後，`workspace_status.warnings` 會包含一筆記載該檔案名稱（不含路徑）的 `AnalyzerReferencesNewerCompiler` 項目。該資訊讀取自組件中繼資料，因此無須載入分析器即可得知。修正方法為使用內嵌較新版 Roslyn 的 dotnet-mcp 發行版。

若 MSBuild 宣告了分析器路徑，但無法解析為可載入的組件——例如輸出尚未建置的 `OutputItemType="Analyzer"` 專案參考，或磁碟上遺失的 DLL——系統同樣不會靜默略過：`project_list_generators` 會將其列於 `unresolvedAnalyzerReferences` 下並附帶 `message`，提示建置分析器專案並重新開啟工作區。產生器身分永遠只對實際成功載入的組件回報。

## 安全性

1. **受信任根（Trusted roots）** —— 每個路徑皆會進行標準化解析（包含父級重解析點）。無法解析的連結會採 fail-closed 拒絕。已載入的專案圖與套用寫入路徑皆會再次檢查。透過 `--roots` / `--roots=path` 或 `DOTNET_MCP_TRUSTED_ROOTS` 設定；同時設定時取聯集。路徑比較在 Windows 忽略大小寫，於 Linux 與 macOS 則區分大小寫（包含預設不區分大小寫的 macOS 磁碟區，因此請使用與設定根相同的大小寫）。啟動設定失敗時於 stderr 輸出 `TrustedRootsConfigurationFailed`，而非堆疊追蹤。
2. **開啟即執行** —— 載入解決方案會執行 MSBuild 以及所參考的分析器 / 原始碼產生器。切勿將此伺服器指向不受信任的目錄樹。`.slnf` 專案項目會在 MSBuild 開啟前先行檢查；`.sln` / `.slnx` / 單一專案圖會在載入後檢查，因此超出受信任根的 `ProjectReference` 會先經過求值才被拒絕。已載入的文件與分析器參考亦可能解析於工具鏈根目錄下——NuGet 套件快取與 dotnet 安裝目錄——因為像 xunit v3 等套件會從套件快取注入來源至編譯中，且建置本來就會執行這些目錄下的程式碼；專案路徑與整個寫入集合仍必須位於受信任根內。
3. **預設唯讀 + 具名寫入** —— 僅提供重新命名 / 診斷修正 / 重構的預覽與套用。無通用寫入、命令或網路工具。套用時若文件無 BOM 且非有效 UTF-8，將拒絕並不會寫入（`SourceEncodingRefused`）。
4. **稽核** —— 本機處理序日誌（stdio 下輸出至 stderr）。僅記錄工具名稱與路徑中繼資料；不含原始碼文字；無遙測。可使用 `DOTNET_MCP_AUDIT=0` 停用。記錄組態僅讀取環境變數與伺服器自身安裝目錄，不會讀取 MCP 用戶端啟動伺服器所在目錄的 `appsettings.json`，以避免已開啟的儲存庫竄改格式或靜默稽核日誌。

請參閱 [ADR-0004](docs/adr/0004-security-and-path-policy.md)。

## 軟預算與長耗時負載

下列工具會在達到軟預算時停止（[ADR-0003](docs/adr/0003-long-running-operations-session-concurrency.md)），回傳部分結果與 `nextCursor`，以避免卡住並超過常見約 60 秒的用戶端 `tools/call` 上限：`project_diagnostics`、`xaml_diagnostics`、`project_list_dynamic_invocations`、`symbol_find_references`、`symbol_find_callers`。對於 F# 控制代碼，預算會在專案掃描之間檢查——耗盡預算會產生部分頁面與接續範圍內下一個專案的 `nextCursor`；單一專案的 FCS 檢查本身不受預算控制。下列工具則無預算限制，執行至完成（或用戶端逾時）：`symbol_members`、`symbol_type_hierarchy`、`symbol_find_implementations`、`project_list_generated_sources`、`project_list_generator_diagnostics`。`symbol_resolve` 受同一預算限制，但到達預算時會以 `SoftBudgetExceeded` 失敗，而非傳回部分頁面。進度通知**不是**保活（keepalive）機制。

| 環境變數 | 預設值 | 用途 |
|----------|--------|------|
| `DOTNET_MCP_BUDGET_SINGLE_PROJECT_MS` | 5000 | 單一專案編譯（例如 `project_diagnostics`） |
| `DOTNET_MCP_BUDGET_FIND_REFS_SCOPED_MS` | 5000 | 限定範圍的查找參考 |
| `DOTNET_MCP_BUDGET_FIND_REFS_ENTIRE_MS` | 20000 | 全解決方案查找參考 |
| `DOTNET_MCP_BUDGET_BATCH_DIAGNOSTICS_MS` | 15000 | 省略 `projectId` 時的 `project_diagnostics` |
| `DOTNET_MCP_BUDGET_FIXALL_PROJECT_MS` | 15000 | 專案 Fix all；超出預算則預覽失敗 |

文件範圍 Fix all 最多套用 32 項修正。專案範圍 Fix all 亦會在固定套用上限（預設 64 項）處停止。這兩項套用上限皆非環境變數。

`0` 代表預算已耗盡——列表工具會立即回傳截斷頁面（可能為空）與 `nextCursor`，且專案 Fix all 會使預覽失敗；`symbol_find_references` 與 `symbol_find_callers` 仍會完成目前文件（而 `symbol_find_references` 會於其 XAML 通道完成目前專案）以讓游標推進，因此頁面仍可能包含項目；負數或無法剖析的值會回退為預設值。

`workspace_open` 永不阻擋 MCP 請求。未採用 MCP Tasks 的用戶端應輪詢 `workspace_status`。`workspace_status.watcher` 為 `ok`、`lost` 或 `off`；`lost` 代表檔案監視失敗並已嘗試重新啟動。詳情見 `spikes/s3-mcp-long-running/CONCLUSIONS.md`，該筆記區分了預研階段 `jobId` 形狀與無 `jobId` 的產品 DTO。

## 開發 / CI

產品與測試目標架構為 **net10.0**。測試夾具包含 net8.0 / net9.0 專案（`MsBuildWorkspaceIntegrationTests` 所需）。

```bash
dotnet restore DotNetMcp.slnx
dotnet build DotNetMcp.slnx -c Release --no-restore
dotnet test DotNetMcp.slnx -c Release --no-build --verbosity normal
```

本儲存庫沒有 `packages.lock.json`，亦未設定 `RestorePackagesWithLockFile`。直接 `PackageReference` 版本於 `Directory.Packages.props` 中集中釘選；測試夾具與預研專案則選擇停用並自行釘選版本。傳遞相依性解析並未鎖定，因此後續還原可能會選取不同的傳遞版本。

CI：[`.github/workflows/ci.yml`](.github/workflows/ci.yml)（Ubuntu、Windows **與** macOS；產品 SDK 透過 [`global.json`](global.json) 10.0.x 向前捲動，夾具 SDK 為 8.0.x/9.0.x；包含 C# 空白格式檢查、已知弱點套件檢查、封裝與 `McpServer` 中繼資料檢查，以及使用「快速開始」中 `dotnet tool exec` 形式執行封裝工具的冒煙測試）。第三方 Actions 皆釘選至 commit SHA，工作流程權杖為唯讀。獨立的 [`codeql.yml`](.github/workflows/codeql.yml) 工作流程會在每次 Pull Request 與每週針對 C# 原始碼執行 CodeQL，Dependabot 則每週提議 NuGet 更新（依套件家族分組）與 GitHub Actions 更新。

空白規則定義於根目錄 [`.editorconfig`](.editorconfig)（LF 換行符號、C# 4 空格縮排）；夾具與預研目錄除外。CI 在 Ubuntu 上執行此檢查，您也可在具備 LF 換行符號的檢出目錄上於本機執行相同指令：

```bash
dotnet format whitespace . --folder --include src/ tests/DotNetMcp.Tests/ benches/ --exclude "**/bin/**" "**/obj/**" --verify-no-changes
```

每個 `--include` 資料夾末端都需要加上斜線。若省略末尾斜線，該值會被當作檔案名稱，導致沒有任何項目符合並在未檢查任何檔案的情況下通過。

產品基準測試見 [`docs/perf/benchmark.md`](docs/perf/benchmark.md)。任務層級 A/B 評測紀錄（包含失敗案例）見 [`docs/perf/agent-eval.md`](docs/perf/agent-eval.md)。該紀錄不會關閉 `#528` 發布門禁。

```bash
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite fixtures
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite smoke
```

本儲存庫使用 [mattpocock/skills](https://github.com/mattpocock/skills)；請參閱 `AGENTS.md` 與 `docs/agents/`。
