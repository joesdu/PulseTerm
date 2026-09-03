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
        // 输入框旁边那个下拉用的短形:工具条只有几十像素宽,设置页那句完整说明进不去。
        // 「默认」在这里的含义与设置页的 ReasoningDefault 完全一致(不带 reasoning 参数)
        ["ReasoningAuto"] = ["Auto", "默认", "預設", "既定", "기본"],
        ["ReasoningTip"] = ["Thinking level for this model. Changing it here applies to this conversation only — it does not touch the model's saved setting.", "当前模型的思考档位。在这儿改只对本次对话生效,不会动模型配置里保存的那个值。", "目前模型的思考檔位。在這裡改只對本次對話生效,不會動模型設定裡儲存的那個值。", "このモデルの思考レベル。ここでの変更はこの会話にのみ適用され、モデルの保存設定は変わりません。", "이 모델의 사고 레벨. 여기서 바꾸면 이번 대화에만 적용되며 모델의 저장된 설정은 바뀌지 않습니다."],
        ["ReasoningOverrideTip"] = ["This conversation is using a thinking level different from the model's saved setting. Pick the saved one again to clear it; switching models clears it too.", "本次对话用的思考档位与模型配置里保存的不一样。选回保存的那一档即可取消;换模型也会自动取消。", "本次對話用的思考檔位與模型設定裡儲存的不一樣。選回儲存的那一檔即可取消;換模型也會自動取消。", "この会話はモデルの保存設定とは異なる思考レベルを使っています。保存された値を選び直すと解除されます。モデルを切り替えても解除されます。", "이 대화는 모델의 저장된 설정과 다른 사고 레벨을 사용 중입니다. 저장된 값을 다시 선택하면 해제되며, 모델을 바꿔도 해제됩니다."],
        ["ReasoningUnsupportedTip"] = ["This model doesn't do thinking (per models.dev), so there is no level to set.", "据 models.dev,这个模型不会思考,没有档位可调。", "據 models.dev,這個模型不會思考,沒有檔位可調。", "models.dev によれば、このモデルは思考しないため、設定できるレベルはありません。", "models.dev에 따르면 이 모델은 사고 기능이 없어 설정할 레벨이 없습니다."],
        ["ReasoningOffHint"] =["This model's Thinking setting is \"Don't ask for thinking\", so no thinking is requested and none comes back. Pick a level in the model settings to see it.", "当前模型的「思考过程」设为不请求,所以服务端不会返回思考内容 —— 想看就在模型配置里选一个档位。", "目前模型的「思考過程」設為不請求,所以伺服器不會回傳思考內容 —— 想看就在模型設定裡選一個檔位。", "このモデルの「思考プロセス」は「要求しない」に設定されているため、思考は返ってきません。表示したいならモデル設定でレベルを選んでください。", "이 모델의 「사고 과정」이 \"요청하지 않음\"으로 설정되어 있어 사고 내용이 오지 않습니다. 보려면 모델 설정에서 레벨을 선택하세요."],
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
        // ── 连接供应商(订阅登录)──────────────────────────────────────
        // 目录页与登录流程的全套文案。"登录 / 连接"两个词分工固定:
        // 「登录」是那个动作(按钮),「已连接 / 未连接」是那个状态(状态灯),别互相串。
        ["SetupProviders"] = ["Connect a provider", "连接供应商", "連接供應商", "プロバイダーを接続", "프로바이더 연결"],
        ["SetupHint"] = ["Click a provider to connect. Sign-in providers open your browser and come straight back — nothing to fill in. The rest ask for an API key and nothing else.", "点一下就连上:能登录的直接开浏览器,授权完自己跳回来,什么都不用填;其余的只问一把 API Key,别的都不问。", "點一下就連上:能登入的直接開瀏覽器,授權完自己跳回來,什麼都不用填;其餘的只問一把 API Key,別的都不問。", "クリックするだけで接続します。ログイン対応のものはブラウザーが開き、認証後そのまま戻ってきます(入力は不要)。それ以外は API キーだけを尋ねます。", "클릭 한 번으로 연결됩니다. 로그인을 지원하는 곳은 브라우저가 열리고 인증 후 바로 돌아옵니다(입력 불필요). 나머지는 API 키만 물어봅니다."],
        ["SetupFootnote"] = ["Sign-in uses an OAuth app VelaShell registers with each provider. Where that registration is still pending, the row asks for a client ID once — or you can register one yourself from the link it shows.", "登录用的是 VelaShell 在各家注册的 OAuth 应用。还没注册下来的那几家,行里会问一次客户端 id —— 你也可以照它给的链接自己去注册一个填进来。", "登入用的是 VelaShell 在各家註冊的 OAuth 應用。還沒註冊下來的那幾家,列裡會問一次用戶端 id —— 你也可以照它給的連結自己去註冊一個填進來。", "ログインには VelaShell が各社に登録した OAuth アプリを使います。登録が未了のものは一度だけクライアント ID を尋ねます(表示されるリンクから自分で登録することもできます)。", "로그인은 VelaShell이 각 업체에 등록한 OAuth 앱을 사용합니다. 아직 등록되지 않은 곳은 클라이언트 ID를 한 번만 물어봅니다(표시된 링크에서 직접 등록해도 됩니다)."],
        ["SetupSignInHint"] = ["Your browser is opening. Finish there and this window takes over — the credential is stored encrypted and the model is set up for you.", "正在打开浏览器。在那边授权完就行,这边会自动接上 —— 凭据加密存好,模型也一并配好。", "正在開啟瀏覽器。在那邊授權完就行,這邊會自動接上 —— 憑據加密存好,模型也一併配好。", "ブラウザーを開いています。そちらで認証を終えれば自動で引き継ぎます。資格情報は暗号化保存され、モデルも設定済みになります。", "브라우저를 여는 중입니다. 거기서 인증만 마치면 자동으로 이어받습니다. 자격 증명은 암호화 저장되고 모델도 함께 설정됩니다."],
        ["SetupModelHint"] = ["A factory example — change it to a model this provider actually serves.", "出厂示例,按这家实际可用的型号改。", "出廠範例,按這家實際可用的型號改。", "出荷時の例です。実際に使える型番に変更してください。", "출고 시 예시입니다. 실제 사용 가능한 모델로 바꾸세요."],
        // 连不上(网络层),不是服务端拒绝 —— 指向宿主的代理设置,别让人去翻 Key
        ["ErrorUnreachable"] = ["could not reach the provider at all. This is the network, not your key: check the connection, and if this network needs a proxy, turn one on in Settings → Proxy (VelaShell does not follow the system proxy unless you pick “Follow system” there).", "根本没连上这家供应商。这是网络层的问题,不是 Key:检查网络;如果这个网络需要代理,去「设置 → 代理」打开(VelaShell 默认不跟随系统代理,得在那里选「跟随系统」或填代理地址)。", "根本沒連上這家供應商。這是網路層的問題,不是 Key:檢查網路;如果這個網路需要代理,去「設定 → 代理」開啟(VelaShell 預設不跟隨系統代理,得在那裡選「跟隨系統」或填代理位址)。", "プロバイダーに接続できませんでした。キーではなくネットワークの問題です。接続を確認し、プロキシが必要な環境なら「設定 → プロキシ」で有効にしてください(VelaShell は既定ではシステムプロキシに従いません)。", "프로바이더에 아예 연결하지 못했습니다. 키가 아니라 네트워크 문제입니다. 연결을 확인하고, 프록시가 필요한 환경이면 「설정 → 프록시」에서 켜세요(VelaShell은 기본적으로 시스템 프록시를 따르지 않습니다)."],
        // 接上之后自动去问"你这儿有哪些模型"
        ["ModelsPull"] = ["Fetch models", "拉取模型", "拉取模型", "モデルを取得", "모델 가져오기"],
        ["NavExpand"] = ["Show this provider's models", "展开这一家的模型", "展開這一家的模型", "このプロバイダーのモデルを表示", "이 프로바이더의 모델 펼치기"],
        ["NavCollapse"] = ["Hide this provider's models", "折起这一家的模型", "摺起這一家的模型", "このプロバイダーのモデルを隠す", "이 프로바이더의 모델 접기"],
        ["ModelsPullHint"] = ["Asks this endpoint which models it actually serves (/models), then fills in context window and prices from models.dev. Relay prices are left blank — they are not the vendor's.", "先问这个端点它实际供应哪些模型(/models),再从 models.dev 补上下文窗口与单价。中转站的单价留空 —— 那跟原厂不是一回事。", "先問這個端點它實際供應哪些模型(/models),再從 models.dev 補上下文視窗與單價。中轉站的單價留空 —— 那跟原廠不是一回事。", "このエンドポイントに実際に提供しているモデル(/models)を尋ね、コンテキスト長と料金を models.dev から補完します。中継の料金は空のままにします(提供元とは別物のため)。", "이 엔드포인트가 실제로 제공하는 모델(/models)을 물어본 뒤, 컨텍스트 길이와 가격을 models.dev에서 채웁니다. 중계 서비스의 가격은 원 제공사와 다르므로 비워 둡니다."],
        ["ModelsPulling"] = ["Fetching the model list…", "正在拉取模型列表…", "正在拉取模型列表…", "モデル一覧を取得中…", "모델 목록을 가져오는 중…"],
        ["ModelsPulled"] = ["Connected · {0} model(s) ready to pick in the chat panel", "已连接 · {0} 个模型已就位,可在聊天面板的下拉里直接选", "已連接 · {0} 個模型已就位,可在聊天面板的下拉裡直接選", "接続済み · モデル {0} 件を用意しました(チャット画面のドロップダウンから選べます)", "연결됨 · 모델 {0}개 준비 완료(채팅 패널 드롭다운에서 선택)"],
        ["ModelsNone"] = ["Connected. This provider does not publish a model list — the factory example is kept; change it under Advanced.", "已连接。这一家不提供模型列表,先用出厂示例,可在「高级设置」里改。", "已連接。這一家不提供模型列表,先用出廠範例,可在「進階設定」裡改。", "接続しました。このプロバイダーはモデル一覧を提供していないため、出荷時の例のままです(「詳細設定」で変更できます)。", "연결되었습니다. 이 프로바이더는 모델 목록을 제공하지 않아 출고 시 예시를 유지합니다(「고급 설정」에서 변경)."],
        ["ModelSpecFilled"] = ["Context window and prices filled in from models.dev.", "上下文窗口与单价已按 models.dev 填好。", "上下文視窗與單價已按 models.dev 填好。", "コンテキスト長と単価を models.dev から入力しました。", "컨텍스트 창과 단가를 models.dev에서 채웠습니다."],
        ["ModelPick"] = ["Model (pulled from the provider)", "模型(已从供应商拉取)", "模型(已從供應商拉取)", "モデル(プロバイダーから取得)", "모델(프로바이더에서 가져옴)"],
        ["SetupExperimental"] = ["experimental", "实验性", "實驗性", "実験的", "실험적"],
        ["SetupExperimentalHint"] = ["This provider talks to an endpoint its vendor has not published as a stable API, using the OAuth client of that vendor's own CLI. It can stop working without notice, and whether it fits the vendor's terms is yours to confirm.", "这一家走的是厂商未公开承诺稳定的接口,用的是该厂商自家命令行工具的 OAuth 客户端标识。它可能在任何一天失效,是否符合对方条款需要你自行确认。", "這一家走的是廠商未公開承諾穩定的介面,用的是該廠商自家命令列工具的 OAuth 用戶端識別。它可能在任何一天失效,是否符合對方條款需要你自行確認。", "このプロバイダーは、ベンダーが安定版 API として公開していないエンドポイントに、そのベンダー自身の CLI の OAuth クライアントで接続します。予告なく使えなくなる可能性があり、規約への適合はご自身でご確認ください。", "이 프로바이더는 공급사가 안정 API로 공개하지 않은 엔드포인트에, 그 공급사 CLI의 OAuth 클라이언트로 접속합니다. 예고 없이 중단될 수 있으며 약관 적합 여부는 직접 확인해야 합니다."],
        ["SetupAdvanced"] = ["Advanced", "高级设置", "進階設定", "詳細設定", "고급 설정"],
        ["SetupNoKeyNeeded"] = ["Runs locally — no key needed. Just add it.", "本地服务,不需要 Key,点「添加」即可。", "本機服務,不需要 Key,點「新增」即可。", "ローカルサービスのためキーは不要です。「追加」を押すだけです。", "로컬 서비스라 키가 필요 없습니다. 「추가」만 누르세요."],
        ["SetupConnectedHint"] = ["Signed in. Use “Sign in again” to switch accounts, or “Sign out” to drop the stored credential.", "已登录。换账号点「重新登录」,不想留凭据点「退出登录」。", "已登入。換帳號點「重新登入」,不想留憑據點「登出」。", "ログイン済みです。アカウントを変えるには「再ログイン」、資格情報を消すには「ログアウト」。", "로그인됨. 계정을 바꾸려면 「다시 로그인」, 자격 증명을 지우려면 「로그아웃」."],
        ["SetupClientIdPending"] = ["VelaShell has not registered an OAuth app with this provider yet, so one-click sign-in is not live. Register one yourself and paste its client ID here — you only do this once.", "VelaShell 还没在这家注册 OAuth 应用,所以一键登录还没通。你可以自己注册一个,把客户端 id 填在下面 —— 只需要填这一次。", "VelaShell 還沒在這家註冊 OAuth 應用,所以一鍵登入還沒通。你可以自己註冊一個,把用戶端 id 填在下面 —— 只需要填這一次。", "VelaShell はこのプロバイダーに OAuth アプリを登録していないため、ワンクリックログインはまだ使えません。ご自身で登録してクライアント ID を入力してください(一度だけです)。", "VelaShell이 이 프로바이더에 OAuth 앱을 아직 등록하지 않아 원클릭 로그인이 준비되지 않았습니다. 직접 등록해 클라이언트 ID를 입력하세요(한 번만 하면 됩니다)."],
        ["SetupOpenRegistration"] = ["Open the registration page", "打开注册页", "開啟註冊頁", "登録ページを開く", "등록 페이지 열기"],
        ["SetupRemove"] = ["Remove", "移除", "移除", "削除", "제거"],
        ["SetupRemoved"] = ["Removed.", "已移除。", "已移除。", "削除しました。", "제거했습니다."],
        ["SetupAdd"] = ["Add", "添加", "新增", "追加", "추가"],
        ["SetupAdded"] = ["Added.", "已添加。", "已新增。", "追加しました。", "추가했습니다."],
        ["SetupSignIn"] = ["Sign in", "登录", "登入", "ログイン", "로그인"],
        ["SetupReconnect"] = ["Sign in again", "重新登录", "重新登入", "再ログイン", "다시 로그인"],
        ["SetupSignOut"] = ["Sign out", "退出登录", "登出", "ログアウト", "로그아웃"],
        ["SetupNeedsBaseUrl"] = ["Fill in the base URL first.", "先把基地址填上。", "先把基底位址填上。", "先にベース URL を入力してください。", "먼저 베이스 URL을 입력하세요."],
        ["SetupNeedsOAuth"] = ["Fill in the client ID and the endpoints first.", "先把客户端 id 与端点地址填上。", "先把用戶端 id 與端點位址填上。", "先にクライアント ID とエンドポイントを入力してください。", "먼저 클라이언트 ID와 엔드포인트를 입력하세요."],
        ["StatusNotAdded"] = ["Not added", "未添加", "未新增", "未追加", "추가 안 됨"],
        ["StatusNotConnected"] = ["Not connected", "未连接", "未連接", "未接続", "연결 안 됨"],
        ["StatusConnected"] = ["Connected", "已连接", "已連接", "接続済み", "연결됨"],
        ["StatusConnectedAs"] = ["Connected · {0}", "已连接 · {0}", "已連接 · {0}", "接続済み · {0}", "연결됨 · {0}"],
        ["StatusNeedsKey"] = ["No API key yet", "还没填 Key", "還沒填 Key", "API キー未設定", "API 키 없음"],
        ["StatusReady"] = ["Ready", "已就绪", "已就緒", "利用可能", "준비됨"],
        ["Cancel"] = ["Cancel", "取消", "取消", "キャンセル", "취소"],
        ["OAuthFlow"] = ["Sign-in flow", "登录方式", "登入方式", "ログイン方式", "로그인 방식"],
        ["OAuthFlowPkce"] = ["Authorization code + PKCE (browser)", "授权码 + PKCE(浏览器)", "授權碼 + PKCE(瀏覽器)", "認可コード + PKCE(ブラウザー)", "인가 코드 + PKCE(브라우저)"],
        ["OAuthFlowDevice"] = ["Device code (type a code in the browser)", "设备码(在浏览器里输一段码)", "裝置碼(在瀏覽器裡輸一段碼)", "デバイスコード(ブラウザーでコードを入力)", "디바이스 코드(브라우저에 코드 입력)"],
        ["OAuthAuthorizeUrl"] = ["Authorization endpoint", "授权地址", "授權位址", "認可エンドポイント", "인가 엔드포인트"],
        ["OAuthDeviceUrl"] = ["Device code endpoint", "设备码地址", "裝置碼位址", "デバイスコードエンドポイント", "디바이스 코드 엔드포인트"],
        ["OAuthTokenUrl"] = ["Token endpoint", "令牌地址", "權杖位址", "トークンエンドポイント", "토큰 엔드포인트"],
        ["OAuthClientId"] = ["Client ID", "客户端 id", "用戶端 id", "クライアント ID", "클라이언트 ID"],
        ["OAuthClientSecret"] = ["Client secret", "客户端密钥", "用戶端密鑰", "クライアントシークレット", "클라이언트 시크릿"],
        ["OAuthClientSecretHint"] = ["A desktop app is a public client, so this is normally empty — PKCE is what protects the exchange. Fill it in only if your own service demands one.", "桌面程序是公共客户端,通常留空 —— 保护换取过程的是 PKCE 而不是它。只有自建服务硬性要求时才填。", "桌面程式是公用用戶端,通常留空 —— 保護換取過程的是 PKCE 而不是它。只有自建服務硬性要求時才填。", "デスクトップアプリはパブリッククライアントなので通常は空です(交換を守るのは PKCE)。自前のサービスが必須とする場合のみ入力します。", "데스크톱 앱은 퍼블릭 클라이언트라 보통 비워 둡니다(교환을 지키는 것은 PKCE). 자체 서비스가 요구할 때만 입력하세요."],
        ["OAuthScopes"] = ["Scopes (space separated)", "权限范围 scope(空格分隔)", "權限範圍 scope(空格分隔)", "スコープ(スペース区切り)", "스코프(공백 구분)"],
        ["LoginStarting"] = ["Starting sign-in…", "正在发起登录…", "正在發起登入…", "ログインを開始しています…", "로그인을 시작하는 중…"],
        ["LoginWaiting"] = ["Browser opened — finish the sign-in there.", "已打开浏览器,请在那边完成授权。", "已開啟瀏覽器,請在那邊完成授權。", "ブラウザーを開きました。そちらで認証を完了してください。", "브라우저를 열었습니다. 거기서 인증을 마치세요."],
        ["LoginExchanging"] = ["Exchanging credentials…", "正在换取凭据…", "正在換取憑據…", "資格情報を取得しています…", "자격 증명을 교환하는 중…"],
        ["LoginUserCode"] = ["Enter this code in the browser: {0}", "在浏览器里输入这段码:{0}", "在瀏覽器裡輸入這段碼:{0}", "ブラウザーでこのコードを入力してください: {0}", "브라우저에 이 코드를 입력하세요: {0}"],
        ["LoginDone"] = ["Signed in.", "登录成功。", "登入成功。", "ログインしました。", "로그인했습니다."],
        ["LoginFailed"] = ["Sign-in failed: {0}", "登录失败:{0}", "登入失敗:{0}", "ログインに失敗しました: {0}", "로그인 실패: {0}"],
        ["LoginCancelled"] = ["Sign-in cancelled.", "已取消登录。", "已取消登入。", "ログインをキャンセルしました。", "로그인을 취소했습니다."],
        ["LoginSignedOut"] = ["Signed out.", "已退出登录。", "已登出。", "ログアウトしました。", "로그아웃했습니다."],
        // 浏览器授权完成后打回本机、由环回端口回给浏览器的那一页
        ["LoginPageTitle"] = ["Signed in to VelaShell", "已登录 VelaShell", "已登入 VelaShell", "VelaShell にログインしました", "VelaShell에 로그인했습니다"],
        ["LoginPageBody"] = ["You can close this tab and go back to VelaShell.", "可以关掉这个标签页,回到 VelaShell 了。", "可以關掉這個分頁,回到 VelaShell 了。", "このタブを閉じて VelaShell に戻ってください。", "이 탭을 닫고 VelaShell로 돌아가세요."],
        // 设置页里那家供应商的登录状态(编辑表单上半部分)
        ["SubscriptionAuth"] = ["Subscription sign-in", "订阅登录", "訂閱登入", "サブスクリプションログイン", "구독 로그인"],
        ["SubscriptionSignedIn"] = ["Signed in on {0}", "已于 {0} 登录", "已於 {0} 登入", "{0} にログイン済み", "{0}에 로그인함"],
        ["SubscriptionNotSignedIn"] = ["Not signed in yet", "还没有登录", "還沒有登入", "まだログインしていません", "아직 로그인하지 않았습니다"],
        ["SubscriptionHint"] = ["This provider authenticates with your account instead of an API key. Sign-in is managed on the “Connect a provider” page.", "这家用账号登录鉴权,不需要 API Key。登录与退出在「连接供应商」那一页管理。", "這家用帳號登入鑑權,不需要 API Key。登入與登出在「連接供應商」那一頁管理。", "このプロバイダーは API キーではなくアカウントで認証します。ログインの管理は「プロバイダーを接続」ページで行います。", "이 프로바이더는 API 키 대신 계정으로 인증합니다. 로그인 관리는 「프로바이더 연결」 페이지에서 합니다."],
        ["ManageSignIn"] = ["Manage sign-in", "管理登录", "管理登入", "ログインを管理", "로그인 관리"],
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
        ["MeterTip"] = ["Share of the context window taken by the last turn.", "上一轮占掉了多大比例的上下文窗口。", "上一輪佔掉了多大比例的上下文視窗。", "直近のターンがコンテキスト長のどれだけを占めたか。", "직전 턴이 컨텍스트 창을 얼마나 차지했는지."],

        // ── 协作接入设置页 ──
        ["CmdCollaboration"] = ["AI: Collaboration (chat bridge & MCP server)", "AI: 协作接入(IM 桥接与 MCP 服务端)", "AI: 協作接入(IM 橋接與 MCP 伺服器)", "AI: 連携設定(チャット連携と MCP サーバー)", "AI: 협업 연동(채팅 브리지 및 MCP 서버)"],
        ["Collaboration"] = ["Collaboration", "协作接入", "協作接入", "連携", "협업 연동"],
        ["SecBridge"] = ["Chat bridge", "IM 桥接", "IM 橋接", "チャット連携", "채팅 브리지"],
        // 这一条是**总开关**,不是"授权别人用"。原文案写的是"允许团队从 IM 里指使这个助手",
        // 于是一个人自用时看起来像是多余的一步(用户反馈):明明只是想自己跟机器人私聊,
        // 却被要求先勾一个听上去在给团队开权限的框。实际语义是"要不要接 IM" —— 不勾就一条连接都不建。
        ["BridgeEnabled"] = [
            "Enable the chat bridge (talk to this assistant from Feishu / DingTalk / Telegram)",
            "启用 IM 接入(在飞书 / 钉钉 / Telegram 里跟这个助手对话)",
            "啟用 IM 接入(在飛書 / 釘釘 / Telegram 裡跟這個助手對話)",
            "チャット連携を有効にする(Feishu / DingTalk / Telegram からこのアシスタントと話す)",
            "채팅 연동 활성화(Feishu / DingTalk / Telegram에서 이 어시스턴트와 대화)"
        ],
        ["BridgeEnabledHint"] = [
            "The master switch: unticked, no connection is made at all. Online only while VelaShell is running (minimise to tray to stay up); tools act on the SSH sessions you already have open. Every chat — including your own direct message to the bot — still has to be authorised below, because anyone in the same tenant can message the bot and it cannot tell which one is you.",
            "总开关:不勾就一条连接都不建。只在 VelaShell 开着时在线(可最小化到托盘),工具作用于你已经连上的那些 SSH 会话。每个聊天(包括你自己跟机器人的单聊)仍要在下面单独授权 —— 同一个租户里任何人都能私聊这个机器人,它分不出哪个是你。",
            "總開關:不勾就一條連線都不建。只在 VelaShell 開著時在線(可最小化到系統匣),工具作用於你已經連上的那些 SSH 工作階段。每個聊天(包括你自己跟機器人的單聊)仍要在下面單獨授權 —— 同一個租戶裡任何人都能私訊這個機器人,它分不出哪個是你。",
            "マスタースイッチです。オフの間は接続を一切張りません。VelaShell の起動中のみオンライン(トレイに最小化で常駐)、ツールは既に接続済みの SSH セッションに作用します。どのチャットも(自分とボットの個別チャットを含め)下で個別に許可が必要です —— 同じテナントの誰でもボットに話しかけられ、ボットにはどれがあなたか分からないからです。",
            "마스터 스위치입니다. 꺼져 있으면 연결을 전혀 만들지 않습니다. VelaShell이 실행 중일 때만 온라인이며(트레이로 최소화하면 계속 유지), 도구는 이미 연결된 SSH 세션에 작용합니다. 모든 대화는(본인과 봇의 1:1 대화 포함) 아래에서 개별적으로 허용해야 합니다 — 같은 테넌트의 누구나 봇에게 말을 걸 수 있고, 봇은 그중 누가 본인인지 구분하지 못하기 때문입니다."
        ],
        ["BridgeMode"] = ["Mode", "挡位", "擋位", "モード", "모드"],
        ["BridgeApproval"] = ["Approval", "审批方式", "審批方式", "承認方式", "승인 방식"],
        ["BridgeModeHint"] = [
            "Plan is read-only and is the safe default for a chat room. Approvals are asked in the chat: reply y or n.",
            "计划档只读,是聊天场景下的安全默认。审批就问在聊天里 —— 回 y 或 n。",
            "計劃檔唯讀,是聊天場景下的安全預設。審批就問在聊天裡 —— 回 y 或 n。",
            "計画モードは読み取り専用で、チャット用の安全な既定値です。承認はチャット内で尋ねます(y / n で返答)。",
            "계획 모드는 읽기 전용이며 채팅에 안전한 기본값입니다. 승인은 대화에서 y 또는 n으로 답하면 됩니다."
        ],
        ["BridgeEscalation"] = ["Allow /mode to raise the mode from chat", "允许在聊天里用 /mode 提高挡位", "允許在聊天裡用 /mode 提高擋位", "チャットの /mode でモードを引き上げることを許可", "대화의 /mode로 모드를 올리는 것을 허용"],
        ["BridgeEscalationHint"] = [
            "Off by default: anyone allowed to talk could otherwise turn the read-only bridge into one that runs commands.",
            "默认关:否则任何能说话的人都能把只读的桥接变成能敲命令的。",
            "預設關:否則任何能說話的人都能把唯讀的橋接變成能敲命令的。",
            "既定はオフ:許可された誰もが読み取り専用の連携をコマンド実行可能にできてしまうためです。",
            "기본값은 꺼짐: 그렇지 않으면 대화 허용된 누구나 읽기 전용 브리지를 명령 실행형으로 바꿀 수 있습니다."
        ],
        ["BridgeTurnTimeout"] = ["Turn timeout (s)", "单轮超时(秒)", "單輪逾時(秒)", "1 ターンのタイムアウト(秒)", "턴 제한 시간(초)"],
        ["BridgeApprovalTimeout"] = ["Approval timeout (s)", "审批超时(秒)", "審批逾時(秒)", "承認のタイムアウト(秒)", "승인 제한 시간(초)"],
        ["BridgeConcurrency"] = ["Concurrent turns", "并发轮次", "並發輪次", "同時実行ターン数", "동시 실행 턴 수"],
        ["BridgeModel"] = ["Model", "模型", "模型", "モデル", "모델"],
        ["BridgeModelFollow"] = ["Follow the chat panel", "跟随聊天面板", "跟隨聊天面板", "チャットパネルに従う", "채팅 패널을 따름"],
        ["BridgeModelHint"] = [
            "Pin one so a failure names the right provider — an \"unauthorised\" from the model looks nothing like one from the chat platform, but both land in the same chat message.",
            "指定一个,出错时才知道该查哪家的密钥 —— 模型返回的「未授权」和 IM 平台返回的「未授权」是两回事,但在群里长得一模一样。",
            "指定一個,出錯時才知道該查哪家的密鑰 —— 模型回傳的「未授權」和 IM 平台回傳的「未授權」是兩回事,但在群裡長得一模一樣。",
            "モデルを固定しておくと、失敗時にどの接続を確認すべきか分かります。モデル側の「未認証」とチャット基盤側の「未認証」は別物ですが、チャットには同じように出ます。",
            "모델을 고정해 두면 실패했을 때 어느 연결을 확인해야 할지 알 수 있습니다. 모델의 \"인증 실패\"와 채팅 플랫폼의 \"인증 실패\"는 다르지만 대화에는 똑같이 표시됩니다."
        ],
        ["ChannelAddBotFeishu"] = [
            "Feishu has no link for this: in the Feishu client open the group → Settings → Group bots → Add bot, and search for your app name.",
            "飞书没有对应的链接:在飞书客户端里打开群 → 设置 → 群机器人 → 添加机器人,搜你的应用名。",
            "飛書沒有對應的連結:在飛書用戶端裡打開群 → 設定 → 群機器人 → 新增機器人,搜你的應用名。",
            "Feishu には該当するリンクがありません。Feishu クライアントでグループを開き、設定 → グループボット → ボットを追加 から、アプリ名で検索してください。",
            "Feishu에는 해당 링크가 없습니다. Feishu 클라이언트에서 그룹 → 설정 → 그룹 봇 → 봇 추가로 이동해 앱 이름으로 검색하세요."
        ],
        ["SecChannels"] = ["Channels", "渠道", "渠道", "チャンネル", "채널"],
        ["ChannelAdd"] = ["Add", "添加", "新增", "追加", "추가"],
        ["ChannelRemove"] = ["Remove", "移除", "移除", "削除", "제거"],
        ["ChannelNone"] = ["No channels yet — pick a platform above and add one.", "还没有渠道 —— 在上面选一个平台添加。", "還沒有渠道 —— 在上面選一個平台新增。", "チャンネルがありません。上でプラットフォームを選んで追加してください。", "채널이 없습니다. 위에서 플랫폼을 선택해 추가하세요."],
        ["ChannelEnabled"] = ["Enabled", "启用", "啟用", "有効", "사용"],
        ["ChannelName"] = ["Display name", "显示名", "顯示名", "表示名", "표시 이름"],
        ["ChannelInternational"] = ["International edition (larksuite.com)", "国际版(larksuite.com)", "國際版(larksuite.com)", "国際版(larksuite.com)", "국제판(larksuite.com)"],
        ["ChannelUsers"] = ["Allowed users (blank = anyone in those chats)", "允许的用户(留空 = 白名单聊天里的任何人)", "允許的使用者(留空 = 白名單聊天裡的任何人)", "許可するユーザー(空欄 = 上記チャットの全員)", "허용할 사용자(비우면 해당 대화의 모든 사람)"],
        ["ChannelApprovers"] = ["Approvers (blank = same as allowed users)", "审批人(留空 = 与允许的用户相同)", "審批人(留空 = 與允許的使用者相同)", "承認者(空欄 = 許可ユーザーと同じ)", "승인자(비우면 허용 사용자와 동일)"],
        ["ChannelTarget"] = ["Default server (user@host:port)", "默认服务器(user@host:port)", "預設伺服器(user@host:port)", "既定のサーバー(user@host:port)", "기본 서버(user@host:port)"],
        ["ChannelTest"] = ["Test", "测试", "測試", "テスト", "테스트"],
        ["ChannelTesting"] = ["Testing…", "正在测试…", "正在測試…", "テスト中…", "테스트 중…"],
        ["ChannelInviteHint"] = [
            "Scan with your phone to add the bot to a group — no need to search for it by name.",
            "手机扫这个码直接把机器人加进群 —— 不用在手机上按名字搜。",
            "手機掃這個碼直接把機器人加進群 —— 不用在手機上按名字搜。",
            "スマホでこのコードを読み取ると、ボットをそのままグループに追加できます(名前で検索する必要はありません)。",
            "휴대폰으로 이 코드를 스캔하면 봇을 바로 그룹에 추가할 수 있습니다(이름으로 검색할 필요 없음)."
        ],
        // ── 授权聊天(配对码 / 一键放行)──
        ["SecPairing"] = ["Authorising chats", "授权聊天", "授權聊天", "チャットの許可", "대화 허용"],
        ["PairCode"] = ["Pairing code", "配对码", "配對碼", "ペアリングコード", "페어링 코드"],
        ["PairIssue"] = ["Generate", "生成", "產生", "生成", "생성"],
        ["PairExpiresIn"] = ["(expires in {0}m {1}s)", "({0} 分 {1} 秒后过期)", "({0} 分 {1} 秒後過期)", "(あと {0} 分 {1} 秒で失効)", "({0}분 {1}초 후 만료)"],
        ["PairHint"] = [
            "Generate a code, then send \"/pair <code>\" in the chat you want to authorise — no need to copy chat ids around. One use, ten minutes, five wrong tries and it dies. This one is for your own direct chat with the bot, so it carries no limit; a group gets its scope chosen below, before the code is issued.",
            "点生成拿一个码,然后在想授权的那个聊天里发「/pair 码」—— 不用再来回抄群 id。一次性、十分钟过期、猜错五次作废。这个码是给「你自己跟机器人的单聊」用的,所以不带范围;给群的码在下面先选好范围再发。",
            "點產生拿一個碼,然後在想授權的那個聊天裡發「/pair 碼」—— 不用再來回抄群 id。一次性、十分鐘過期、猜錯五次作廢。這個碼是給「你自己跟機器人的單聊」用的,所以不帶範圍;給群的碼在下面先選好範圍再發。",
            "コードを生成し、許可したいチャットで「/pair コード」と送るだけです(チャット ID を書き写す必要はありません)。1 回限り・10 分で失効・5 回間違えると無効。これは自分とボットの個別チャット用なので範囲の制限は付きません。グループ用のコードは下で範囲を選んでから発行します。",
            "코드를 생성한 뒤 허용할 대화에서 \"/pair 코드\"를 보내세요(대화 ID를 옮겨 적을 필요 없음). 일회용, 10분 만료, 5회 틀리면 폐기됩니다. 이 코드는 자신과 봇의 개인 대화용이라 범위 제한이 없으며, 그룹용 코드는 아래에서 범위를 고른 뒤 발급합니다."
        ],
        ["PairNeedsBridge"] = [
            "Turn the chat bridge on and save first — a pairing code is only useful while the bot is online.",
            "先把 IM 桥接打开并保存 —— 机器人不在线的时候,配对码没有意义。",
            "先把 IM 橋接打開並儲存 —— 機器人不在線的時候,配對碼沒有意義。",
            "先にチャット連携を有効にして保存してください。ボットがオンラインでなければペアリングコードは意味がありません。",
            "먼저 채팅 브리지를 켜고 저장하세요. 봇이 온라인이 아니면 페어링 코드는 의미가 없습니다."
        ],
        ["PairPending"] = ["Chats that tried to talk to the bot", "敲过门的聊天", "敲過門的聊天", "ボットに話しかけてきたチャット", "봇에게 말을 건 대화"],
        ["PairNoPending"] = [
            "Nothing yet. Add the bot to a group and say something — it will show up here.",
            "还没有。把机器人加进群里说句话,它就会出现在这。",
            "還沒有。把機器人加進群裡說句話,它就會出現在這。",
            "まだありません。ボットをグループに追加して何か話しかけると、ここに出てきます。",
            "아직 없습니다. 봇을 그룹에 추가하고 말을 걸면 여기에 나타납니다."
        ],
        ["PairAllow"] = ["Allow", "允许", "允許", "許可", "허용"],
        ["PairIgnore"] = ["Ignore", "忽略", "忽略", "無視", "무시"],
        ["PairGroup"] = ["group", "群聊", "群聊", "グループ", "그룹"],
        ["PairDirect"] = ["direct", "单聊", "單聊", "個別", "개인"],
        ["PairAllowed"] = ["{0} is now allowed.", "已放行 {0}。", "已放行 {0}。", "{0} を許可しました。", "{0}을(를) 허용했습니다."],
        ["ChannelWeComHint"] = [
            "WeCom is the odd one out: it can only push to a public callback URL, so this listener needs a tunnel or reverse proxy in front of it.",
            "企业微信和另外三家不一样:它只能往一个公网回调地址推消息,所以这个监听口前面得有一条隧道或反向代理。",
            "企業微信和另外三家不一樣:它只能往一個公網回呼位址推訊息,所以這個監聽埠前面得有一條隧道或反向代理。",
            "WeCom だけは公開コールバック URL にしか送れないため、この待ち受けの前にトンネルかリバースプロキシが必要です。",
            "WeCom만 예외입니다. 공개 콜백 URL로만 전송하므로 이 수신 포트 앞에 터널이나 리버스 프록시가 필요합니다."
        ],
        ["ChannelCallbackToken"] = ["Callback Token", "回调 Token", "回呼 Token", "コールバック Token", "콜백 Token"],
        ["ChannelWebhookPort"] = ["Callback port (127.0.0.1)", "回调端口(127.0.0.1)", "回呼連接埠(127.0.0.1)", "コールバックポート(127.0.0.1)", "콜백 포트(127.0.0.1)"],
        ["ChannelWebhookPath"] = ["Callback path", "回调路径", "回呼路徑", "コールバックパス", "콜백 경로"],
        ["ChannelWeComCallbackHint"] = [
            "Only 127.0.0.1 is bound. Point WeCom at a public HTTPS address and forward it here — VelaShell's own remote port forwarding (Session → Tunnels) can do that leg.",
            "只绑 127.0.0.1。把企业微信的回调地址指向一个公网 HTTPS 入口,再转发到这里 —— 这一段可以直接用 VelaShell 自己的远程端口转发(会话 → 隧道)。",
            "只綁 127.0.0.1。把企業微信的回呼位址指向一個公網 HTTPS 入口,再轉發到這裡 —— 這一段可以直接用 VelaShell 自己的遠端連接埠轉發(工作階段 → 隧道)。",
            "127.0.0.1 のみにバインドします。WeCom には公開 HTTPS のアドレスを設定し、そこからここへ転送してください。この区間は VelaShell のリモートポート転送(セッション → トンネル)で賄えます。",
            "127.0.0.1에만 바인딩합니다. WeCom에는 공개 HTTPS 주소를 설정하고 이곳으로 전달하세요. 이 구간은 VelaShell의 원격 포트 포워딩(세션 → 터널)으로 해결할 수 있습니다."
        ],
        // ── 对外 MCP 服务端 ──
        ["SecMcpServer"] = ["MCP server (for other agents)", "对外 MCP 服务端(给别的 agent 用)", "對外 MCP 伺服器(給別的 agent 用)", "MCP サーバー(他のエージェント向け)", "MCP 서버(다른 에이전트용)"],
        ["McpServerEnabled"] = ["Let Claude Code / Codex call VelaShell's tools", "让 Claude Code / Codex 调用 VelaShell 的工具", "讓 Claude Code / Codex 呼叫 VelaShell 的工具", "Claude Code / Codex から VelaShell のツールを呼べるようにする", "Claude Code / Codex가 VelaShell 도구를 호출하도록 허용"],
        ["McpServerEnabledHint"] = [
            "Listens on 127.0.0.1 only, and every request must carry the token below.",
            "只监听 127.0.0.1,而且每个请求都必须带下面那个令牌。",
            "只監聽 127.0.0.1,而且每個請求都必須帶下面那個權杖。",
            "127.0.0.1 のみで待ち受け、すべてのリクエストに下のトークンが必要です。",
            "127.0.0.1에서만 수신하며 모든 요청에 아래 토큰이 필요합니다."
        ],
        ["McpServerPort"] = ["Port", "端口", "連接埠", "ポート", "포트"],
        ["McpServerApprovalHint"] = [
            "An external agent has no window to show an approval card in, so \"ask every time\" refuses writes outright. Pick read-only auto or bypass to let it change things.",
            "外部 agent 那边没有能弹审批卡的界面,所以「每次询问」在这条路上等于直接拒绝写操作。要让它能改东西,得选只读放行或绕过审批。",
            "外部 agent 那邊沒有能彈審批卡的介面,所以「每次詢問」在這條路上等於直接拒絕寫操作。要讓它能改東西,得選唯讀放行或繞過審批。",
            "外部エージェント側に承認カードを出す画面がないため、「毎回確認」は書き込みを即拒否します。変更を許すには読み取り自動許可かバイパスを選んでください。",
            "외부 에이전트에는 승인 카드를 띄울 화면이 없어 \"매번 확인\"은 쓰기를 즉시 거부합니다. 변경을 허용하려면 읽기 전용 자동 또는 우회를 선택하세요."
        ],
        ["McpServerTargets"] = ["Allowed servers (one user@host:port per line; blank = all connected)", "允许操作的服务器(每行一个 user@host:port;留空 = 全部已连会话)", "允許操作的伺服器(每行一個 user@host:port;留空 = 全部已連工作階段)", "許可するサーバー(1 行に 1 つ user@host:port、空欄 = 接続中すべて)", "허용할 서버(한 줄에 user@host:port 하나, 비우면 연결된 전체)"],
        ["McpServerTargetsHint"] = ["The external agent can only pick from this list.", "外部 agent 只能在这个名单里挑。", "外部 agent 只能在這個名單裡挑。", "外部エージェントはこの一覧からのみ選べます。", "외부 에이전트는 이 목록에서만 고를 수 있습니다."],
        ["McpServerToken"] = ["Access token", "访问令牌", "存取權杖", "アクセストークン", "액세스 토큰"],
        ["McpServerTokenHint"] = [
            "Anything running on this machine can reach a local port — this token is the only thing that stops it.",
            "本机上任何程序都能敲本地端口,拦住它们的只有这个令牌。",
            "本機上任何程式都能敲本地連接埠,攔住它們的只有這個權杖。",
            "ローカルポートはこの端末上のどのプログラムからも叩けます。それを止めるのはこのトークンだけです。",
            "이 컴퓨터의 어떤 프로그램도 로컬 포트에 접근할 수 있습니다. 이를 막는 것은 이 토큰뿐입니다."
        ],
        ["McpServerCommand"] = ["How to connect", "接入方式", "接入方式", "接続方法", "연결 방법"],
        ["McpServerRotate"] = ["Regenerate", "重新生成", "重新產生", "再生成", "재생성"],
        ["McpServerRotated"] = ["New token generated — update your agent's config.", "已生成新令牌 —— 记得同步改你 agent 那边的配置。", "已產生新權杖 —— 記得同步改你 agent 那邊的設定。", "新しいトークンを生成しました。エージェント側の設定も更新してください。", "새 토큰을 생성했습니다. 에이전트 설정도 업데이트하세요."],

        // ── IM 桥接:发进聊天里的那些话。跟随宿主语言,不跟随发消息的人 ──
        ["BridgeThinking"] = ["Working on it…", "正在处理…", "正在處理…", "処理中…", "처리 중…"],
        ["BridgeBusy"] = ["Still on the previous request — queued.", "上一条还没跑完,已排队。", "上一條還沒跑完,已排隊。", "前の依頼を処理中です。順番待ちに入れました。", "이전 요청을 처리 중입니다. 대기열에 넣었습니다."],
        ["BridgeRunningTool"] = ["running {0}…", "正在调用 {0}…", "正在呼叫 {0}…", "{0} を実行中…", "{0} 실행 중…"],
        ["BridgeFooter"] = ["— {0} · {1}s · {2} tool call(s)", "— {0} · {1}s · {2} 次工具调用", "— {0} · {1}s · {2} 次工具呼叫", "— {0} · {1}s · ツール {2} 回", "— {0} · {1}s · 도구 {2}회"],
        ["BridgeNoModel"] = ["No AI model is configured in VelaShell yet.", "VelaShell 里还没配置模型。", "VelaShell 裡還沒設定模型。", "VelaShell にモデルが設定されていません。", "VelaShell에 모델이 설정되지 않았습니다."],
        ["BridgeEmptyReply"] = ["(the model returned nothing)", "(模型没有返回内容)", "(模型沒有回傳內容)", "(モデルの応答が空でした)", "(모델이 아무것도 반환하지 않았습니다)"],
        ["BridgeTurnFailed"] = ["Failed: {0}", "出错了:{0}", "出錯了:{0}", "失敗しました:{0}", "실패했습니다: {0}"],
        ["BridgeTimeout"] = ["Timed out — stopped this turn.", "超时,已中止这一轮。", "逾時,已中止這一輪。", "タイムアウトしたため中断しました。", "시간이 초과되어 이번 턴을 중단했습니다."],
        ["BridgeUnauthorized"] = [
            "This chat is not authorised yet. Generate a pairing code in VelaShell (Collaboration) and send \"/pair <code>\" here — or allow it from that page. Chat id: {0}",
            "这个聊天还没被授权。到 VelaShell 的「协作接入」生成配对码,在这里发「/pair 码」即可 —— 或者直接在那一页点允许。聊天 id:{0}",
            "這個聊天還沒被授權。到 VelaShell 的「協作接入」產生配對碼,在這裡發「/pair 碼」即可 —— 或者直接在那一頁點允許。聊天 id:{0}",
            "このチャットは未許可です。VelaShell の「連携」でペアリングコードを生成し、ここで「/pair コード」と送ってください(その画面から許可することもできます)。チャット ID:{0}",
            "이 대화는 아직 허용되지 않았습니다. VelaShell의 「협업 연동」에서 페어링 코드를 생성한 뒤 여기서 \"/pair 코드\"를 보내세요(해당 화면에서 바로 허용할 수도 있습니다). 대화 ID: {0}"
        ],
        ["BridgePairUsage"] = ["Usage: /pair <code> — the code is in VelaShell under Collaboration.", "用法:/pair 码 —— 码在 VelaShell 的「协作接入」页里。", "用法:/pair 碼 —— 碼在 VelaShell 的「協作接入」頁裡。", "使い方:/pair コード —— コードは VelaShell の「連携」画面にあります。", "사용법: /pair 코드 — 코드는 VelaShell의 「협업 연동」 화면에 있습니다."],
        ["BridgePairRejected"] = ["That code is not valid. Generate a fresh one in VelaShell.", "这个码不对。到 VelaShell 里重新生成一个。", "這個碼不對。到 VelaShell 裡重新產生一個。", "そのコードは使えません。VelaShell で新しく生成してください。", "이 코드는 사용할 수 없습니다. VelaShell에서 새로 생성하세요."],
        // ---- 会话范围授权 ----
        ["BridgePairedScoped"] = ["Paired — this chat can talk to me now, limited to: {0}. Try /help.", "配对成功,这个聊天现在可以跟我说话了,范围:{0}。试试 /help。", "配對成功,這個聊天現在可以跟我說話了,範圍:{0}。試試 /help。", "ペアリングできました。このチャットから話しかけられます(範囲:{0})。/help を試してください。", "페어링에 성공했습니다. 이제 이 대화에서 말을 걸 수 있습니다(범위: {0}). /help를 사용해 보세요."],
        ["BridgeStatusScope"] = ["Scope: {0}", "范围:{0}", "範圍:{0}", "範囲:{0}", "범위: {0}"],
        ["BridgeScopeAll"] = ["all machines", "全部机器", "全部機器", "すべてのマシン", "모든 머신"],
        ["ScopeAll"] = ["No limit — every machine", "不限范围(全部机器)", "不限範圍(全部機器)", "制限なし(すべてのマシン)", "제한 없음(모든 머신)"],
        ["ScopeLimited"] = ["Only what I tick below", "只有下面勾选的", "只有下面勾選的", "下でチェックしたものだけ", "아래에서 선택한 것만"],
        ["ScopeLabel"] = ["Can operate", "能操作", "能操作", "操作できる範囲", "조작 범위"],
        ["ScopeGroups"] = ["Groups", "分组", "分組", "グループ", "그룹"],
        ["ScopeMachines"] = ["Individual machines", "单台机器", "單台機器", "個別のマシン", "개별 머신"],
        ["ScopeEmptyWarning"] = ["Nothing ticked — this chat can reach no machine at all.", "一个都没勾:这个聊天碰不到任何机器。", "一個都沒勾:這個聊天碰不到任何機器。", "何もチェックされていません。このチャットはどのマシンにも到達できません。", "아무것도 선택되지 않았습니다. 이 대화는 어떤 머신에도 접근할 수 없습니다."],
        ["ScopeNoSaved"] = ["No saved connections yet — save some in the session tree first.", "还没有已保存的连接,先去会话树里存几台。", "還沒有已保存的連線,先去工作階段樹裡存幾台。", "保存済みの接続がありません。先にセッションツリーに登録してください。", "저장된 연결이 없습니다. 먼저 세션 트리에 등록하세요."],
        ["GrantsLabel"] = ["Authorized chats", "已授权的聊天", "已授權的聊天", "許可済みのチャット", "허가된 대화"],
        ["GrantsHint"] = ["A group's scope is a property of the room, not of you — your own DM and the AI panel are never limited.", "群的范围是这个房间的属性,不是你的:你自己的单聊与 AI 面板永远不受限。", "群的範圍是這個房間的屬性,不是你的:你自己的單聊與 AI 面板永遠不受限。", "グループの範囲は部屋の属性であって、あなたの属性ではありません。自分との個別チャットと AI パネルは制限されません。", "그룹의 범위는 방의 속성이지 사용자의 속성이 아닙니다. 자신과의 개인 대화와 AI 패널은 제한되지 않습니다."],
        ["GrantAdd"] = ["Add a chat", "添加聊天", "新增聊天", "チャットを追加", "대화 추가"],
        ["GrantChatId"] = ["Chat id", "聊天 id", "聊天 id", "チャット ID", "대화 ID"],
        ["GrantMode"] = ["Mode", "挡位", "擋位", "モード", "모드"],
        ["GrantApproval"] = ["Approval", "审批", "審批", "承認", "승인"],
        ["GrantFollowGlobal"] = ["Follow the global setting", "跟随全局设置", "跟隨全域設定", "全体設定に従う", "전역 설정 따르기"],
        ["GrantIsGroup"] = ["group", "群", "群", "グループ", "그룹"],
        ["GrantIsDirect"] = ["direct chat", "单聊", "單聊", "個別チャット", "개인 대화"],
        ["PairForGroup"] = ["Pairing code for a group", "生成配对码(群)", "產生配對碼(群)", "グループ用のペアリングコード", "그룹용 페어링 코드"],
        ["PairForSelf"] = ["Pairing code for myself (no limit)", "生成配对码(我自己 · 不限范围)", "產生配對碼(我自己 · 不限範圍)", "自分用のペアリングコード(制限なし)", "내 계정용 페어링 코드(제한 없음)"],
        ["PairScopeHint"] = ["The code carries this scope — the chat is limited from its first second, with no window where it holds everything.", "配对码携带这份范围:群从第一秒起就受限,不存在\"先全开再收紧\"的窗口。", "配對碼攜帶這份範圍:群從第一秒起就受限,不存在\"先全開再收緊\"的視窗。", "コードはこの範囲を伴います。チャットは最初から制限され、「まず全開放してから絞る」窓は存在しません。", "코드가 이 범위를 함께 전달합니다. 대화는 처음부터 제한되며 \"먼저 전부 열고 나중에 좁히는\" 구간이 없습니다."],
        ["BridgePaired"] = ["Paired — this chat can talk to me now. Try /help.", "配对成功,这个聊天现在可以跟我说话了。试试 /help。", "配對成功,這個聊天現在可以跟我說話了。試試 /help。", "ペアリングできました。このチャットから話しかけられます。/help を試してください。", "페어링에 성공했습니다. 이제 이 대화에서 말을 걸 수 있습니다. /help를 사용해 보세요."],
        // ── 斜杠命令 ──
        // 欢迎语 = 自我介绍 + **此刻真实的**设定 + 命令表({4} 塞的就是下面那份)。
        // 设定不写死成静态文案:一个只读、只授权了某个分组的群里,印着"你可以让我重启服务"
        // 比不印更糟 —— 人会照着去下命令,然后撞上一句(按设计)不解释范围的拒绝。
        ["BridgeWelcome"] = [
            "**VelaShell assistant** is connected to this chat. I can reach the servers you saved in VelaShell — read logs, check services, run commands.\n\n**Right now**\n- Mode: {0}\n- Approval: {1}\n- Can operate: {2}\n- Bound to: {3}\n\n**Commands**\n{4}\n\nOr just ask, e.g. \"any errors in the 32601 service today?\". In a group, @ me first.",
            "**VelaShell 助手**已接入这个聊天。我能连到你在 VelaShell 里保存的服务器,读日志、看服务、跑命令。\n\n**当前设定**\n- 挡位:{0}\n- 审批:{1}\n- 能操作:{2}\n- 绑定:{3}\n\n**命令**\n{4}\n\n也可以直接问,比如「32601 那个服务今天有报错吗」。群里记得先 @ 我。",
            "**VelaShell 助手**已接入這個聊天。我能連到你在 VelaShell 裡儲存的伺服器,讀日誌、看服務、跑命令。\n\n**目前設定**\n- 擋位:{0}\n- 審批:{1}\n- 能操作:{2}\n- 綁定:{3}\n\n**命令**\n{4}\n\n也可以直接問,比如「32601 那個服務今天有報錯嗎」。群裡記得先 @ 我。",
            "**VelaShell アシスタント**がこのチャットに接続しました。VelaShell に保存したサーバーに接続して、ログの確認・サービスの状態確認・コマンド実行ができます。\n\n**現在の設定**\n- モード:{0}\n- 承認:{1}\n- 操作できる範囲:{2}\n- 接続先:{3}\n\n**コマンド**\n{4}\n\nそのまま質問しても構いません(例:「32601 のサービスに今日エラーは出ていますか」)。グループでは先に @ してください。",
            "**VelaShell 어시스턴트**가 이 대화에 연결되었습니다. VelaShell에 저장한 서버에 접속해 로그 확인, 서비스 상태 확인, 명령 실행을 할 수 있습니다.\n\n**현재 설정**\n- 모드: {0}\n- 승인: {1}\n- 조작 범위: {2}\n- 대상: {3}\n\n**명령**\n{4}\n\n그냥 물어봐도 됩니다(예: \"32601 서비스에 오늘 오류가 있나요?\"). 그룹에서는 먼저 @ 해 주세요."
        ],
        ["BridgeHelp"] = [
            "/sessions — list connected servers\n/use <user@host:port> — bind this chat to one\n/mode chat|plan|agent — change the mode\n/new — start a fresh conversation\n/stop — stop the current turn\n/status — show what this chat is set to, including which machines it may touch",
            "/sessions — 列出已连上的服务器\n/use <user@host:port> — 把本聊天绑到某一台\n/mode chat|plan|agent — 换挡位\n/new — 开一段新对话\n/stop — 中止当前这一轮\n/status — 看本聊天的当前设定(含能操作哪些机器)",
            "/sessions — 列出已連上的伺服器\n/use <user@host:port> — 把本聊天綁到某一台\n/mode chat|plan|agent — 換擋位\n/new — 開一段新對話\n/stop — 中止目前這一輪\n/status — 看本聊天的目前設定",
            "/sessions — 接続中のサーバー一覧\n/use <user@host:port> — このチャットを 1 台に紐付け\n/mode chat|plan|agent — モード変更\n/new — 会話をやり直す\n/stop — 実行中のターンを中止\n/status — このチャットの設定を表示",
            "/sessions — 연결된 서버 목록\n/use <user@host:port> — 이 대화를 서버에 연결\n/mode chat|plan|agent — 모드 변경\n/new — 새 대화 시작\n/stop — 현재 턴 중단\n/status — 이 대화의 설정 보기"
        ],
        ["BridgeNewChat"] = ["Started a fresh conversation.", "已开一段新对话。", "已開一段新對話。", "会話をやり直しました。", "새 대화를 시작했습니다."],
        ["BridgeStopped"] = ["Stopped the current turn.", "已中止当前这一轮。", "已中止目前這一輪。", "実行中のターンを中止しました。", "현재 턴을 중단했습니다."],
        ["BridgeNothingRunning"] = ["Nothing is running.", "当前没有正在跑的轮次。", "目前沒有正在跑的輪次。", "実行中のターンはありません。", "실행 중인 턴이 없습니다."],
        ["BridgeStatus"] = ["{0} · mode {1} · approval {2} · bound to {3} ({4})", "{0} · 挡位 {1} · 审批 {2} · 绑定 {3}({4})", "{0} · 擋位 {1} · 審批 {2} · 綁定 {3}({4})", "{0} · モード {1} · 承認 {2} · 接続先 {3}({4})", "{0} · 모드 {1} · 승인 {2} · 대상 {3}({4})"],
        ["BridgeSessionOnline"] = ["connected", "在线", "在線", "接続中", "연결됨"],
        ["BridgeSessionOffline"] = ["not connected right now", "当前未连接", "目前未連線", "現在未接続", "현재 연결 안 됨"],
        ["BridgeNoSessions"] = ["No connected sessions. Connect to a server in VelaShell first.", "当前没有连上的会话。先在 VelaShell 里连一台。", "目前沒有連上的工作階段。先在 VelaShell 裡連一台。", "接続中のセッションがありません。まず VelaShell で接続してください。", "연결된 세션이 없습니다. 먼저 VelaShell에서 서버에 연결하세요."],
        ["BridgeSessions"] = ["Connected sessions:\n{0}", "已连上的会话:\n{0}", "已連上的工作階段:\n{0}", "接続中のセッション:\n{0}", "연결된 세션:\n{0}"],
        ["BridgeBound"] = ["Bound this chat to {0}.", "已把本聊天绑定到 {0}。", "已把本聊天綁定到 {0}。", "このチャットを {0} に紐付けました。", "이 대화를 {0}에 연결했습니다."],
        ["BridgeBindUsage"] = ["Usage: /use user@host:port", "用法:/use user@host:port", "用法:/use user@host:port", "使い方:/use user@host:port", "사용법: /use user@host:port"],
        ["BridgeBindNotFound"] = ["No connected session matches {0}. Try /sessions.", "没有连着的会话匹配 {0}。先看 /sessions。", "沒有連著的工作階段符合 {0}。先看 /sessions。", "{0} に一致する接続中のセッションがありません。/sessions を確認してください。", "{0}과(와) 일치하는 연결된 세션이 없습니다. /sessions를 확인하세요."],
        ["BridgeModeSet"] = ["Mode is now {0}.", "挡位已切到 {0}。", "擋位已切到 {0}。", "モードを {0} にしました。", "모드를 {0}(으)로 변경했습니다."],
        ["BridgeModeUsage"] = ["Usage: /mode chat|plan|agent (or /mode reset)", "用法:/mode chat|plan|agent(或 /mode reset)", "用法:/mode chat|plan|agent(或 /mode reset)", "使い方:/mode chat|plan|agent(または /mode reset)", "사용법: /mode chat|plan|agent (또는 /mode reset)"],
        ["BridgeModeLocked"] = ["Raising the mode from chat is turned off. The bridge is fixed at {0} — change it in VelaShell.", "不允许在聊天里提高挡位。桥接固定为 {0},要改请到 VelaShell 里改。", "不允許在聊天裡提高擋位。橋接固定為 {0},要改請到 VelaShell 裡改。", "チャットからモードを上げることは無効です。ブリッジは {0} 固定です。VelaShell 側で変更してください。", "대화에서 모드를 올릴 수 없습니다. 브리지는 {0}으로 고정되어 있으며 VelaShell에서 변경하세요."],
        // ── 审批 ──
        ["BridgeApprovalAsk"] = ["⚠️ Approval needed — {0}\n{1}\n\nReply y to allow, n to refuse{2} ({3}s until it is refused automatically).", "⚠️ 需要审批 — {0}\n{1}\n\n回复 y 放行,n 拒绝{2}({3} 秒后自动拒绝)。", "⚠️ 需要審批 — {0}\n{1}\n\n回覆 y 放行,n 拒絕{2}({3} 秒後自動拒絕)。", "⚠️ 承認が必要です — {0}\n{1}\n\ny で許可、n で拒否{2}({3} 秒で自動的に拒否)。", "⚠️ 승인이 필요합니다 — {0}\n{1}\n\ny는 허용, n은 거부{2} ({3}초 후 자동 거부)."],
        ["BridgeApprovalAlways"] = [", a to always allow it in this conversation", ",a 表示本次对话内总是放行", ",a 表示本次對話內總是放行", "、a でこの会話中は常に許可", ", a는 이 대화에서 항상 허용"],
        ["BridgeApprovalGranted"] = ["Approved.", "已放行。", "已放行。", "承認しました。", "승인했습니다."],
        ["BridgeApprovalDenied"] = ["Refused.", "已拒绝。", "已拒絕。", "拒否しました。", "거부했습니다."],
        ["BridgeApprovalTimedOut"] = ["Nobody answered in time — refused.", "没人应答,按拒绝处理。", "沒人應答,按拒絕處理。", "応答がなかったため拒否しました。", "응답이 없어 거부했습니다."],
        ["BridgeApprovalNotAllowed"] = ["You are not on the approver list for this bridge.", "你不在这个桥接的审批人名单里。", "你不在這個橋接的審批人名單裡。", "このブリッジの承認者リストに含まれていません。", "이 브리지의 승인자 목록에 없습니다."]
    };
}
