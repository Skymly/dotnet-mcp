# dotnet-mcp

[English](README.md) | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | **日本語**

.NET ワークスペース向けの MCP（Model Context Protocol）サーバーです。コーディングエージェントに対し、**コンパイラレベルの精度**を備えたシンボルナビゲーション、ソースジェネレーター帰属（attribution）、Avalonia/MAUI XAML クエリ、および**制限付き** Workspace Edit サーフェス（名前変更 / 診断修正 / コードリファクタリング）を提供します。汎用ファイルライター、シェル、あるいは LSP プロキシではありません。

NuGet パッケージ ID：**`Skymly.DotNetMcp`**（CLI コマンド：`dnmcp`）。.NET 10 以降が必要です。

## できること

- `.sln` / `.slnx` / `.slnf` / SDK スタイルの `.csproj` / `.vbproj` / `.fsproj` の読み込み
- Roslyn による C# と VB.NET、および Roslyn スナップショットと並行する FCS スタックによる F# のサポート
- 名前を解決してチェックサム付きの `SymbolHandle` を取得し、定義へ移動、メンバー、参照検索、実装検索、呼び出し元検索、型階層を実行
- 手書きメンバーとソースジェネレーター生成メンバーの帰属判定（ジェネレーターアセンブリ + 型 + バージョン）
- プロジェクト診断、ジェネレーター / 生成ソース / ジェネレーター診断の一覧、`dynamic` 呼び出し箇所の一覧
- Avalonia `.axaml` および MAUI `.xaml`：クラス、xmlns、`x:Name`、コンパイル済みバインディングパス、XAML 診断
- 制限付き書き込み：名前変更、診断修正（プロジェクト全体の Fix all を含む）、コードリファクタリングに対するプレビュー後に適用するフロー

## しないこと

- 汎用のファイル書き込み / パッチ / 作成 / 削除ツール
- シェル、プロセス、HTTP、またはパッケージダウンロードツール
- WPF / WinUI XAML
- F# ソースジェネレーター帰属（F# ハンドルに対する `symbol_attribution` は `GeneratorLanguageNotSupported` を返却）、ジェネレータークエリ、動的呼び出しクエリ、診断修正、またはコードリファクタリング
- 言語を跨ぐ F# の読み書き — F# ハンドルは定義プロジェクトとその F# 依存プロジェクト（または `entireSolution` 指定時は全 F# プロジェクト）を検索しますが、C#/VB の利用側は名前のみ示され検索されず、C#/VB の依存先を持つ可視な F# シンボルは名前変更できません
- メソッド抽出 / シグネチャ変更 UI
- 信頼できないリポジトリの安全なオープン — `workspace_open` は **MSBuild 評価およびプロジェクトのアナライザー／ジェネレーターを実行します**

ツール名はスナップショットテストにより固定されています。ドメイン用語は [`CONTEXT.md`](CONTEXT.md) を参照してください。

## クイックスタート

信頼済みルート（trusted roots）の指定が**必須**です。プロセスの作業ディレクトリが暗黙の信頼済みルートになることはありません。  
Windows で複数のルートを指定する場合は `;` で区切ります。`DOTNET_MCP_TRUSTED_ROOTS` を設定することも可能です。両方設定された場合、起動時は一方を置き換えるのではなく両者の和集合を使用します。`--roots=path` は `--roots path` と同等であり、空の値は拒否されます。ルート設定が欠落または空の場合は終了コード 1 で終了し、スタックトレースではなく単一の JSON オブジェクト（`error`、`message`、`suggestedAction`）を stderr に出力します。

`Skymly.DotNetMcp` は NuGet に公開されています。推奨インストール方法は `dnx` です。ソースビルドやローカルパックも利用できます。

```bash
dnx Skymly.DotNetMcp --yes -- --roots /path/to/repo
```

MCP クライアント（stdio）：

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

等価コマンド：`dotnet tool exec Skymly.DotNetMcp --yes -- --roots /path/to/repo`、または `dotnet tool install -g Skymly.DotNetMcp` 実行後に `dnmcp --roots /path/to/repo`。

### 開発実行

このリポジトリのクローンから実行する場合：

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

### ローカルパック

```bash
dotnet pack src/DotNetMcp.Server -c Release -o ./artifacts
dotnet tool exec --source ./artifacts --yes Skymly.DotNetMcp -- --roots /path/to/repo
```

stdio のみ対応。フレームワーク依存 .NET ツールであり、NativeAOT は不要です。

典型的なエージェントループ：`workspace_open`（即座に返却）→ `ready` になるまで `workspace_status` をポーリング → `symbol_resolve` → ナビゲーション / 帰属。大規模ソリューションでは `.slnf` または単一プロジェクトを推奨します。すでにワークスペースに含まれている `.slnf` エントリ（重複、または先行エントリによって推移的に取り込まれたプロジェクト）はオープン失敗とはせずスキップし、ロード警告に名前を記載します。

エージェント向けの導入設定については [`docs/agent-guidance.md`](docs/agent-guidance.md) を参照してください。利用側リポジトリの `AGENTS.md` / `CLAUDE.md` にそのまま貼り付けられるブロック（呼び出しループ、1-based 位置モデル、preview → apply 書き込みルール）が含まれています。サーバー側も initialize 結果の `instructions` に同じ要点を送信し、2 つのコアワークフローを案内する `trace_symbol` / `safe_rename` プロンプトを提供しています。

## MCP ツール

| グループ | ツール |
|----------|--------|
| Workspace | `workspace_open` · `workspace_status` · `workspace_list_projects` · `workspace_check_drift` |
| Diagnostic fix | `diagnostics_list_fixes` · `diagnostics_preview_fix` · `diagnostics_apply_fix` |
| Symbol | `symbol_resolve` · `symbol_resolve_at` · `symbol_summary` · `symbol_goto_definition` · `symbol_source` · `symbol_members` · `symbol_find_references` · `symbol_find_implementations` · `symbol_find_callers` · `symbol_type_hierarchy` · `symbol_attribution` · `symbol_preview_rename` · `symbol_apply_rename` · `symbol_list_refactorings` · `symbol_preview_refactoring` · `symbol_apply_refactoring` |
| Project | `project_diagnostics` · `project_list_generators` · `project_list_generated_sources` · `project_list_generator_diagnostics` · `project_list_dynamic_invocations` |
| XAML | `xaml_resolve_class` · `xaml_list_xmlns` · `xaml_resolve_name` · `xaml_resolve_binding` · `xaml_diagnostics` |

`diagnostics_list_fixes` と `symbol_list_refactorings` は `epoch` を返します。`fixIndex` と `refactoringIndex` はその `epoch` に対してのみ有効です。`epoch` が進んだ後にプレビューを呼ぶと、別のアクションを選ぶのではなく `FixListEpochMismatch` または `RefactoringListEpochMismatch` で失敗します。

コードアクションプロバイダーが例外を投げた場合でも、`diagnostics_preview_fix` と `symbol_preview_refactoring` は `FixApplyFailed` または `RefactoringApplyFailed` を返し、メッセージにプロバイダーがスローした旨を記載します。これは手書き変更を生まない修正とは区別されます。Fix all はその箇所をスキップせず `FixApplyFailed` で停止します。

`projectId` を省略した `project_diagnostics` は、読み込みに失敗したプロジェクトを `Severity=Error` の診断ではなく `error` と `suggestedAction` として報告します。その行を `diagnostics_list_fixes` に渡さないでください。

コンパイルが CS0006 または CS0012（解決できなかったメタデータ参照）を報告している場合、`project_diagnostics` はページング前のプロジェクト全体診断セットから判定して `degraded` と `degradedReason` を設定します。その際に出力されるエラーは実際のコード不具合ではなくワークスペース由来のアーティファクトである可能性があるため、修正プレビューを信頼する前に `dotnet build` で確認してください。それ以外の場合、これら 2 フィールドは省略されます。  
ページング結果（`items` / `truncated` / `nextCursor`）の `message` は任意フィールドであり、補足事項がない場合は省略されます。

`symbol_find_references` は C#/VB ハンドルに対して正確な XAML 使用箇所（バインディング、`x:Class`、要素型、`x:Name` フィールド、イベントハンドラー）も統合します。ソース側のヒットが先にページングされ、XAML ヒットは同じページングストリームの後続として `xamlUsage`（使用種別名）付きで返されます（ソースヒットでは省略）。名前一致のみの `Unverified` 箇所は含まれず、XAML スキャンはソース参照と同じ範囲（定義プロジェクト＋依存プロジェクト、または `entireSolution` 時は全プロジェクト）を対象とします。両レーンは 1 つのソフトバジェットを共有し、カーソルは両ステージを順に進みます。

`symbol_resolve_at` は登録済み XAML ドキュメント内の位置も受け付けます：使用箇所を覆う位置は対象ハンドルへ解決され、`resolvedFrom` = `Reference` および正確な使用範囲の `matched` を返します。いずれの使用箇所にも該当しない位置は `NoSymbolAtPosition` を返します。

スコープ未指定の C#/VB `symbol_resolve` がソフトバジェットに達した場合、それまでに検証済みの候補と `incompleteProjectIds` を返します。候補の `summary.projectId` を用いて次の検索をスコープ限定するか、ソース位置を `symbol_resolve_at` に渡してください。候補リストはバジェット切れ前に完了した作業分のみを含みます。

`symbol_preview_rename` は C#/VB ハンドルについて XAML も同期させます：登録済み XAML ドキュメント内の `Exact` 使用箇所（バインディング、`x:Class`、要素／属性名、`x:DataType`、マークアップ型参照、`x:Name` 値、イベントハンドラー値）は同一プレビュー内の通常の old/new スライスになり（それぞれそのスパン内の単純名出現箇所のみを編集）、`symbol_apply_rename` が同一パイプライン（WriteSuppression、エンコーディング／BOM 保持、ファイル追加・削除なし）で書き込みます。名前一致の `Unverified` 使用箇所は書き込まれず `unverifiedXamlMentions` に報告されます。`Origin = SourceGenerator` の拒否原則は維持されますが、例外が 1 つあります：生成された `x:Name` フィールドはその XAML 値と手書き参照を通じて名前変更でき、ジェネレーターが次回実行時にフィールドを再出力します。診断修正やコードリファクタリングのプレビューが XAML ドキュメントをスライス編集することはありません。

名前変更のプレビューと適用は、リンクドキュメント、読み込み済み各ターゲットフレームワーク、登録済み XAML を含め、変更ファイルを持つ読み込み済みコンパイルの `affectedProjectIds` を返します。新たなコンパイルエラーが発生していないか確認するには、適用前後で各 ID を `projectId` に指定して `project_diagnostics` を呼び出し、`nextCursor` でページングしてください。プロジェクトが `degraded` を報告した場合は対象ビルドで確認してください。この範囲は編集を所有するプロジェクトのみを対象とするため、変更のない利用側は必要に応じて別途確認してください。

名前変更の書き込みセットは、コンパイラから可視な参照と上記 `Exact` XAML 使用箇所です。メタデータ名文字列（`GetTypeByMetadataName` や `ForAttributeWithMetadataName` の引数など）、コンパイラがバインドしなかった短い属性名、ドキュメント、コメント、PublicAPI リストは書き換えません。これら名前一致のヒットは代わりに `unverifiedMentions` に報告されるため（ドキュメント系ヒットは `includeDocMentions` 指定時のみ）、名前変更がすべて網羅したと仮定する前にリストを確認してください。

位置情報を含むすべての出力は単一の位置モデルを共有します：`line` と `column` は 1-based、列は UTF-16 コード単位で数え（タブは 1 列）、`endColumn` は排他的終端であり、コンパイラが出力する `file(line,col)` 規約と一致します。`start`/`length` はスライス用の UTF-16 オフセットとして残されています。ソース位置を持たない場合（`InMetadata`、`None`）は位置フィールドが省略され、ソース生成ドキュメント内の位置は生成テキスト内の座標になります。あるツールが報告した位置はそのまま次のツールへ渡せます — たとえば `project_diagnostics` 行の `line`/`column`/`endLine`/`endColumn` は `diagnostics_list_fixes` や `diagnostics_preview_fix` のロケーター引数そのままです。

`symbol_attribution` は宣言の有無とジェネレーター ID に加えて第 3 の軸を報告します：ルートシンボルおよび各メンバー上の `triggers[]` は、生成メンバーの原因となった手書きコードを示します — コンパイラがバインドした `PartialDefinition` 対応箇所（C# と VB）、`[ObservableProperty]` / `[RelayCommand]` / `[JsonSerializable]` などの `Attribute` 宣言、または XAML 使用インデックスから解決された `XamlElement`（`x:Name` フィールドまたは `x:Class` ドキュメントルート）です。各トリガーは `confidence`（コンパイラバインド済み証拠は `Exact`、一意な名前逆引き規約は `Convention`）、統一位置 4 値、`detail`、およびコードトリガーの場合は `symbol_summary` で再解決できる `handle` を持ちます。手書きシンボルは `triggers: []` を返し、いずれのルールにも一致せず partial 対応箇所も持たない生成メンバーは推測位置ではなく `triggers: []` と `triggerMessage` を返します。

`workspace_open` または XAML ツールへの空または空白のみの `path` は、`PathOutsideTrustedRoots` ではなく空パスエラー（`InvalidWorkspacePath` または `XamlDocumentNotFound`）になります。  
`workspace_open` はバックグラウンド読み込み開始前に失敗した場合の `errorCode`（null の場合は省略）を含め、`workspace_status` と同じ進捗フィールドを返します。

ソースジェネレーターまたはアナライザーがサーバー同梱版より新しい `Microsoft.CodeAnalysis` を参照している場合、Roslyn により拒否され、その出力は `project_list_generators`、`project_list_generated_sources`、`symbol_attribution` から静かに欠落します。ワークスペースが ready になると、`workspace_status.warnings` にファイル名（パスは除く）を示す `AnalyzerReferencesNewerCompiler` エントリが入ります。これはアセンブリのメタデータから読み取られるため、判定のためにアナライザーを読み込む必要はありません。対処法はより新しい Roslyn を同梱した dotnet-mcp リリースへの更新です。

MSBuild が宣言したものの読み込み可能なアセンブリに解決されないアナライザーパス（出力が未ビルドの `OutputItemType="Analyzer"` プロジェクト参照やディスク上に存在しない DLL など）も黙殺されません：`project_list_generators` はそれを `unresolvedAnalyzerReferences` に記載し、アナライザープロジェクトをビルドしてワークスペースを開き直すよう促す `message` を添えます。ジェネレーター ID は実際に読み込まれたアセンブリに対してのみ報告されます。

## セキュリティ

1. **信頼済みルート（Trusted roots）** — すべてのパスは正規化されます（親の再解析ポイントを含む）。解決できないリンクは fail-closed で失敗します。読み込まれたプロジェクトグラフと適用書き込みパスは再チェックされます。`--roots` / `--roots=path` または `DOTNET_MCP_TRUSTED_ROOTS` で設定してください。両方設定すると和集合になります。パス比較は Windows では大文字・小文字を区別せず、Linux と macOS では区別します（既定で大文字・小文字を区別しない macOS ボリュームを含むため、設定したルートと同じ大文字・小文字表記で指定してください）。起動失敗時はスタックトレースではなく stderr に `TrustedRootsConfigurationFailed` を出力します。
2. **オープンは実行を意味する** — ソリューションの読み込みは MSBuild と参照されているアナライザー / ソースジェネレーターを実行します。信頼できないツリーに対してこのサーバーを使用しないでください。`.slnf` のプロジェクトエントリは MSBuild が開く前に検査されますが、`.sln` / `.slnx` / 単一プロジェクトのグラフは読み込み後に検査されるため、ルート外の `ProjectReference` は拒否前に評価されます。また、xunit v3 などのパッケージがコンパイル時にパッケージキャッシュのソースを注入し、ビルドが元来それらツールチェーンルート上のコードを実行するため、読み込まれたドキュメントやアナライザー参照は NuGet パッケージキャッシュと dotnet インストールディレクトリ配下への解決が許可されます；プロジェクトパスおよび書き込みセット全体には引き続き信頼済みルートが必須です。
3. **既定は読み取り専用 + 限定書き込み** — 名前変更 / 診断修正 / リファクタリングのプレビュー・適用のみ。汎用の書き込み・コマンド実行・ネットワークツールはありません。BOM がなく有効な UTF-8 でもないドキュメントに対する適用は拒否され、書き込まれません（`SourceEncodingRefused`）。
4. **監査** — ローカルプロセスログ（stdio 時は stderr）。ツール名とパスのメタデータのみを記録し、ソース本文は含まず、テレメトリもありません。`DOTNET_MCP_AUDIT=0` で無効化できます。ログ設定は環境変数とサーバー自身のインストールディレクトリからのみ読み込まれ、MCP クライアントがサーバーを起動したディレクトリの `appsettings.json` は読み込まれないため、開いたリポジトリが監査ログの形式変更やサイレント化を行うことはできません。

詳細は [ADR-0004](docs/adr/0004-security-and-path-policy.md) を参照してください。

## ソフトバジェットと長時間負荷

以下のツールはソフトバジェット（[ADR-0003](docs/adr/0003-long-running-operations-session-concurrency.md)）に達すると停止し、一般的なクライアントの ~60 秒 `tools/call` 上限でハングする代わりに部分結果と `nextCursor` を返します：`project_diagnostics`、`xaml_diagnostics`、`project_list_dynamic_invocations`、`symbol_find_references`、`symbol_find_callers`。F# ハンドルの場合、バジェットはプロジェクトスキャン間でチェックされます — バジェットを消費すると部分ページとスコープ内の次プロジェクトから再開する `nextCursor` を返し、プロジェクト単位の FCS チェック自体はバジェット中断されません。以下はバジェットなしで完了まで（またはクライアントタイムアウトまで）実行されます：`symbol_members`、`symbol_type_hierarchy`、`symbol_find_implementations`、`project_list_generated_sources`、`project_list_generator_diagnostics`。`symbol_resolve` は同じバジェットで制限されますが、バジェット超過時は部分ページではなく `SoftBudgetExceeded` で失敗します。進捗通知はキープアライブ（keepalive）では**ありません**。

| 環境変数 | 既定値 | 用途 |
|----------|--------|------|
| `DOTNET_MCP_BUDGET_SINGLE_PROJECT_MS` | 5000 | 単一プロジェクトのコンパイル（例：`project_diagnostics`） |
| `DOTNET_MCP_BUDGET_FIND_REFS_SCOPED_MS` | 5000 | スコープ限定 find-references |
| `DOTNET_MCP_BUDGET_FIND_REFS_ENTIRE_MS` | 20000 | ソリューション全体 find-references |
| `DOTNET_MCP_BUDGET_BATCH_DIAGNOSTICS_MS` | 15000 | `projectId` 省略時の `project_diagnostics` |
| `DOTNET_MCP_BUDGET_FIXALL_PROJECT_MS` | 15000 | プロジェクト Fix all；超過するとプレビュー失敗 |

ドキュメント範囲の Fix all は最大 32 件まで修正を適用します。プロジェクト範囲の Fix all も固定の適用上限（既定 64 件）で停止します。いずれの適用上限も環境変数ではありません。

`0` はバジェットがすでに消費済みであることを意味します — リストツールは即座に（空の場合もある）切り詰めページと `nextCursor` を返し、プロジェクト Fix all はプレビューに失敗します；`symbol_find_references` と `symbol_find_callers` はカーソルを進めるため現在のドキュメント（さらに `symbol_find_references` は XAML レーン上の現在のプロジェクト）を完了するため、ページに項目が含まれる場合があります；負値や解析不能な値は既定値へフォールバックします。

`workspace_open` は MCP リクエストをブロックしません。MCP Tasks に対応しないクライアントは `workspace_status` をポーリングしてください。`workspace_status.watcher` は `ok`、`lost`、`off` のいずれかで、`lost` はファイル監視に失敗し再起動を試みたことを示します。詳細は `spikes/s3-mcp-long-running/CONCLUSIONS.md` を参照してください。同ノートはスパイク時の `jobId` 形式と `jobId` を持たない製品 DTO を区別しています。

## 開発 / CI

製品およびテストの対象は **net10.0** です。フィクスチャには net8.0 / net9.0 プロジェクトが含まれます（`MsBuildWorkspaceIntegrationTests` に必要）。

```bash
dotnet restore DotNetMcp.slnx
dotnet build DotNetMcp.slnx -c Release --no-restore
dotnet test DotNetMcp.slnx -c Release --no-build --verbosity normal
```

`packages.lock.json` はなく、`RestorePackagesWithLockFile` も設定していません。直接 `PackageReference` のバージョンは `Directory.Packages.props` で一元固定されています；テストフィクスチャとスパイクはオプトアウトし各自で固定しています。推移的依存の解決はロックされていないため、後のリストアで別の推移バージョンが選ばれる場合があります。

CI：[`.github/workflows/ci.yml`](.github/workflows/ci.yml)（Ubuntu、Windows **および** macOS；製品 SDK は [`global.json`](global.json) 10.0.x ロールフォワード、フィクスチャ SDK は 8.0.x/9.0.x；C# 空白フォーマット検査、既知脆弱性パッケージ検査、pack ＋ `McpServer` メタデータ検査、および Quick Start の `dotnet tool exec` 形式でパック済みツールを動かすスモークテストを実行）。サードパーティ Action は commit SHA に固定され、ワークフロートークンは読み取り専用です。独立した [`codeql.yml`](.github/workflows/codeql.yml) ワークフローが毎回のプルリクエストと週次で C# ソースに対して CodeQL を実行し、Dependabot が週次で NuGet 更新（パッケージファミリー別）と GitHub Actions 更新を提案します。

空白ルールはルートの [`.editorconfig`](.editorconfig)（LF 改行、C# 4 スペースインデント）にあります；フィクスチャとスパイクは除外されます。CI は Ubuntu 上でこれを実行するため、LF 改行のチェックアウトであればローカルでも同じコマンドを実行できます：

```bash
dotnet format whitespace . --folder --include src/ tests/DotNetMcp.Tests/ benches/ --exclude "**/bin/**" "**/obj/**" --verify-no-changes
```

各 `--include` フォルダには末尾のスラッシュが必要です。省略するとファイル名として解釈され、一致するものがなく 1 ファイルも検査せずにパスしてしまいます。

製品ベンチマーク：[`docs/perf/benchmark.md`](docs/perf/benchmark.md)。失敗ケースも含むタスク単位 A/B 評価記録は [`docs/perf/agent-eval.md`](docs/perf/agent-eval.md) です。この記録は `#528` 公開ゲートを閉じるものではありません。

```bash
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite fixtures
dotnet run --project benches/DotNetMcp.Bench -c Release -- --suite smoke
```

このリポジトリは [mattpocock/skills](https://github.com/mattpocock/skills) を使用しています；`AGENTS.md` と `docs/agents/` を参照してください。
