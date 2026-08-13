namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// 插件自理的多语言文案,与宿主同覆盖五语(en / zh-Hans / zh-Hant / ja / ko)。
/// 用法:<c>var loc = new Loc(context.Host.Locale); loc["Send"]</c>。
/// </summary>
public sealed class Loc(string locale)
{
    private const int En = 0, ZhHans = 1, ZhHant = 2, Ja = 3, Ko = 4;

    private int _index = Resolve(locale);

    /// <summary>宿主语言切换时更新(共享实例即处处生效,调用方随后重刷各自文案)。</summary>
    public void Switch(string newLocale) => _index = Resolve(newLocale);

    private static int Resolve(string locale)
    {
        if (locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return locale.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                   || locale.Contains("TW", StringComparison.OrdinalIgnoreCase)
                   || locale.Contains("HK", StringComparison.OrdinalIgnoreCase)
                   || locale.Contains("MO", StringComparison.OrdinalIgnoreCase)
                ? ZhHant
                : ZhHans;
        }
        if (locale.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            return Ja;
        }
        if (locale.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
        {
            return Ko;
        }
        return En;
    }

    /// <summary>取词;缺键时返回键名本身(可视化暴露问题)。</summary>
    public string this[string key] => Table.TryGetValue(key, out string[]? values) ? values[_index] : key;

    /// <summary>带格式化参数取词。</summary>
    public string F(string key, params object[] args) => string.Format(this[key], args);

    //                                 en, zh-Hans, zh-Hant, ja, ko
    private static readonly Dictionary<string, string[]> Table = new()
    {
        ["Title"] = ["AI Assistant", "AI 助手", "AI 助手", "AI アシスタント", "AI 어시스턴트"],
        ["NoProvider"] = ["No provider configured — open Settings (⚙) to add one.", "尚未配置模型接入 —— 点右上角 ⚙ 添加。", "尚未設定模型接入 —— 點右上角 ⚙ 新增。", "プロバイダー未設定 — 右上の ⚙ から追加してください。", "프로바이더가 없습니다 — 오른쪽 위 ⚙에서 추가하세요."],
        ["Agent"] = ["Agent", "Agent", "Agent", "Agent", "Agent"],
        ["AgentTip"] = ["Agent mode: the model may call tools (read terminal, run commands…).", "Agent 模式:模型可调用工具(读终端、执行命令等)。", "Agent 模式:模型可呼叫工具(讀終端、執行命令等)。", "Agent モード:モデルがツールを呼び出せます(端末読取・コマンド実行など)。", "Agent 모드: 모델이 도구를 호출할 수 있습니다(터미널 읽기, 명령 실행 등)."],
        ["AutoApprove"] = ["Auto-approve", "自动批准", "自動批准", "自動承認", "자동 승인"],
        ["AutoApproveTip"] = ["Run commands without asking (risky).", "不再逐条审批命令(有风险)。", "不再逐條審批命令(有風險)。", "確認なしでコマンドを実行します(危険)。", "확인 없이 명령을 실행합니다(위험)."],
        ["NoSession"] = ["(no session)", "(无会话)", "(無會話)", "(セッションなし)", "(세션 없음)"],
        ["InputPlaceholder"] = ["Ask anything…  (Enter to send, Shift+Enter newline, ↑↓ history, @ to attach a remote file)", "问点什么… (Enter 发送,Shift+Enter 换行,↑↓ 翻历史,@ 引用服务器文件)", "問點什麼… (Enter 傳送,Shift+Enter 換行,↑↓ 翻歷史,@ 引用伺服器檔案)", "質問を入力… (Enter で送信、Shift+Enter で改行、↑↓ で履歴、@ でリモートファイル参照)", "질문을 입력하세요… (Enter 전송, Shift+Enter 줄바꿈, ↑↓ 기록, @ 원격 파일 첨부)"],
        // 历史会话(时序库)
        ["History"] = ["Chat history", "历史会话", "歷史會話", "チャット履歴", "대화 기록"],
        ["HistoryHeader"] = ["Saved conversations", "已保存的会话", "已儲存的會話", "保存された会話", "저장된 대화"],
        ["HistoryCount"] = ["{0} conversation(s)", "共 {0} 个会话", "共 {0} 個會話", "会話 {0} 件", "대화 {0}개"],
        ["NoHistory"] = ["No saved conversations yet.", "还没有保存的会话。", "還沒有儲存的會話。", "保存された会話はまだありません。", "저장된 대화가 없습니다."],
        ["Untitled"] = ["(untitled)", "(无标题)", "(無標題)", "(無題)", "(제목 없음)"],
        ["MessageCount"] = ["{0} message(s)", "{0} 条消息", "{0} 則訊息", "メッセージ {0} 件", "메시지 {0}개"],
        ["HistoryLoaded"] = ["Loaded {0} message(s) from history.", "已从历史载入 {0} 条消息。", "已從歷史載入 {0} 則訊息。", "履歴から {0} 件のメッセージを読み込みました。", "기록에서 메시지 {0}개를 불러왔습니다."],
        ["ClearHistory"] = ["Clear all", "清空历史", "清空歷史", "すべて削除", "전체 삭제"],
        ["ConfirmClear"] = ["Click again to confirm", "再点一次确认", "再點一次確認", "もう一度クリックで確定", "한 번 더 클릭하면 삭제"],
        // @ 文件引用
        ["FilePickerHeader"] = ["Files in {0} — ↑↓ to select, Enter to insert", "{0} 下的文件 —— ↑↓ 选择,回车插入", "{0} 下的檔案 —— ↑↓ 選擇,Enter 插入", "{0} のファイル — ↑↓ で選択、Enter で挿入", "{0} 의 파일 — ↑↓ 선택, Enter 삽입"],
        ["FilePickerEmpty"] = ["Nothing matches in {0}", "{0} 下没有匹配项", "{0} 下沒有符合項", "{0} に一致する項目がありません", "{0} 에 일치하는 항목이 없습니다"],
        ["AttachIntro"] = ["--- Referenced remote files (read via SFTP) ---", "--- 以下是用户引用的远端文件(经 SFTP 读取)---", "--- 以下是使用者引用的遠端檔案(經 SFTP 讀取)---", "--- 参照されたリモートファイル(SFTP 経由)---", "--- 참조된 원격 파일(SFTP로 읽음) ---"],
        ["AttachedCount"] = ["Attached {0} file(s)", "已附带 {0} 个文件", "已附帶 {0} 個檔案", "{0} 個のファイルを添付", "파일 {0}개 첨부됨"],
        ["AttachBinary"] = ["binary file — not attached; ask the agent to inspect it with a command instead", "二进制文件,未附带;可让 Agent 用命令查看", "二進位檔案,未附帶;可讓 Agent 用命令檢視", "バイナリファイルのため添付しません。コマンドで確認してください", "바이너리 파일이라 첨부하지 않았습니다. 명령으로 확인하세요"],
        ["AttachFailed"] = ["could not be read", "读取失败", "讀取失敗", "読み取りに失敗", "읽지 못했습니다"],
        ["AttachLimit"] = ["only the first {0} files were attached", "只附带了前 {0} 个文件", "只附帶了前 {0} 個檔案", "最初の {0} 件のみ添付しました", "처음 {0}개 파일만 첨부했습니다"],
        ["Send"] = ["Send", "发送", "傳送", "送信", "전송"],
        ["Stop"] = ["Stop", "停止", "停止", "停止", "중지"],
        ["NewChat"] = ["New chat", "新会话", "新會話", "新規チャット", "새 채팅"],
        ["NewChatTip"] = ["Start a new conversation (discards the current one).", "开始新会话(丢弃当前对话)。", "開始新會話(丟棄當前對話)。", "新しい会話を開始します(現在の会話は破棄)。", "새 대화를 시작합니다(현재 대화는 삭제)."],
        ["Settings"] = ["Settings", "设置", "設定", "設定", "설정"],
        ["You"] = ["You", "你", "你", "あなた", "나"],
        ["AssistantRole"] = ["Assistant", "助手", "助手", "アシスタント", "어시스턴트"],
        ["Thinking"] = ["Thinking", "思考过程", "思考過程", "思考プロセス", "사고 과정"],
        ["ApprovalTitle"] = ["The agent wants to run:", "Agent 请求执行:", "Agent 請求執行:", "エージェントが実行を要求:", "에이전트가 실행을 요청:"],
        ["Approve"] = ["Approve", "批准", "批准", "承認", "승인"],
        ["Deny"] = ["Deny", "拒绝", "拒絕", "拒否", "거부"],
        ["ToolRunning"] = ["running…", "执行中…", "執行中…", "実行中…", "실행 중…"],
        ["ToolDone"] = ["done", "完成", "完成", "完了", "완료"],
        // 代码块头部两个按钮的提示(LiveMarkdown 模板自带的文案是写死英文,这里覆盖)
        ["Copy"] = ["Copy", "复制", "複製", "コピー", "복사"],
        ["ToggleWrap"] = ["Toggle wrap", "切换自动换行", "切換自動換行", "折り返しの切替", "줄바꿈 전환"],
        ["Error"] = ["Error", "错误", "錯誤", "エラー", "오류"],
        ["Cancelled"] = ["Cancelled.", "已取消。", "已取消。", "キャンセルしました。", "취소되었습니다."],
        ["Usage"] = ["tokens: in {0} / out {1}", "tokens:输入 {0} / 输出 {1}", "tokens:輸入 {0} / 輸出 {1}", "tokens:入力 {0} / 出力 {1}", "tokens: 입력 {0} / 출력 {1}"],
        ["Providers"] = ["Providers", "模型接入", "模型接入", "プロバイダー", "프로바이더"],
        ["Add"] = ["Add", "新增", "新增", "追加", "추가"],
        ["Delete"] = ["Delete", "删除", "刪除", "削除", "삭제"],
        ["Save"] = ["Save", "保存", "儲存", "保存", "저장"],
        ["Saved"] = ["Saved.", "已保存。", "已儲存。", "保存しました。", "저장되었습니다."],
        ["Test"] = ["Test", "测试", "測試", "テスト", "테스트"],
        ["Testing"] = ["Testing…", "测试中…", "測試中…", "テスト中…", "테스트 중…"],
        ["TestOk"] = ["Connection OK: {0}", "连接正常:{0}", "連接正常:{0}", "接続 OK:{0}", "연결 정상: {0}"],
        ["TestFail"] = ["Test failed: {0}", "测试失败:{0}", "測試失敗:{0}", "テスト失敗:{0}", "테스트 실패: {0}"],
        ["Name"] = ["Name", "名称", "名稱", "名前", "이름"],
        ["Protocol"] = ["Protocol", "协议", "協議", "プロトコル", "프로토콜"],
        ["BaseUrl"] = ["Base URL", "基地址", "基底位址", "ベース URL", "베이스 URL"],
        ["Model"] = ["Model", "模型", "模型", "モデル", "모델"],
        ["MaxTokens"] = ["Max output tokens", "最大输出 tokens", "最大輸出 tokens", "最大出力トークン", "최대 출력 토큰"],
        ["ApiKey"] = ["API Key", "API Key", "API Key", "API キー", "API 키"],
        ["ApiKeyHint"] = ["Stored encrypted via the host secret store.", "经宿主机密存储加密保存。", "經宿主機密儲存加密保存。", "ホストのシークレットストアで暗号化保存されます。", "호스트 시크릿 저장소에 암호화되어 저장됩니다."],
        ["SystemPrompt"] = ["System prompt (optional)", "系统提示词(可选)", "系統提示詞(可選)", "システムプロンプト(任意)", "시스템 프롬프트(선택)"],
        ["McpServers"] = ["MCP servers", "MCP 服务器", "MCP 伺服器", "MCP サーバー", "MCP 서버"],
        ["McpHint"] = ["Model Context Protocol servers add extra tools to Agent mode. Tools that may modify state ask for approval before running.", "MCP(Model Context Protocol)服务器为 Agent 模式提供额外工具;可能修改状态的工具执行前会请求审批。", "MCP(Model Context Protocol)伺服器為 Agent 模式提供額外工具;可能修改狀態的工具執行前會請求審批。", "MCP(Model Context Protocol)サーバーは Agent モードにツールを追加します。状態を変更しうるツールは実行前に承認を求めます。", "MCP(Model Context Protocol) 서버는 Agent 모드에 도구를 추가합니다. 상태를 변경할 수 있는 도구는 실행 전 승인을 요청합니다."],
        ["McpEnabled"] = ["Enabled", "启用", "啟用", "有効", "사용"],
        ["McpTransport"] = ["Transport", "传输方式", "傳輸方式", "トランスポート", "전송 방식"],
        ["McpCommand"] = ["Command (e.g. npx / uvx / python)", "命令(如 npx / uvx / python)", "命令(如 npx / uvx / python)", "コマンド(例:npx / uvx / python)", "명령(예: npx / uvx / python)"],
        ["McpArguments"] = ["Arguments (one line, quote items with spaces)", "参数(单行,含空格的片段用引号包裹)", "參數(單行,含空格的片段用引號包裹)", "引数(1 行、空白を含む場合は引用符で囲む)", "인수(한 줄, 공백 포함 시 따옴표 사용)"],
        ["McpWorkingDir"] = ["Working directory (optional)", "工作目录(可选)", "工作目錄(可選)", "作業ディレクトリ(任意)", "작업 디렉터리(선택)"],
        ["McpEnv"] = ["Environment variables (KEY=VALUE per line, optional)", "环境变量(每行一条 KEY=VALUE,可选)", "環境變數(每行一條 KEY=VALUE,可選)", "環境変数(1 行に KEY=VALUE、任意)", "환경 변수(줄마다 KEY=VALUE, 선택)"],
        ["McpUrl"] = ["Server URL", "服务器 URL", "伺服器 URL", "サーバー URL", "서버 URL"],
        ["McpHeaders"] = ["HTTP headers (Name: Value per line, optional)", "HTTP 请求头(每行一条 Name: Value,可选)", "HTTP 標頭(每行一條 Name: Value,可選)", "HTTP ヘッダー(1 行に Name: Value、任意)", "HTTP 헤더(줄마다 Name: Value, 선택)"],
        ["McpTestOk"] = ["Connected — {0} tool(s): {1}", "连接成功 —— {0} 个工具:{1}", "連接成功 —— {0} 個工具:{1}", "接続成功 — ツール {0} 件:{1}", "연결 성공 — 도구 {0}개: {1}"],
        ["McpConnecting"] = ["Connecting MCP servers…", "正在连接 MCP 服务器…", "正在連接 MCP 伺服器…", "MCP サーバーに接続中…", "MCP 서버 연결 중…"],
        ["ExplainPrompt"] = ["Explain the following terminal output. Point out any errors and suggest fixes:\n", "请解释以下终端输出,指出其中的错误并给出可能的解决办法:\n", "請解釋以下終端輸出,指出其中的錯誤並給出可能的解決辦法:\n", "以下のターミナル出力を説明し、エラーがあれば指摘して解決策を提案してください:\n", "다음 터미널 출력을 설명하고, 오류를 지적하고 해결 방법을 제안해 주세요:\n"],
        ["NoConnectedSession"] = ["No connected session.", "没有已连接的会话。", "沒有已連接的會話。", "接続中のセッションがありません。", "연결된 세션이 없습니다."],
        ["CmdChat"] = ["AI: Open Chat (Tab)", "AI:打开聊天(标签页)", "AI:開啟聊天(標籤頁)", "AI:チャットを開く(タブ)", "AI: 채팅 열기(탭)"],
        ["CmdChatWindow"] = ["AI: Open Chat (Window)", "AI:打开聊天(窗口)", "AI:開啟聊天(視窗)", "AI:チャットを開く(ウィンドウ)", "AI: 채팅 열기(창)"],
        ["CmdExplain"] = ["AI: Explain Terminal Output", "AI:解释终端输出", "AI:解釋終端輸出", "AI:ターミナル出力を説明", "AI: 터미널 출력 설명"]
    };
}
