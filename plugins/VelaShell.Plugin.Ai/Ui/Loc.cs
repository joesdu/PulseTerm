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
        // 只在"用户按了发送、却一个模型都没配"时出现,紧接着就会把配置窗口开出来 ——
        // 所以这句是在交代"接下来发生了什么",不再指路去点哪个按钮(空状态那边有按钮)。
        ["NoProvider"] = ["No model configured — opening the model settings.", "尚未配置模型 —— 已为你打开模型配置。", "尚未設定模型 —— 已為你開啟模型設定。", "モデル未設定 — モデル設定を開きました。", "모델이 없습니다 — 모델 설정을 열었습니다."],
        ["Agent"] = ["Agent", "Agent", "Agent", "Agent", "Agent"],
        ["AgentTip"] = ["Agent mode: the model may call tools (read terminal, run commands…).", "Agent 模式:模型可调用工具(读终端、执行命令等)。", "Agent 模式:模型可呼叫工具(讀終端、執行命令等)。", "Agent モード:モデルがツールを呼び出せます(端末読取・コマンド実行など)。", "Agent 모드: 모델이 도구를 호출할 수 있습니다(터미널 읽기, 명령 실행 등)."],
        // 对话模式(顺序 = ChatMode 的枚举值)
        ["ModeChat"] = ["Chat", "对话", "對話", "チャット", "대화"],
        ["ModePlan"] = ["Plan", "计划", "計劃", "計画", "계획"],
        ["ModeAgent"] = ["Agent", "Agent", "Agent", "Agent", "Agent"],
        ["ModeChatTip"] = ["Chat only — no tools at all. The model answers from the conversation.", "纯对话:完全不给工具,模型只凭对话内容作答。", "純對話:完全不給工具,模型只憑對話內容作答。", "チャットのみ:ツールを一切渡さず、会話の内容だけで答えます。", "대화만: 도구를 전혀 주지 않고 대화 내용만으로 답합니다."],
        ["ModePlanTip"] = ["Plan — read-only tools only. The model investigates and proposes a plan, but cannot change anything.", "计划:只给只读工具。模型可以查,但改不了任何东西,产出的是方案。", "計劃:只給唯讀工具。模型可以查,但改不了任何東西,產出的是方案。", "計画:読み取り専用ツールのみ。調査はできますが変更はできず、手順を提案します。", "계획: 읽기 전용 도구만. 조사는 하되 아무것도 변경할 수 없고 계획을 제시합니다."],
        ["ModeAgentTip"] = ["Agent — full tools (read terminal, run commands, edit remote files). Changes go through the approval mode below.", "Agent:全部工具(读终端、执行命令、改远端文件)。改动按下面的审批方式处理。", "Agent:全部工具(讀終端、執行命令、改遠端檔案)。改動按下面的審批方式處理。", "Agent:全ツール(端末読取・コマンド実行・リモートファイル編集)。変更は下の承認方式に従います。", "Agent: 전체 도구(터미널 읽기, 명령 실행, 원격 파일 수정). 변경은 아래 승인 방식을 따릅니다."],
        // 审批方式(顺序 = ApprovalMode 的枚举值)
        // 这三个标签是安全开关,措辞不许有歧义:曾用"默认审批"表示"每次都问",
        // 但那四个字也能读成"默认给批准",意思正好反过来 —— 用户实际问过。
        // 现在一律用动作句(问 / 部分自动 / 全自动),看一眼就知道会不会替你按下确定。
        ["ApprovalAsk"] = ["Ask each time", "每次询问", "每次詢問", "都度確認", "매번 확인"],
        ["ApprovalReadOnly"] = ["Auto-run read-only", "只读自动", "唯讀自動", "読み取り専用は自動", "읽기 전용 자동"],
        ["ApprovalBypass"] = ["Auto-run everything", "全部自动", "全部自動", "すべて自動実行", "모두 자동 실행"],
        ["ApprovalAskTip"] = ["Every state-changing operation asks first.", "每个可能改变状态的操作都先问一次。", "每個可能改變狀態的操作都先問一次。", "状態を変えうる操作は必ず先に確認します。", "상태를 바꿀 수 있는 작업은 항상 먼저 확인합니다."],
        ["ApprovalReadOnlyTip"] = ["Commands that are provably side-effect free (ls, df, cat…) run without asking. Writing files, typing into the terminal, and anything unclear still ask.", "确定无副作用的命令(ls、df、cat 之类)直接跑;写文件、往终端敲字、以及任何看不准的命令仍旧逐条问。", "確定無副作用的命令(ls、df、cat 之類)直接跑;寫檔案、往終端敲字、以及任何看不準的命令仍舊逐條問。", "副作用がないと確実な命令(ls・df・cat など)は確認なしで実行。ファイル書き込み・端末入力・判断がつかないものは従来どおり確認します。", "부작용이 없다고 확신할 수 있는 명령(ls, df, cat 등)은 바로 실행하고, 파일 쓰기·터미널 입력·판단이 어려운 것은 계속 확인합니다."],
        ["ApprovalBypassTip"] = ["Every tool call runs automatically, including destructive ones. Risky.", "所有工具调用一律自动执行,包括破坏性操作。有风险。", "所有工具呼叫一律自動執行,包括破壞性操作。有風險。", "破壊的なものも含め、すべてのツール呼び出しを自動実行します。危険です。", "파괴적인 것을 포함해 모든 도구 호출을 자동 실행합니다. 위험합니다."],
        ["NoSession"] = ["(no session)", "(无会话)", "(無會話)", "(セッションなし)", "(세션 없음)"],
        ["InputPlaceholder"] = ["Ask anything…  (Enter to send, Shift+Enter newline, ↑↓ history, @ to attach a remote file)", "问点什么… (Enter 发送,Shift+Enter 换行,↑↓ 翻历史,@ 引用服务器文件)", "問點什麼… (Enter 傳送,Shift+Enter 換行,↑↓ 翻歷史,@ 引用伺服器檔案)", "質問を入力… (Enter で送信、Shift+Enter で改行、↑↓ で履歴、@ でリモートファイル参照)", "질문을 입력하세요… (Enter 전송, Shift+Enter 줄바꿈, ↑↓ 기록, @ 원격 파일 첨부)"],
        // 一轮在跑时的占位提示:此刻回车不是"发下一轮",而是"插进这一轮"
        ["InputPlaceholderBusy"] = ["Add to what it's doing…  (Enter queues it; the model reads it before its next step)", "补充点什么… (Enter 排队,模型在下一步之前就会读到)", "補充點什麼… (Enter 排隊,模型在下一步之前就會讀到)", "作業中の内容に補足… (Enter でキューへ、次のステップの前にモデルが読みます)", "진행 중인 작업에 덧붙이기… (Enter로 대기열에 넣으면 다음 단계 전에 모델이 읽습니다)"],
        // 历史会话(时序库)
        ["History"] = ["Chat history", "历史会话", "歷史會話", "チャット履歴", "대화 기록"],
        ["HistoryHeader"] = ["Saved conversations", "已保存的会话", "已儲存的會話", "保存された会話", "저장된 대화"],
        ["HistoryCount"] = ["{0} conversation(s)", "共 {0} 个会话", "共 {0} 個會話", "会話 {0} 件", "대화 {0}개"],
        ["NoHistory"] = ["No saved conversations yet.", "还没有保存的会话。", "還沒有儲存的會話。", "保存された会話はまだありません。", "저장된 대화가 없습니다."],
        ["Untitled"] = ["(untitled)", "(无标题)", "(無標題)", "(無題)", "(제목 없음)"],
        ["MessageCount"] = ["{0} message(s)", "{0} 条消息", "{0} 則訊息", "メッセージ {0} 件", "메시지 {0}개"],
        ["HistoryLoaded"] = ["Loaded {0} message(s) from history.", "已从历史载入 {0} 条消息。", "已從歷史載入 {0} 則訊息。", "履歴から {0} 件のメッセージを読み込みました。", "기록에서 메시지 {0}개를 불러왔습니다."],
        ["SearchHistory"] = ["Search by title…", "按标题搜索…", "按標題搜尋…", "タイトルで検索…", "제목으로 검색…"],
        ["HistoryFiltered"] = ["{0} of {1} conversation(s)", "匹配 {0} / 共 {1} 个会话", "符合 {0} / 共 {1} 個會話", "{1} 件中 {0} 件", "{1}개 중 {0}개"],
        ["Rename"] = ["Rename", "重命名", "重新命名", "名前を変更", "이름 바꾸기"],
        ["Export"] = ["Export as Markdown", "导出为 Markdown", "匯出為 Markdown", "Markdown で書き出す", "Markdown으로 내보내기"],
        ["Exported"] = ["Exported.", "已导出。", "已匯出。", "書き出しました。", "내보냈습니다."],
        ["ClearHistory"] = ["Clear all", "清空历史", "清空歷史", "すべて削除", "전체 삭제"],
        ["ConfirmClear"] = ["Click again to confirm", "再点一次确认", "再點一次確認", "もう一度クリックで確定", "한 번 더 클릭하면 삭제"],
        // @ 文件引用
        ["FilePickerHeader"] = ["Files in {0} — ↑↓ to select, Enter to insert", "{0} 下的文件 —— ↑↓ 选择,回车插入", "{0} 下的檔案 —— ↑↓ 選擇,Enter 插入", "{0} のファイル — ↑↓ で選択、Enter で挿入", "{0} 의 파일 — ↑↓ 선택, Enter 삽입"],
        ["FilePickerEmpty"] = ["Nothing matches in {0}", "{0} 下没有匹配项", "{0} 下沒有符合項", "{0} に一致する項目がありません", "{0} 에 일치하는 항목이 없습니다"],
        ["AttachIntro"] = ["--- Referenced remote files (read via SFTP) ---", "--- 以下是用户引用的远端文件(经 SFTP 读取)---", "--- 以下是使用者引用的遠端檔案(經 SFTP 讀取)---", "--- 参照されたリモートファイル(SFTP 経由)---", "--- 참조된 원격 파일(SFTP로 읽음) ---"],
        ["AttachedCount"] = ["Attached {0} file(s)", "已附带 {0} 个文件", "已附帶 {0} 個檔案", "{0} 個のファイルを添付", "파일 {0}개 첨부됨"],
        ["AttachBinary"] = ["binary file — not attached; ask the agent to inspect it with a command instead", "二进制文件,未附带;可让 Agent 用命令查看", "二進位檔案,未附帶;可讓 Agent 用命令檢視", "バイナリファイルのため添付しません。コマンドで確認してください", "바이너리 파일이라 첨부하지 않았습니다. 명령으로 확인하세요"],
        ["AttachFailed"] = ["could not be read", "读取失败", "讀取失敗", "読み取りに失敗", "읽지 못했습니다"],
        ["AttachFailedList"] = ["Could not read: {0}", "读取失败:{0}", "讀取失敗:{0}", "読み取れませんでした: {0}", "읽지 못했습니다: {0}"],
        ["AttachLocal"] = ["Attach local files", "附加本地文件", "附加本機檔案", "ローカルファイルを添付", "로컬 파일 첨부"],
        ["AttachTooBig"] = ["larger than {0} MB", "超过 {0} MB", "超過 {0} MB", "{0} MB を超えています", "{0} MB 초과"],
        ["RemoveAttachment"] = ["Click to remove", "点击移除", "點擊移除", "クリックで削除", "클릭하여 제거"],
        ["AttachLimit"] = ["only the first {0} files were attached", "只附带了前 {0} 个文件", "只附帶了前 {0} 個檔案", "最初の {0} 件のみ添付しました", "처음 {0}개 파일만 첨부했습니다"],
        ["Send"] = ["Send", "发送", "傳送", "送信", "전송"],
        ["Stop"] = ["Stop", "停止", "停止", "停止", "중지"],
        // 边跑边补:一轮还没答完时,发送键换成「排队」,新消息插进当前这一轮
        // (见 ChatPanelView.Steering.cs)。措辞要说清"会发出去、只是要等一步",
        // 不能让人以为按下去什么都没发生。
        ["Queue"] = ["Queue", "排队", "排隊", "キュー", "대기"],
        ["QueueTip"] = ["Send now — the model gets it before its next step.", "现在就发 —— 模型在下一步之前就会读到。", "現在就傳 —— 模型在下一步之前就會讀到。", "今すぐ送信 — 次のステップの前にモデルが読みます。", "지금 보냅니다 — 모델이 다음 단계 전에 읽습니다."],
        ["Queued"] = ["Queued — it reaches the model before its next step.", "已排队 —— 模型在下一步之前就会读到。", "已排隊 —— 模型在下一步之前就會讀到。", "キューに入れました — 次のステップの前にモデルへ届きます。", "대기열에 넣었습니다 — 모델이 다음 단계 전에 받습니다."],
        ["QueuedTip"] = ["Click to take it back.", "点击撤回。", "點擊撤回。", "クリックで取り消します。", "클릭하면 취소합니다."],
        ["QueueFull"] = ["Already {0} messages queued — let it answer first.", "已经排了 {0} 条 —— 先让它答完这一轮。", "已經排了 {0} 則 —— 先讓它答完這一輪。", "すでに {0} 件が待機中です — まず答えさせてください。", "이미 {0}개가 대기 중입니다 — 먼저 답하게 두세요."],
        ["QueueReturned"] = ["This turn ended early — the queued message is back in the box.", "这一轮提前结束了 —— 排队的消息已放回输入框。", "這一輪提前結束了 —— 排隊的訊息已放回輸入框。", "このターンは途中で終わりました — 待機中のメッセージを入力欄に戻しました。", "이번 턴이 중간에 끝났습니다 — 대기 중이던 메시지를 입력창에 되돌렸습니다."],
        ["Interjected"] = ["You added", "你补充了", "你補充了", "追加した内容", "추가한 내용"],
        ["NewChat"] = ["New chat", "新会话", "新會話", "新規チャット", "새 채팅"],
        ["NewChatTip"] = ["Start a new conversation (discards the current one).", "开始新会话(丢弃当前对话)。", "開始新會話(丟棄當前對話)。", "新しい会話を開始します(現在の会話は破棄)。", "새 대화를 시작합니다(현재 대화는 삭제)."],
        ["Settings"] = ["Settings", "设置", "設定", "設定", "설정"],
        ["ModelSettings"] = ["Models", "模型配置", "模型設定", "モデル設定", "모델 설정"],
        ["GlobalSettings"] = ["Global settings", "全局设置", "全域設定", "全体設定", "전역 설정"],
        ["Close"] = ["Close", "关闭", "關閉", "閉じる", "닫기"],
        ["Ok"] = ["OK", "确定", "確定", "OK", "확인"],
        // 配置工具(独立窗口)
        ["ConfigureTools"] = ["Configure tools", "配置工具", "設定工具", "ツールを設定", "도구 구성"],
        ["ToolsBuiltin"] = ["Built-in", "内置", "內建", "組み込み", "내장"],
        ["ToolsSelected"] = ["{0} tool(s) selected", "已选 {0} 项", "已選 {0} 項", "{0} 件を選択", "{0}개 선택됨"],
        ["ToolReadOnly"] = ["read-only", "只读", "唯讀", "読み取り専用", "읽기 전용"],
        ["ToolsNotLoaded"] = ["Tool list not loaded yet — click “Refresh tools” to fetch it from the server.", "还没有拉过这台服务器的工具清单 —— 点「更新工具库」获取。", "還沒有拉過這台伺服器的工具清單 —— 點「更新工具庫」取得。", "このサーバーのツール一覧はまだ取得していません。「ツールを更新」で取得してください。", "이 서버의 도구 목록을 아직 가져오지 않았습니다 — 「도구 갱신」을 누르세요."],
        ["RefreshTools"] = ["Refresh tools", "更新工具库", "更新工具庫", "ツールを更新", "도구 갱신"],
        ["RefreshingTools"] = ["Connecting…", "正在连接…", "正在連接…", "接続中…", "연결 중…"],
        ["ToolsRefreshed"] = ["{0}: {1} tool(s)", "{0}:{1} 个工具", "{0}:{1} 個工具", "{0}: ツール {1} 件", "{0}: 도구 {1}개"],
        ["You"] = ["You", "你", "你", "あなた", "나"],
        ["AssistantRole"] = ["Assistant", "助手", "助手", "アシスタント", "어시스턴트"],
        ["Thinking"] = ["Thinking", "思考过程", "思考過程", "思考プロセス", "사고 과정"],
        // 回复气泡:思考折叠区标题 / 头部的步数与耗时 / 底部的复制与元信息
        ["ThinkingActive"] = ["Thinking…", "正在思考…", "正在思考…", "思考中…", "생각 중…"],
        ["ThinkingDone"] = ["Thought for {0}", "已思考 {0}", "已思考 {0}", "{0} 思考しました", "{0} 동안 생각함"],
        ["Steps"] = ["{0} steps", "{0} 步", "{0} 步", "{0} ステップ", "{0}단계"],
        ["EditMessage"] = ["Edit and resend (discards everything after)", "编辑重发(丢弃其后的全部内容)", "編輯重送(丟棄其後的全部內容)", "編集して再送信(以降はすべて破棄)", "수정 후 재전송(이후 내용은 모두 삭제)"],
        ["DeleteFromHere"] = ["Delete this and everything after", "删除这条及其之后的全部内容", "刪除這則及其之後的全部內容", "これ以降をすべて削除", "이 메시지 이후 전체 삭제"],
        ["Regenerate"] = ["Regenerate", "重新生成", "重新生成", "再生成", "다시 생성"],
        ["RegenerateOnlyLast"] = ["Only the last reply can be regenerated — edit the message above instead.", "只有最后一条回复能重新生成 —— 想改前面的,请用那条消息上的「编辑重发」。", "只有最後一則回覆能重新生成 —— 想改前面的,請用該訊息上的「編輯重送」。", "再生成できるのは最後の回答だけです。前のものは該当メッセージの「編集して再送信」から。", "마지막 답변만 다시 생성할 수 있습니다 — 이전 것은 해당 메시지의 「수정 후 재전송」을 사용하세요."],
        ["CopyReply"] = ["Copy the whole reply", "复制整段回复", "複製整段回覆", "回答全体をコピー", "답변 전체 복사"],
        ["Copied"] = ["Copied", "已复制", "已複製", "コピーしました", "복사됨"],
        // 输入框下方的用量(可见文字尽量短,细节全在提示里)
        ["UsageIdle"] = ["no tokens used yet", "尚无用量", "尚無用量", "使用量なし", "사용량 없음"],
        ["UsageContextLine"] = ["Last turn context: {0} / {1} ({2}%)", "上一轮上下文:{0} / {1}({2}%)", "上一輪上下文:{0} / {1}({2}%)", "直近の入力: {0} / {1}({2}%)", "직전 입력: {0} / {1}({2}%)"],
        ["UsageTotalsLine"] = ["Conversation total: in {0} / out {1}", "本次会话累计:输入 {0} / 输出 {1}", "本次會話累計:輸入 {0} / 輸出 {1}", "この会話の累計: 入力 {0} / 出力 {1}", "이 대화 누적: 입력 {0} / 출력 {1}"],
        ["UsageReasoningLine"] = ["Reasoning tokens: {0}", "思考 tokens:{0}", "思考 tokens:{0}", "思考トークン: {0}", "사고 토큰: {0}"],
        ["ShowEarlier"] = ["Show {0} earlier message(s)", "显示更早的 {0} 条消息", "顯示更早的 {0} 則訊息", "以前のメッセージ {0} 件を表示", "이전 메시지 {0}개 보기"],
        ["UsageTrimmedLine"] = ["{0} earliest message(s) dropped from context to fit the window", "为放进上下文窗口,已移出最早的 {0} 条消息(界面与历史里仍在)", "為放進上下文視窗,已移出最早的 {0} 則訊息(介面與歷史仍在)", "コンテキスト長に収めるため、最も古い {0} 件を送信対象から外しました(表示と履歴には残っています)", "컨텍스트 창에 맞추기 위해 가장 오래된 {0}개를 전송에서 제외했습니다(화면과 기록에는 남아 있음)"],
        ["CacheShort"] = ["cache", "缓存", "快取", "キャッシュ", "캐시"],
        ["UsageCacheLine"] = ["Prompt cache hit: {0} / {1} ({2}%)", "缓存命中:{0} / {1}({2}%)", "快取命中:{0} / {1}({2}%)", "キャッシュヒット: {0} / {1}({2}%)", "캐시 적중: {0} / {1}({2}%)"],
        ["UsageCacheTotalsLine"] = ["Cache total: read {0} / written {1}", "缓存累计:读取 {0} / 写入 {1}", "快取累計:讀取 {0} / 寫入 {1}", "キャッシュ累計: 読み取り {0} / 書き込み {1}", "캐시 누적: 읽기 {0} / 쓰기 {1}"],
        ["UsageCostLine"] = ["Estimated cost: {0} (at your configured rates)", "预计花费:{0}(按你在接入里填的单价)", "預計花費:{0}(按你在接入裡填的單價)", "推定コスト: {0}(設定した単価による)", "예상 비용: {0}(설정한 단가 기준)"],
        ["UsageLimitsLine"] = ["Max output {0} · context window {1}", "最大输出 {0} · 上下文窗口 {1}", "最大輸出 {0} · 上下文視窗 {1}", "最大出力 {0} · コンテキスト {1}", "최대 출력 {0} · 컨텍스트 {1}"],
        ["ApprovalTitle"] = ["The agent wants to run:", "Agent 请求执行:", "Agent 請求執行:", "エージェントが実行を要求:", "에이전트가 실행을 요청:"],
        ["Approve"] = ["Approve", "批准", "批准", "承認", "승인"],
        ["ApproveAlways"] = ["Always in this chat", "本次会话总是允许", "本次會話總是允許", "この会話では常に許可", "이 대화에서는 항상 허용"],
        // 按钮上必须写清"总是允许的到底是什么":记忆键是按命令名分的(run_command:du),
        // 只写「本次会话总是允许」会被读成"这段对话里什么都别再问了" —— 用户实测就是这么理解的。
        ["ApproveAlwaysKey"] = ["Always allow “{0}”", "总是允许「{0}」", "總是允許「{0}」", "「{0}」を常に許可", "「{0}」 항상 허용"],
        ["ApproveAlwaysTip"] = ["Stop asking for “{0}” until this chat ends (not saved to settings).", "本次会话内不再询问「{0}」(不写入设置,换会话即失效)。", "本次會話內不再詢問「{0}」(不寫入設定,換會話即失效)。", "この会話が終わるまで「{0}」は確認しません(設定には保存されません)。", "이 대화가 끝날 때까지 「{0}」은(는) 묻지 않습니다(설정에 저장되지 않음)."],
        ["Retrying"] = ["Connection failed, retrying ({0})…", "连接失败,正在重试({0})…", "連線失敗,正在重試({0})…", "接続に失敗、再試行中({0})…", "연결 실패, 재시도 중({0})…"],
        ["Deny"] = ["Deny", "拒绝", "拒絕", "拒否", "거부"],
        ["ToolRunning"] = ["running…", "执行中…", "執行中…", "実行中…", "실행 중…"],
        ["ToolDone"] = ["done", "完成", "完成", "完了", "완료"],
        // 代码块头部两个按钮的提示(LiveMarkdown 模板自带的文案是写死英文,这里覆盖)
        ["Copy"] = ["Copy", "复制", "複製", "コピー", "복사"],
        ["ToggleWrap"] = ["Toggle wrap", "切换自动换行", "切換自動換行", "折り返しの切替", "줄바꿈 전환"],
        ["Error"] = ["Error", "错误", "錯誤", "エラー", "오류"],
        // 作为回复气泡头部的一段(「助手 · 12.3s · 已取消」),不带句末标点
        ["Cancelled"] = ["stopped", "已取消", "已取消", "キャンセル", "취소됨"],
        ["Providers"] = ["Providers & models", "供应商与模型", "供應商與模型", "プロバイダーとモデル", "프로바이더와 모델"],
        // 设置页左栏两层树 + 供应商/模型两套表单
        ["AddProvider"] = ["New provider", "新增供应商", "新增供應商", "プロバイダーを追加", "프로바이더 추가"],
        ["AddModel"] = ["New model", "新增模型", "新增模型", "モデルを追加", "모델 추가"],
        ["SecProvider"] = ["Provider", "供应商", "供應商", "プロバイダー", "프로바이더"],
        ["DefaultProtocol"] = ["Default protocol", "默认协议", "預設協議", "既定のプロトコル", "기본 프로토콜"],
        ["DefaultProtocolHint"] = ["Models under this provider use it unless they pick their own.", "该供应商下的模型默认走它;单个模型可以另选。", "該供應商下的模型預設走它;單個模型可以另選。", "このプロバイダー配下のモデルは既定でこれを使います(モデルごとに変更可)。", "이 프로바이더의 모델은 기본으로 이를 사용합니다(모델별로 변경 가능)."],
        ["ProviderKeyHint"] = ["Stored encrypted via the host secret store. Shared by every model under this provider unless a model brings its own key.", "经宿主机密存储加密保存。该供应商下所有模型共用,除非某个模型自带 Key。", "經宿主機密儲存加密保存。該供應商下所有模型共用,除非某個模型自帶 Key。", "ホストのシークレットストアで暗号化保存。配下の全モデルで共用します(モデル独自のキーがある場合を除く)。", "호스트 시크릿 저장소에 암호화 저장. 이 프로바이더의 모든 모델이 공용합니다(모델 자체 키가 있으면 제외)."],
        ["InheritProtocol"] = ["Inherit from provider ({0})", "继承供应商({0})", "繼承供應商({0})", "プロバイダーに従う({0})", "프로바이더 상속({0})"],
        ["OwnApiKey"] = ["Use a separate API Key for this model", "此模型使用独立的 API Key", "此模型使用獨立的 API Key", "このモデルに個別の API キーを使う", "이 모델에 별도 API 키 사용"],
        ["OwnApiKeyHint"] = ["Off = the provider's key is used.", "关闭则沿用供应商的 Key。", "關閉則沿用供應商的 Key。", "オフならプロバイダーのキーを使います。", "끄면 프로바이더의 키를 사용합니다."],
        ["BaseUrlOverride"] = ["Base URL override (optional)", "覆盖基地址(可选)", "覆蓋基底位址(可選)", "ベース URL の上書き(任意)", "베이스 URL 재정의(선택)"],
        ["BaseUrlOverrideHint"] = ["Leave blank to use the provider's base URL. Only for the odd model that lives on a different path.", "留空即用供应商的基地址;只有个别模型路径不同时才需要填。", "留空即用供應商的基底位址;只有個別模型路徑不同時才需要填。", "空欄ならプロバイダーのベース URL を使います。パスが違う特殊なモデルのみ指定してください。", "비워 두면 프로바이더의 베이스 URL을 사용합니다. 경로가 다른 특수한 모델에만 지정하세요."],
        ["Unnamed"] = ["(unnamed)", "(未命名)", "(未命名)", "(名称未設定)", "(이름 없음)"],
        ["NoModels"] = ["No models yet — click \"New model\" to add one under this provider.", "还没有模型 —— 点「新增模型」在该供应商下添加。", "還沒有模型 —— 點「新增模型」在該供應商下新增。", "モデルがありません — 「モデルを追加」でこのプロバイダーに追加してください。", "모델이 없습니다 — \"모델 추가\"로 이 프로바이더에 추가하세요."],
        ["DeleteProviderConfirm"] = ["Click again to delete this provider and its {0} model(s)", "再点一次将删除该供应商及其 {0} 个模型", "再點一次將刪除該供應商及其 {0} 個模型", "もう一度クリックでこのプロバイダーと {0} 個のモデルを削除", "한 번 더 클릭하면 이 프로바이더와 모델 {0}개를 삭제합니다"],
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
        // 模型表单里的这两个字段与供应商那边的"名称"同框,只写"名称/模型"分不清哪个是给人看的、
        // 哪个是要原样发给服务端的(设计图 E)。
        ["DisplayName"] = ["Display name", "显示名称", "顯示名稱", "表示名", "표시 이름"],
        ["ModelId"] = ["Model ID", "模型 ID", "模型 ID", "モデル ID", "모델 ID"],
        ["MaxTokens"] = ["Max output tokens", "最大输出 tokens", "最大輸出 tokens", "最大出力トークン", "최대 출력 토큰"],
        ["MaxInputTokens"] = ["Context window (max input tokens)", "上下文窗口(最大输入 tokens)", "上下文視窗(最大輸入 tokens)", "コンテキスト長(最大入力トークン)", "컨텍스트 창(최대 입력 토큰)"],
        ["MaxInputTokensHint"] = ["Only used for the usage ratio under the input box; 0 = unknown.", "只用于输入框下方的用量占比;填 0 表示未知。", "只用於輸入框下方的用量占比;填 0 表示未知。", "入力欄下の使用率表示にのみ使用します(0 = 不明)。", "입력창 아래 사용률 표시에만 쓰입니다(0 = 알 수 없음)."],
        ["Reasoning"] = ["Thinking", "思考过程", "思考過程", "思考プロセス", "사고 과정"],
        ["ReasoningHint"] = ["\"Provider default\" usually means no reasoning is requested and none comes back — pick a level to actually see the thinking. Models that don't support it ignore the parameter.", "「跟随接入默认」通常等于不请求思考,也就看不到思考过程 —— 想看就选一个档位。不支持的模型会忽略该参数。", "「跟隨接入預設」通常等於不請求思考,也就看不到思考過程 —— 想看就選一個檔位。不支援的模型會忽略該參數。", "「プロバイダー既定」は通常「思考を要求しない」= 思考は返ってきません。表示したいならレベルを選んでください(非対応のモデルはこのパラメーターを無視します)。", "「프로바이더 기본값」은 보통 사고 과정을 요청하지 않아 아무것도 오지 않습니다 — 보려면 레벨을 고르세요(미지원 모델은 이 매개변수를 무시)."],
        ["PromptCache"] = ["Prompt caching", "提示词缓存", "提示詞快取", "プロンプトキャッシュ", "프롬프트 캐싱"],
        ["PromptCacheHint"] = ["Anthropic only. Caches the system prompt and the conversation so far, so repeated context is billed at the (much cheaper) cache rate. Prefixes below the minimum cacheable length are simply ignored — nothing extra is charged.", "仅 Anthropic 有效。把系统提示词与已有对话缓存起来,重复的上下文按缓存价计费(便宜一个数量级)。短于最小可缓存长度的前缀会被服务端直接忽略,不会多花钱。", "僅 Anthropic 有效。把系統提示詞與已有對話快取起來,重複的上下文按快取價計費(便宜一個數量級)。短於最小可快取長度的前綴會被伺服器直接忽略,不會多花錢。", "Anthropic のみ。システムプロンプトとこれまでの会話をキャッシュし、繰り返し送る文脈をキャッシュ料金(大幅に安価)で課金します。最小キャッシュ長に満たない前置きは無視されるだけで、追加課金はありません。", "Anthropic 전용. 시스템 프롬프트와 지금까지의 대화를 캐시해 반복되는 컨텍스트를 훨씬 저렴한 캐시 요금으로 청구합니다. 최소 캐시 길이에 못 미치는 접두부는 그냥 무시되며 추가 비용이 없습니다."],
        ["ReasoningDefault"] = ["Don't ask for thinking (provider default)", "不请求思考过程(接入默认)", "不請求思考過程(接入預設)", "思考プロセスを要求しない(プロバイダー既定)", "사고 과정을 요청하지 않음(프로바이더 기본값)"],
        ["ReasoningOff"] = ["Off", "关闭", "關閉", "オフ", "끔"],
        ["ReasoningLow"] = ["Low", "低", "低", "低", "낮음"],
        ["ReasoningMedium"] = ["Medium", "中", "中", "中", "보통"],
        ["ReasoningHigh"] = ["High", "高", "高", "高", "높음"],
        ["ReasoningOffHint"] = ["This model's Thinking setting is \"Don't ask for thinking\", so no thinking is requested and none comes back. Pick a level in the model settings to see it.", "当前模型的「思考过程」设为不请求,所以服务端不会返回思考内容 —— 想看就在模型配置里选一个档位。", "目前模型的「思考過程」設為不請求,所以伺服器不會回傳思考內容 —— 想看就在模型設定裡選一個檔位。", "このモデルの「思考プロセス」は「要求しない」に設定されているため、思考は返ってきません。表示したいならモデル設定でレベルを選んでください。", "이 모델의 「사고 과정」이 \"요청하지 않음\"으로 설정되어 있어 사고 내용이 오지 않습니다. 보려면 모델 설정에서 레벨을 선택하세요."],
        ["Temperature"] = ["Temperature", "温度", "溫度", "温度", "온도"],
        ["TopP"] = ["Top-P", "Top-P", "Top-P", "Top-P", "Top-P"],
        ["StopSequences"] = ["Stop (one per line)", "停止序列(每行一条)", "停止序列(每行一條)", "停止シーケンス(1 行に 1 つ)", "정지 시퀀스(줄마다 하나)"],
        ["SamplingHint"] = ["Leave blank to let the provider decide. Note: Anthropic models with thinking on only accept temperature 1 (or blank).", "留空即使用服务端默认。注意:开了思考的 Anthropic 模型只接受温度 1(或留空)。", "留空即使用伺服器預設。注意:開了思考的 Anthropic 模型只接受溫度 1(或留空)。", "空欄ならプロバイダー既定に従います。注意:思考を有効にした Anthropic モデルは temperature 1(または空欄)のみ受け付けます。", "비워 두면 프로바이더 기본값을 사용합니다. 참고: 사고를 켠 Anthropic 모델은 temperature 1(또는 공란)만 허용합니다."],
        ["PriceIn"] = ["Price / M input", "输入单价 / 百万", "輸入單價 / 百萬", "入力単価 / 100万", "입력 단가 / 100만"],
        ["PriceOut"] = ["Price / M output", "输出单价 / 百万", "輸出單價 / 百萬", "出力単価 / 100万", "출력 단가 / 100만"],
        ["PriceCached"] = ["Price / M cached", "缓存单价 / 百万", "快取單價 / 百萬", "キャッシュ単価 / 100万", "캐시 단가 / 100만"],
        ["PriceHint"] = ["Only used to estimate spend in the usage tooltip. Currency is whatever you type — leave 0 to skip the estimate.", "只用于用量提示里的花费估算。币种由你自己心里有数;留 0 就不估算。", "只用於用量提示裡的花費估算。幣別由你自己心裡有數;留 0 就不估算。", "使用量ツールチップでの概算にのみ使います。通貨は任意、0 なら概算しません。", "사용량 툴팁의 비용 추정에만 쓰입니다. 통화는 자유이며 0이면 추정하지 않습니다."],
        ["ProviderPrompt"] = ["System prompt for this model (overrides the global one)", "本模型专用的系统提示词(盖过全局那份)", "本模型專用的系統提示詞(蓋過全域那份)", "このモデル専用のシステムプロンプト(全体設定より優先)", "이 모델 전용 시스템 프롬프트(전역 설정보다 우선)"],
        ["ApiKey"] = ["API Key", "API Key", "API Key", "API キー", "API 키"],
        ["ApiKeyHint"] = ["Stored encrypted via the host secret store.", "经宿主机密存储加密保存。", "經宿主機密儲存加密保存。", "ホストのシークレットストアで暗号化保存されます。", "호스트 시크릿 저장소에 암호화되어 저장됩니다."],
        ["SystemPrompt"] = ["System prompt (optional)", "系统提示词(可选)", "系統提示詞(可選)", "システムプロンプト(任意)", "시스템 프롬프트(선택)"],
        // 输入框上方的建议药丸:起手提示是本地文案(不花钱),后续提问由模型给
        ["Starter1"] = ["Explain the latest terminal output", "解释刚才的终端输出", "解釋剛才的終端輸出", "直前のターミナル出力を説明して", "방금 터미널 출력을 설명해줘"],
        ["Starter2"] = ["Check this server's disk usage", "看看这台服务器的磁盘占用", "看看這台伺服器的磁碟佔用", "このサーバーのディスク使用量を確認して", "이 서버의 디스크 사용량을 확인해줘"],
        ["Starter3"] = ["Which services are listening on ports?", "有哪些服务在监听端口?", "有哪些服務在監聽連接埠?", "どのサービスがポートを待ち受けている?", "어떤 서비스가 포트를 열고 있어?"],
        // 上下文压缩
        ["Compacting"] = ["Compacting earlier context…", "正在压缩早期上下文…", "正在壓縮早期上下文…", "以前の文脈を圧縮中…", "이전 문맥을 압축하는 중…"],
        ["Compacted"] = ["Compacted {0} earlier messages — click to read the digest", "已把早期 {0} 条消息压缩成摘要 —— 点开可读", "已把早期 {0} 則訊息壓縮成摘要 —— 點開可讀", "以前のメッセージ {0} 件を要約に圧縮しました(クリックで表示)", "이전 메시지 {0}개를 요약으로 압축했습니다(클릭하면 표시)"],
        ["CompactContext"] = ["Compact context when it fills up", "上下文快满时自动压缩", "上下文快滿時自動壓縮", "コンテキストが埋まりかけたら自動で圧縮", "컨텍스트가 찰 때 자동 압축"],
        ["CompactContextHint"] = ["Near the window limit, fold the earlier conversation into a factual digest and keep the recent turns verbatim — instead of simply dropping the oldest messages. Costs one extra request when it triggers; needs \"context window\" to be set above.", "接近上下文窗口时,把早期对话折成一段事实摘要、近几轮保持原文 —— 而不是直接丢掉最早的几条。触发时多花一次请求;需要上面填了\"上下文窗口\"才生效。", "接近上下文視窗時,把早期對話折成一段事實摘要、近幾輪保持原文 —— 而不是直接丟掉最早的幾條。觸發時多花一次請求;需要上面填了\"上下文視窗\"才生效。", "コンテキスト長に近づいたら、古い会話を事実の要約に畳み、直近のやり取りは原文のまま残します(単に古いものを捨てるのではなく)。発動時にリクエストが 1 回増えます。上の「コンテキスト長」の設定が必要です。", "컨텍스트 한계에 가까워지면 이전 대화를 사실 요약으로 접고 최근 대화는 원문 그대로 유지합니다(오래된 것을 그냥 버리지 않음). 발동 시 요청이 한 번 추가되며, 위의 \"컨텍스트 창\" 설정이 필요합니다."],
        ["SuggestFollowUps"] = ["Suggest follow-up questions", "推荐后续提问", "推薦後續提問", "続きの質問を提案する", "후속 질문 제안"],
        ["SuggestFollowUpsHint"] = ["After each reply, ask the model for a few short follow-ups and show them above the input box. Costs one extra small request per reply; the starter prompts in an empty chat are local and always free.", "每轮回答后额外问一次模型,把几条后续提问显示在输入框上方。每轮多花一次(很小的)请求;空会话里的起手提示是本地文案,始终不花钱。", "每輪回答後額外問一次模型,把幾條後續提問顯示在輸入框上方。每輪多花一次(很小的)請求;空會話裡的起手提示是本地文案,始終不花錢。", "回答のたびにモデルへ追加で一度問い合わせ、続きの質問を入力欄の上に表示します。回答ごとに小さなリクエストが 1 回増えます(空のチャットの初期候補はローカル文言で無料)。", "답변마다 모델에 한 번 더 물어 후속 질문을 입력창 위에 표시합니다. 답변당 작은 요청이 한 번 추가됩니다(빈 대화의 시작 제안은 로컬 문구라 무료)."],
        ["McpServers"] = ["MCP servers", "MCP 服务器", "MCP 伺服器", "MCP サーバー", "MCP 서버"],
        ["McpHint"] = ["Model Context Protocol servers add extra tools to Agent mode. Tools that may modify state ask for approval before running.", "MCP(Model Context Protocol)服务器为 Agent 模式提供额外工具;可能修改状态的工具执行前会请求审批。", "MCP(Model Context Protocol)伺服器為 Agent 模式提供額外工具;可能修改狀態的工具執行前會請求審批。", "MCP(Model Context Protocol)サーバーは Agent モードにツールを追加します。状態を変更しうるツールは実行前に承認を求めます。", "MCP(Model Context Protocol) 서버는 Agent 모드에 도구를 추가합니다. 상태를 변경할 수 있는 도구는 실행 전 승인을 요청합니다."],
        ["McpEnabled"] = ["Enabled", "启用", "啟用", "有効", "사용"],
        // 「配置工具」左栏的一句话:说清左右两边是什么关系
        ["McpPaneHint"] = ["Once a server is set up, its tools show up on the right for you to check.", "配好服务器后,它带来的工具直接在右侧勾选。", "設定好伺服器後,它帶來的工具直接在右側勾選。", "サーバーを設定すると、そのツールが右側に並んで選べます。", "서버를 설정하면 그 도구가 오른쪽에 나와 선택할 수 있습니다."],
        ["McpAdd"] = ["New server", "新增服务器", "新增伺服器", "サーバーを追加", "서버 추가"],
        ["ToolsChecked"] = ["{0}/{1} checked", "已勾 {0}/{1}", "已勾 {0}/{1}", "{0}/{1} 選択", "{0}/{1} 선택"],
        // 设置页的分节标题(版式对齐宿主设置页:节标题 + 一张描边卡片)
        // 这一节装的是名称 / 模型 id / 协议 / Key / 基地址 —— 叫「模型」太窄了,它整体是"怎么接上去"
        ["SecEndpoint"] = ["Endpoint", "接入端点", "接入端點", "接続エンドポイント", "접속 엔드포인트"],
        ["SecCapacity"] = ["Capacity & thinking", "容量与思考", "容量與思考", "上限と思考", "용량과 사고"],
        ["SecSampling"] = ["Sampling, pricing & prompt", "采样、计费与提示词", "採樣、計費與提示詞", "サンプリング・料金・プロンプト", "샘플링·요금·프롬프트"],
        ["SecGlobal"] = ["Global", "全局", "全域", "全体設定", "전역"],
        ["SecWebSearch"] = ["Web search", "网络检索", "網路檢索", "ウェブ検索", "웹 검색"],
        ["WebEnabled"] = ["Let the assistant search and read the web", "允许助手检索并阅读网页", "允許助手檢索並閱讀網頁", "アシスタントにウェブの検索と閲覧を許可する", "어시스턴트가 웹을 검색하고 읽도록 허용"],
        ["WebEnabledHint"] = ["Adds two read-only tools — web_search (a list of results) and web_fetch (one page as text). Available in Plan and Agent modes.", "会多出两个只读工具:web_search(结果清单)与 web_fetch(把一个网页取成文本)。计划模式与 Agent 模式下可用。", "會多出兩個唯讀工具:web_search(結果清單)與 web_fetch(把一個網頁取成文字)。計畫模式與 Agent 模式下可用。", "読み取り専用のツールが 2 つ増えます:web_search(結果一覧)と web_fetch(1 ページをテキスト化)。プラン/エージェントモードで使えます。", "읽기 전용 도구 두 개가 추가됩니다: web_search(결과 목록)와 web_fetch(한 페이지를 텍스트로). 플랜·에이전트 모드에서 사용할 수 있습니다."],
        ["WebSearxUrl"] = ["SearXNG instance", "SearXNG 实例地址", "SearXNG 實例位址", "SearXNG インスタンス", "SearXNG 인스턴스"],
        ["WebSearxHint"] = ["Defaults to a shared instance run by VelaShell, so search works out of the box — but your queries pass through it. Point this at your own instance for full control: docker run -d -p 8080:8080 searxng/searxng (its settings.yml must list 'json' under search.formats). Clear the field to turn search off; web_fetch still works.", "默认走 VelaShell 提供的公共实例,装完就能搜 —— 但你的搜索词会经过它。想完全自己掌控就换成自建地址:docker run -d -p 8080:8080 searxng/searxng(实例的 settings.yml 要把 json 加进 search.formats)。清空此项即关闭检索,web_fetch 不受影响。", "預設走 VelaShell 提供的公共實例,裝完就能搜 —— 但你的搜尋詞會經過它。想完全自己掌控就換成自建位址:docker run -d -p 8080:8080 searxng/searxng(實例的 settings.yml 要把 json 加進 search.formats)。清空此項即關閉檢索,web_fetch 不受影響。", "既定では VelaShell が運用する共有インスタンスを使うため、そのまま検索できます — ただし検索語はそこを経由します。完全に自分で管理したい場合は自前のインスタンスを指定してください:docker run -d -p 8080:8080 searxng/searxng(settings.yml の search.formats に json が必要)。空にすると検索は無効になります(web_fetch は影響を受けません)。", "기본값은 VelaShell이 운영하는 공용 인스턴스라 설치 직후 바로 검색됩니다 — 다만 검색어가 그곳을 거칩니다. 완전히 직접 관리하려면 자체 인스턴스를 지정하세요: docker run -d -p 8080:8080 searxng/searxng (settings.yml의 search.formats에 json 필요). 비우면 검색이 꺼지며 web_fetch는 영향받지 않습니다."],
        ["WebMaxResults"] = ["Results per search", "每次检索返回条数", "每次檢索回傳條數", "1 回の検索で返す件数", "검색당 결과 수"],
        ["WebNative"] = ["Prefer the model provider's own web search when available", "模型自带服务端检索时优先用它", "模型自帶服務端檢索時優先用它", "モデル側のウェブ検索が使えるときはそちらを優先する", "모델 제공자의 자체 웹 검색이 있으면 우선 사용"],
        ["WebNativeHint"] = ["Anthropic Messages and OpenAI Responses can search on their side: no key of yours, and results come with citations. Other protocols (Chat Completions, Ollama, most relays) fall back to the backend above.", "Anthropic Messages 与 OpenAI Responses 能在它们那侧检索:不用你的 Key,结果自带引用。其余协议(Chat Completions、Ollama、多数中转站)回落到上面选的后端。", "Anthropic Messages 與 OpenAI Responses 能在它們那側檢索:不用你的 Key,結果自帶引用。其餘協定(Chat Completions、Ollama、多數中轉站)回落到上面選的後端。", "Anthropic Messages と OpenAI Responses はプロバイダー側で検索できます(自前のキー不要、引用付き)。その他のプロトコル(Chat Completions、Ollama、多くの中継)は上のバックエンドにフォールバックします。", "Anthropic Messages와 OpenAI Responses는 제공자 쪽에서 검색할 수 있습니다(내 키 불필요, 인용 포함). 다른 프로토콜(Chat Completions, Ollama, 대부분의 중계)은 위 백엔드로 대체됩니다."],
        ["WebPrivate"] = ["Allow private and loopback addresses", "放行私网与本机地址", "放行私網與本機位址", "プライベート/ループバックアドレスを許可", "사설·루프백 주소 허용"],
        ["WebPrivateHint"] = ["Off by default: the plugin runs on your machine, so an unrestricted fetch is an intranet probe — and on a cloud host 169.254.169.254 is the metadata service. Prefer listing the few hosts you need below.", "默认关:插件跑在你自己的机器上,不设限的抓取等于一个内网探测器 —— 云主机上 169.254.169.254 就是元数据服务。更稳妥的做法是在下面列出你确实需要的那几台。", "預設關:外掛跑在你自己的機器上,不設限的抓取等於一個內網探測器 —— 雲主機上 169.254.169.254 就是中繼資料服務。更穩妥的做法是在下面列出你確實需要的那幾台。", "既定はオフです。プラグインは自分のマシンで動くため、無制限の取得は社内ネットワークの探索と同じです(クラウドでは 169.254.169.254 がメタデータサービスです)。必要なホストだけを下に列挙するほうが安全です。", "기본값은 꺼짐입니다. 플러그인은 사용자의 머신에서 실행되므로 제한 없는 요청은 내부망 탐색과 같습니다(클라우드에서 169.254.169.254는 메타데이터 서비스입니다). 필요한 호스트만 아래에 나열하는 편이 안전합니다."],
        ["WebAllowedHosts"] = ["Always-allowed internal hosts (one per line)", "始终放行的内网主机(每行一条)", "始終放行的內網主機(每行一條)", "常に許可する内部ホスト(1 行に 1 つ)", "항상 허용할 내부 호스트(한 줄에 하나)"],
        ["WebAllowedHostsHint"] = ["Host or host:port, for example 127.0.0.1:8080 for a local SearXNG. These are allowed even when the switch above is off.", "写主机名或 host:port,例如本机 SearXNG 的 127.0.0.1:8080。即使上面的开关关着,这里列出的也放行。", "寫主機名或 host:port,例如本機 SearXNG 的 127.0.0.1:8080。即使上面的開關關著,這裡列出的也放行。", "ホスト名または host:port(例:ローカル SearXNG の 127.0.0.1:8080)。上のスイッチがオフでもここに書いたものは許可されます。", "호스트명 또는 host:port(예: 로컬 SearXNG의 127.0.0.1:8080). 위 스위치가 꺼져 있어도 여기 나열한 것은 허용됩니다."],
        // 回复正文里的链接(远端路径 = 下载下来)
        ["LinkDownload"] = ["Save the file from the server", "把服务器上的文件保存到本地", "把伺服器上的檔案儲存到本機", "サーバー上のファイルを保存", "서버의 파일을 저장"],
        ["LinkDownloading"] = ["Downloading {0}…", "正在下载 {0}…", "正在下載 {0}…", "{0} をダウンロード中…", "{0} 다운로드 중…"],
        ["LinkDownloaded"] = ["Saved to {0}", "已保存到 {0}", "已儲存到 {0}", "{0} に保存しました", "{0} 에 저장했습니다"],
        ["LinkMissingRemote"] = ["No such file on the server: {0}", "服务器上没有这个文件:{0}", "伺服器上沒有這個檔案:{0}", "サーバーにそのファイルはありません: {0}", "서버에 그런 파일이 없습니다: {0}"],
        ["LinkMissingLocal"] = ["No such local file: {0}", "本机没有这个文件:{0}", "本機沒有這個檔案:{0}", "ローカルにそのファイルはありません: {0}", "로컬에 그런 파일이 없습니다: {0}"],
        ["LinkIsDirectory"] = ["{0} is a directory, not a file.", "{0} 是目录,不是文件。", "{0} 是目錄,不是檔案。", "{0} はディレクトリです(ファイルではありません)。", "{0} 은(는) 디렉터리입니다."],
        ["LinkTooBig"] = ["Larger than {0} MB — use the file panel's transfer queue for this one.", "超过 {0} MB —— 这种请走文件面板的传输队列。", "超過 {0} MB —— 這種請走檔案面板的傳輸佇列。", "{0} MB を超えています —— ファイルパネルの転送キューをお使いください。", "{0} MB 초과 —— 파일 패널의 전송 큐를 사용하세요."],
        ["LinkUnsupported"] = ["Don't know how to open: {0}", "不知道该怎么打开:{0}", "不知道該怎麼打開:{0}", "開き方が分かりません: {0}", "여는 방법을 알 수 없습니다: {0}"],
        ["McpNoServers"] = ["No MCP servers yet.\nAdd one below.", "还没有 MCP 服务器。\n点下方「新增」添加一台。", "還沒有 MCP 伺服器。\n點下方「新增」新增一台。", "MCP サーバーがまだありません。\n下の「追加」から登録してください。", "MCP 서버가 아직 없습니다.\n아래 「추가」로 등록하세요."],
        ["McpNoSelection"] = ["Pick a server on the left to configure it.", "在左侧选一台服务器来配置。", "在左側選一台伺服器來設定。", "左のリストからサーバーを選んでください。", "왼쪽 목록에서 서버를 선택하세요."],
        ["McpToolCount"] = ["{0} tools", "{0} 个工具", "{0} 個工具", "ツール {0} 件", "도구 {0}개"],
        ["McpToolsNotLoaded"] = ["tools not loaded", "工具未拉取", "工具未拉取", "ツール未取得", "도구 미조회"],
        ["McpDisabledMark"] = ["disabled", "已停用", "已停用", "無効", "사용 안 함"],
        ["McpToolsKnown"] = ["Tool library: {0} tools · updated {1}", "工具库:{0} 个 · 更新于 {1}", "工具庫:{0} 個 · 更新於 {1}", "ツールライブラリ:{0} 件 · 更新 {1}", "도구 라이브러리: {0}개 · 갱신 {1}"],
        ["McpToolsUnknown"] = ["Tool library not fetched yet — hit Test to connect and pull it in.", "工具库尚未拉取 —— 点「测试」连一次就会带回来。", "工具庫尚未拉取 —— 點「測試」連一次就會帶回來。", "ツールライブラリ未取得 —— 「テスト」で接続すると取り込まれます。", "도구 라이브러리 미조회 —— 「테스트」로 접속하면 가져옵니다."],
        ["McpStdioHint"] = ["Arguments go on one line; quote any fragment containing spaces.", "参数写在一行里,含空格的片段用引号括起来。", "參數寫在一行裡,含空格的片段用引號括起來。", "引数は 1 行に。空白を含む断片は引用符で囲みます。", "인수는 한 줄에 작성하고, 공백이 포함된 조각은 따옴표로 묶으세요."],
        ["McpHttpHint"] = ["Streamable HTTP / SSE is detected automatically.", "Streamable HTTP / SSE 会自动探测。", "Streamable HTTP / SSE 會自動探測。", "Streamable HTTP / SSE は自動判定します。", "Streamable HTTP / SSE 는 자동으로 판별합니다."],
        ["McpTransport"] = ["Transport", "传输方式", "傳輸方式", "トランスポート", "전송 방식"],
        ["McpCommand"] = ["Command (e.g. npx / uvx / python)", "命令(如 npx / uvx / python)", "命令(如 npx / uvx / python)", "コマンド(例:npx / uvx / python)", "명령(예: npx / uvx / python)"],
        ["McpArguments"] = ["Arguments (one line, quote items with spaces)", "参数(单行,含空格的片段用引号包裹)", "參數(單行,含空格的片段用引號包裹)", "引数(1 行、空白を含む場合は引用符で囲む)", "인수(한 줄, 공백 포함 시 따옴표 사용)"],
        ["McpWorkingDir"] = ["Working directory (optional)", "工作目录(可选)", "工作目錄(可選)", "作業ディレクトリ(任意)", "작업 디렉터리(선택)"],
        ["McpWorkingDirHint"] = ["Leave blank to use {0} (same tree as the logs). Supports a leading ~. MCP tools that write files put them here.", "留空即用 {0}(与日志同一棵目录树);支持 ~ 前缀。会写文件的 MCP 工具默认把文件落在这里。", "留空即用 {0}(與日誌同一棵目錄樹);支援 ~ 前綴。會寫檔案的 MCP 工具預設把檔案落在這裡。", "空欄なら {0}(ログと同じツリー)を使います。先頭の ~ に対応。ファイルを書く MCP ツールの出力先になります。", "비워 두면 {0}(로그와 같은 트리)을 사용합니다. 앞의 ~ 지원. 파일을 쓰는 MCP 도구의 출력이 여기에 놓입니다."],
        ["McpEnv"] = ["Environment variables (KEY=VALUE per line, optional)", "环境变量(每行一条 KEY=VALUE,可选)", "環境變數(每行一條 KEY=VALUE,可選)", "環境変数(1 行に KEY=VALUE、任意)", "환경 변수(줄마다 KEY=VALUE, 선택)"],
        ["McpUrl"] = ["Server URL", "服务器 URL", "伺服器 URL", "サーバー URL", "서버 URL"],
        ["McpHeaders"] = ["HTTP headers (Name: Value per line, optional)", "HTTP 请求头(每行一条 Name: Value,可选)", "HTTP 標頭(每行一條 Name: Value,可選)", "HTTP ヘッダー(1 行に Name: Value、任意)", "HTTP 헤더(줄마다 Name: Value, 선택)"],
        ["McpTestOk"] = ["Connected — {0} tool(s): {1}", "连接成功 —— {0} 个工具:{1}", "連接成功 —— {0} 個工具:{1}", "接続成功 — ツール {0} 件:{1}", "연결 성공 — 도구 {0}개: {1}"],
        ["McpConnecting"] = ["Connecting MCP servers…", "正在连接 MCP 服务器…", "正在連接 MCP 伺服器…", "MCP サーバーに接続中…", "MCP 서버 연결 중…"],
        ["ExplainPrompt"] = ["Explain the following terminal output. Point out any errors and suggest fixes:\n", "请解释以下终端输出,指出其中的错误并给出可能的解决办法:\n", "請解釋以下終端輸出,指出其中的錯誤並給出可能的解決辦法:\n", "以下のターミナル出力を説明し、エラーがあれば指摘して解決策を提案してください:\n", "다음 터미널 출력을 설명하고, 오류를 지적하고 해결 방법을 제안해 주세요:\n"],
        ["NoConnectedSession"] = ["No connected session.", "没有已连接的会话。", "沒有已連接的會話。", "接続中のセッションがありません。", "연결된 세션이 없습니다."],
        ["CmdChat"] = ["AI: Open Chat (Tab)", "AI:打开聊天(标签页)", "AI:開啟聊天(標籤頁)", "AI:チャットを開く(タブ)", "AI: 채팅 열기(탭)"],
        ["CmdChatWindow"] = ["AI: Open Chat (Window)", "AI:打开聊天(窗口)", "AI:開啟聊天(視窗)", "AI:チャットを開く(ウィンドウ)", "AI: 채팅 열기(창)"],
        ["CmdExplain"] = ["AI: Explain Terminal Output", "AI:解释终端输出", "AI:解釋終端輸出", "AI:ターミナル出力を説明", "AI: 터미널 출력 설명"],
        // 回复头部的阶段芯片:辉光只说明"在动",这几个字说明"在干什么"
        ["PhaseThinking"] = ["thinking", "正在思考", "正在思考", "思考中", "생각 중"],
        ["PhaseTool"] = ["running tools", "调用工具", "呼叫工具", "ツール実行中", "도구 실행 중"],
        ["PhaseWriting"] = ["writing", "正在生成", "正在生成", "生成中", "생성 중"],
        ["PhaseWaiting"] = ["waiting for you", "等待你确认", "等待你確認", "確認待ち", "확인 대기"],
        // 审批卡上的风险标签(按工具名归类,只说"会做什么",不做价值判断)
        ["RiskWrite"] = ["writes a remote file", "写远端文件", "寫遠端檔案", "リモートファイルに書き込み", "원격 파일 쓰기"],
        ["RiskExec"] = ["runs a command", "执行命令", "執行命令", "コマンド実行", "명령 실행"],
        ["RiskInput"] = ["types into the terminal", "写入终端", "寫入終端", "端末へ入力", "터미널 입력"],
        ["RiskMcp"] = ["external MCP tool", "外部 MCP 工具", "外部 MCP 工具", "外部 MCP ツール", "외부 MCP 도구"],
        ["ApprovalPaused"] = ["This turn stays paused until you decide.", "在你做出选择之前,这一轮是暂停的。", "在你做出選擇之前,這一輪是暫停的。", "選択するまでこのターンは停止しています。", "선택하기 전까지 이번 턴은 멈춰 있습니다."],
        // 失败卡:错误不该混在正文里当一段 Markdown
        ["ErrorKept"] = ["Whatever this turn already produced is kept.", "本轮已产生的内容保留。", "本輪已產生的內容保留。", "このターンで既に生成された内容は保持されます。", "이번 턴에서 이미 생성된 내용은 유지됩니다."],
        // 空状态(一个模型都没配):给下一步动作,而不是一句陈述
        ["EmptyTitle"] = ["No model available yet", "还没有可用的模型", "還沒有可用的模型", "利用できるモデルがありません", "사용할 수 있는 모델이 없습니다"],
        ["EmptyBody"] = ["Add a provider and a model, and you can ask this server anything right here.", "添加一个供应商和模型之后,就能直接对着当前这台服务器提问。", "新增一個供應商和模型之後,就能直接對著目前這台伺服器提問。", "プロバイダーとモデルを追加すれば、このサーバーについてそのまま質問できます。", "프로바이더와 모델을 추가하면 이 서버에 대해 바로 물어볼 수 있습니다."],
        ["EmptyAction"] = ["Add a model", "添加模型接入", "新增模型接入", "モデルを追加", "모델 추가"],
        ["EmptyExamples"] = ["Once it's set up, you can ask", "配好之后可以这样问", "設定好之後可以這樣問", "設定後はこんなふうに聞けます", "설정한 뒤에는 이렇게 물어볼 수 있습니다"],
        // 配好了、但这段对话还一个字都没有:中部别留一整块空白,说清它能替你看什么
        ["ReadyExamples"] = ["Try one of these", "可以这样问", "可以這樣問", "こんなふうに聞けます", "이렇게 물어볼 수 있습니다"],
        ["ReadyTitle"] = ["Ask this server anything", "问这台服务器点什么", "問這台伺服器點什麼", "このサーバーについて聞いてみましょう", "이 서버에 대해 물어보세요"],
        ["ReadyBody"] = ["The model can read this session's terminal output, and files on the server when you ask it to.", "模型能读到这个会话的终端输出,你让它看时也能读服务器上的文件。", "模型能讀到這個工作階段的終端輸出,你讓它看時也能讀伺服器上的檔案。", "モデルはこのセッションの端末出力を読めます。指示すればサーバー上のファイルも読みます。", "모델은 이 세션의 터미널 출력을 읽을 수 있고, 요청하면 서버의 파일도 읽습니다."],
        // 历史会话按日期分组
        ["HistToday"] = ["Today", "今天", "今天", "今日", "오늘"],
        ["HistYesterday"] = ["Yesterday", "昨天", "昨天", "昨日", "어제"],
        ["HistEarlier"] = ["Earlier", "更早", "更早", "それ以前", "이전"],
        // 设置页:密钥去向徽章 + 左栏的连通性状态点(本次窗口内测出来的)
        ["KeyEncrypted"] = ["encrypted by the host", "由宿主加密存储", "由宿主加密儲存", "ホストが暗号化して保存", "호스트가 암호화 저장"],
        ["DotUntested"] = ["Not tested in this window", "本次窗口内未测试", "本次視窗內未測試", "このウィンドウでは未テスト", "이 창에서 테스트하지 않음"],
        ["DotPassed"] = ["Test passed", "测试通过", "測試通過", "テスト成功", "테스트 통과"],
        ["DotFailed"] = ["Test failed", "测试失败", "測試失敗", "テスト失敗", "테스트 실패"],
        // 上下文占用条的悬停说明(条只给量感,数字仍在用量提示里)
        ["MeterTip"] = ["Share of the context window taken by the last turn.", "上一轮占掉了多大比例的上下文窗口。", "上一輪佔掉了多大比例的上下文視窗。", "直近のターンがコンテキスト長のどれだけを占めたか。", "직전 턴이 컨텍스트 창을 얼마나 차지했는지."]
    };
}
