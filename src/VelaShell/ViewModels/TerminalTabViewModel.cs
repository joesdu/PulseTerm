using System.Reactive;
using System.Text;
using Avalonia.Threading;
using ReactiveUI;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Ssh;
using VelaShell.Presentation.ViewModels;
using VelaShell.Services;
using VelaShell.Services.ZModem;
using VelaShell.Terminal;
using VelaShell.Terminal.Input;
using VelaShell.Terminal.Rendering;

namespace VelaShell.ViewModels;

/// <summary>
/// 单个终端标签页的视图模型:持有终端模拟器与(可选的)SSH/本地传输,负责连接状态、
/// 断开/重连、PTY 尺寸同步与命令补全的行跟踪。
/// </summary>
public class TerminalTabViewModel : TabViewModel, IDisposable
{
    private readonly Lock _ptyResizeGate = new();
    private bool _disposed;
    private (int Columns, int Rows)? _pendingPtySize;
    private bool _ptyResizeSending;
    private bool _started;

    /// <summary>
    /// 创建一个拥有终端模拟器但尚未建立实时传输的标签。用于让标签立即以
    /// “连接中”状态显示;待 shell 流可用时(#17)调用 <see cref="AttachTransport" />,
    /// 重连时同样调用它就地重连(#19)。
    /// </summary>
    public TerminalTabViewModel(ITerminalEmulator terminalEmulator)
    {
        TerminalEmulator =
            terminalEmulator ?? throw new ArgumentNullException(nameof(terminalEmulator));
        Title = Strings.NewTab;
        ConnectionStatus = SessionStatus.Disconnected;

        // 保持远程 PTY 尺寸与本地终端网格同步。这绑定在模拟器而非传输上,
        // 因此重连后依然有效。
        TerminalEmulator.PtySizeChanged += OnPtySizeChanged;

        // 命令补全(plan.md #16):旁路跟踪用户键入的命令行;Enter 提交时做回显校验
        // (密码输入无回显,不入历史)后向宿主上报。必须订阅 TypedInput 而非 UserInput:
        // 后者还承载终端的协议自动应答(ESC 开头),会把跟踪器永久打进未知态。
        TerminalEmulator.TypedInput += OnUserInputForTracker;
        InputTracker.CommandSubmitted += OnTrackedCommandSubmitted;
        InputTracker.UnknownLineSubmitted += OnUnknownLineSubmitted;

        // OSC 7:shell 上报当前工作目录 → 供「文件浏览器跟随终端目录」用。仅在 cwd 真正变化时向外播报。
        TerminalEmulator.WorkingDirectoryChanged += OnEmulatorWorkingDirectoryChanged;

        // 工具栏快捷操作:拆除传输但保留标签,
        // 或请求宿主就地重连(#19 流程)。
        DisconnectCommand = ReactiveCommand.Create(
            () =>
            {
                UserRequestedDisconnect = true;
                DetachTransport();
                MarkDisconnected();
            },
            this.WhenAnyValue(x => x.IsConnected)
        );
        ReconnectCommand = ReactiveCommand.Create(
            RequestReconnect,
            this.WhenAnyValue(x => x.IsConnected, connected => !connected)
        );

        // 标签页内失败/断开覆盖层(设计 yxjmg)的“关闭标签页”按钮。
        CloseTabCommand = ReactiveCommand.Create(() =>
            CloseRequested?.Invoke(this, EventArgs.Empty)
        );

        // 同步输入横条(标签右键菜单 → 同步输入)的三个动作。
        ToggleSyncPauseCommand = ReactiveCommand.Create(() =>
        {
            IsSyncPaused = !IsSyncPaused;
        });
        LeaveSyncChannelCommand = ReactiveCommand.Create(LeaveSyncChannel);
        CloseSyncChannelCommand = ReactiveCommand.Create(() =>
        {
            if (SyncChannel is { } channel)
            {
                SyncChannelCloseRequested?.Invoke(channel);
            }
        });
    }

    /// <summary>创建一个标签并立即挂载实时传输(已建立连接的场景)。</summary>
    public TerminalTabViewModel(ITerminalEmulator terminalEmulator, IShellStreamWrapper shellStream)
        : this(terminalEmulator)
    {
        AttachTransport(shellStream ?? throw new ArgumentNullException(nameof(shellStream)));
    }

    /// <summary>该标签所属会话的唯一标识,用于与宿主的会话管理关联。</summary>
    public Guid SessionId { get; set; }

    /// <summary>
    /// 本标签的 SFTP 面板开关状态(标签生命周期内记忆,含断线重连):null 表示尚未
    /// 决定过(首次连接时取设置「连接后自动打开文件浏览器」)。用户在该标签上
    /// 打开/关闭面板时由宿主回写,切回本标签按此恢复,与其他标签互不影响。
    /// </summary>
    public bool? FileBrowserOpen { get; set; }

    /// <summary>
    /// 本标签页的资源面板数据(悬停 > 400ms 时显示,§11)。
    /// 握手完成后才赋值,而标签 ToolTip.Tip 在标签创建时就已绑定到它,
    /// 必须发变更通知,否则 Tip 停留在 null,悬浮面板永远不弹。
    /// </summary>
    public ResourceMonitorViewModel? ResourceMonitor
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>本标签连接所用的配置,用于就地重连(#19)。</summary>
    public SessionProfile? Profile { get; set; }

    /// <summary>
    /// 该连接的稳定标识色(标签页色条与 SFTP 面板同色联动,防多标签误操作)。
    /// 本地终端/无配置标签返回透明。Profile 在标签创建时就已赋值,绑定一次性读取即可。
    /// </summary>
    public Avalonia.Media.IBrush ConnectionAccentBrush =>
        Profile is { } profile
            ? ConnectionAccent.BrushFor(profile.Id)
            : Avalonia.Media.Brushes.Transparent;

    // ---- 同步输入频道(标签右键菜单 → 同步输入,对等转发见 SyncInputCoordinator) ----

    /// <summary>所属同步输入频道;null = 未加入。经 <see cref="JoinSyncChannel" /> / <see cref="LeaveSyncChannel" /> 变更。</summary>
    public SyncInputChannel? SyncChannel
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(SyncChannelLetter));
            this.RaisePropertyChanged(nameof(SyncChannelBrush));
            this.RaisePropertyChanged(nameof(IsInSyncChannel));
        }
    }

    /// <summary>是否已加入某个同步输入频道(标签头字母与终端上方横条的可见性)。</summary>
    public bool IsInSyncChannel => SyncChannel is not null;

    /// <summary>频道字母(A/B/C/D),未加入频道时为空串。</summary>
    public string SyncChannelLetter => SyncChannel?.ToString() ?? string.Empty;

    /// <summary>频道标识色,未加入频道时透明。</summary>
    public Avalonia.Media.IBrush SyncChannelBrush =>
        SyncChannel is { } channel
            ? SyncInputChannels.BrushFor(channel)
            : Avalonia.Media.Brushes.Transparent;

    /// <summary>true = 本标签的同步输入已暂停:既不向频道发送,也不接收频道输入。</summary>
    public bool IsSyncPaused
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>暂停/恢复本标签的同步输入(横条“暂停”按钮)。</summary>
    public ReactiveCommand<Unit, Unit> ToggleSyncPauseCommand { get; }

    /// <summary>让本标签退出频道(横条“离开频道”按钮与关闭钮)。</summary>
    public ReactiveCommand<Unit, Unit> LeaveSyncChannelCommand { get; }

    /// <summary>请求关闭整个频道:所有成员标签一并退出(横条“关闭频道”按钮)。</summary>
    public ReactiveCommand<Unit, Unit> CloseSyncChannelCommand { get; }

    /// <summary>“关闭频道”触发,由 SyncInputCoordinator 让频道内全部标签退出。</summary>
    public event Action<SyncInputChannel>? SyncChannelCloseRequested;

    /// <summary>加入频道(已在其他频道时直接改挂新频道),并清除暂停态。</summary>
    public void JoinSyncChannel(SyncInputChannel channel)
    {
        IsSyncPaused = false;
        SyncChannel = channel;
    }

    /// <summary>退出当前频道并清除暂停态(未加入时为空操作)。</summary>
    public void LeaveSyncChannel()
    {
        SyncChannel = null;
        IsSyncPaused = false;
    }

    /// <summary>
    /// 把同频道其他标签的用户输入直写本标签 PTY。走桥的 SendRaw 而非终端控件的
    /// WriteInput:不触发本标签的 TypedInput(防转发回环),也不进入命令补全的行
    /// 跟踪——智能建议弹层只属于用户正在键入的焦点标签。以 IsConnected 为闸门,
    /// 关闭中的标签(Dispose 先复位 IsConnected)不会被写入。
    /// </summary>
    public void WriteSyncInput(byte[] data)
    {
        if (!IsConnected)
        {
            return;
        }
        Bridge?.SendRaw(data);
    }

    /// <summary>true = 当前的程序化写入不转发到同步频道(快捷命令自带多目标分发)。</summary>
    public bool IsSyncForwardSuppressed { get; private set; }

    /// <summary>本标签正在键入的命令行跟踪器(命令补全弹层的数据入口,见视图侧)。</summary>
    public TerminalInputTracker InputTracker { get; } = new();

    /// <summary>补全建议提供器(宿主 MainWindowViewModel 注入;null = 补全不可用)。</summary>
    public CommandSuggestionProvider? SuggestionProvider { get; set; }

    /// <summary>
    /// ZMODEM 下载目录选择委托(由视图层 MainWindow 注入,视图层独占 StorageProvider)。
    /// 后台接收线程经它弹出原生文件夹选择框;返回所选目录,取消则为 null。null = 未接线,ZMODEM 不启用。
    /// </summary>
    public Func<ZModemFolderPromptRequest, CancellationToken, Task<string?>>? ZModemDownloadFolderPicker { get; set; }

    /// <summary>
    /// ZMODEM 上传文件选择委托(由视图层 MainWindow 注入)。远端跑 <c>rz</c> 时,
    /// 后台发送线程经它弹出多选文件框;返回所选文件的绝对路径,取消则为空清单。
    /// null = 未接线,遇到 <c>rz</c> 不接管(字节原样喂终端)。
    /// </summary>
    public Func<bool, CancellationToken, Task<IReadOnlyList<string>>>? ZModemUploadFilePicker { get; set; }

    /// <summary>共享的文件传输面板(宿主注入),用于展示 ZMODEM 接收进度;null = 不展示进度。</summary>
    public FileTransferViewModel? FileTransfer { get; set; }

    /// <summary>读取应用设置的委托(宿主注入),ZMODEM 落地据此取默认下载目录与冲突策略。</summary>
    public Func<Task<AppSettings>>? GetSettingsAsync { get; set; }

    /// <summary>用户在本标签提交了一条通过回显校验的命令(宿主记入全局命令历史)。</summary>
    public event Action<string>? CommandLineSubmitted;

    private void OnUserInputForTracker(byte[] data)
    {
        InputTracker.Process(data);
        // 每键路径:先查开关再拼实参,否则关着诊断每键也分配 3 个字符串。
        if (SuggestDiag.IsEnabled)
        {
            SuggestDiag.Log(
                "typed",
                $"""
                bytes=[{Convert.ToHexString(data)}] input="{InputTracker.CurrentInput ?? "<unknown>"}"
                """
            );
        }
    }

    private void OnTrackedCommandSubmitted(string command)
    {
        // 回显校验:所键入的文本应已被 shell 回显到屏上;密码提示符不回显 → 不记录,
        // 防止口令进历史。注意桥接层的输出是按帧合并 Feed 的——Enter 瞬间最后几个
        // 字符的回显可能还在队列里,同步校验失败时延迟 200ms 后在整个可视区做二次
        // 校验(此时命令行可能已随输出上移,故扫全屏而非只看光标行)。
        if (TerminalEmulator is not VelaTerminalControl control)
        {
            return;
        }
        if (CursorLineContains(control, command))
        {
            CommandLineSubmitted?.Invoke(command);
            return;
        }
        DispatcherTimer.RunOnce(
            () =>
            {
                if (!_disposed && ScreenContains(control, command))
                {
                    CommandLineSubmitted?.Invoke(command);
                }
            },
            TimeSpan.FromMilliseconds(200)
        );
    }

    private void OnUnknownLineSubmitted()
    {
        // 行内容本地不可知(用户按过方向键/Tab 等):改从屏幕的光标行提取命令——
        // 提示符之后的文本就是 shell 将执行的整行。读取发生在 Enter 的瞬间,
        // 先于命令输出刷屏;密码提示行没有提示符结尾标记,天然不会被提取。
        if (TerminalEmulator is not VelaTerminalControl control)
        {
            return;
        }
        try
        {
            string? command = ExtractCommandAfterPrompt(control.GetBufferLine(control.CursorRow));
            if (!string.IsNullOrWhiteSpace(command))
            {
                CommandLineSubmitted?.Invoke(command);
            }
        }
        catch
        {
            // 读缓冲失败时宁可漏记不误记。
        }
    }

    /// <summary>
    /// 从提示符行提取命令:取最早出现的提示符结尾标记("$ "/"# "/"❯ "/"% ")之后的文本。
    /// 识别不了的提示符样式(无标记)返回 null——宁可漏记不误记。
    /// </summary>
    internal static string? ExtractCommandAfterPrompt(string line)
    {
        int best = -1;
        foreach (string marker in (ReadOnlySpan<string>)["$ ", "# ", "❯ ", "% "])
        {
            int index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }
        if (best < 0)
        {
            return null;
        }
        string command = line[(best + 2)..].Trim();
        return command.Length == 0 ? null : command;
    }

    private static bool CursorLineContains(VelaTerminalControl control, string command)
    {
        try
        {
            return control
                .GetBufferLine(control.CursorRow)
                .Contains(command, StringComparison.Ordinal);
        }
        catch
        {
            // 读缓冲失败(极端竞态)时宁可漏记不误记。
            return false;
        }
    }

    private static bool ScreenContains(VelaTerminalControl control, string command)
    {
        try
        {
            for (int row = 0; row < control.Rows; row++)
            {
                if (control.GetBufferLine(row).Contains(command, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 同上:宁可漏记不误记。
        }
        return false;
    }

    /// <summary>
    /// 本地终端标签(§12 P1-1)对应的 shell;null = SSH 会话。重开(Enter/Ctrl+R)
    /// 用它重新拉起本地进程。
    /// </summary>
    public LocalShellInfo? LocalShell { get; set; }

    /// <summary>
    /// true = 本次断开由用户主动触发(断开按钮),自动重连(设置 → 常规)不介入;
    /// 重新挂载传输时复位。
    /// </summary>
    public bool UserRequestedDisconnect { get; private set; }

    /// <summary>本标签在状态栏显示的连接摘要,例如 "SSH • root@host:22"。</summary>
    public string ConnectionSummary { get; init; } = string.Empty;

    /// <summary>本会话声明的终端模拟类型。</summary>
    public string TerminalTypeName { get; init; } = "xterm-256color";

    /// <summary>本会话使用的字符编码。</summary>
    public string EncodingName { get; init; } = "UTF-8";

    /// <summary>本标签持有的终端模拟器,跨重连保持不变(拥有滚动缓冲区)。</summary>
    public ITerminalEmulator TerminalEmulator { get; }

    /// <summary>该终端 shell 当前工作目录(经 OSC 7 上报);未知/未上报时为 null。</summary>
    public string? TerminalWorkingDirectory { get; private set; }

    /// <summary>终端 shell 工作目录【变化】时触发(去重:同值不重复触发)。参数为绝对路径。</summary>
    public event Action<string>? WorkingDirectoryChanged;

    private void OnEmulatorWorkingDirectoryChanged(string path)
    {
        // OSC 7 每次提示符都发(即使 cwd 未变);只在真正变化时对外播报,避免把用户在文件浏览器里
        // 的手动切换在每次回车时都拽回终端目录(仅当终端 cd 到新目录才同步)。
        if (string.IsNullOrEmpty(path) || string.Equals(path, TerminalWorkingDirectory, StringComparison.Ordinal))
        {
            return;
        }
        TerminalWorkingDirectory = path;
        WorkingDirectoryChanged?.Invoke(path);
    }

    /// <summary>当前挂载的 shell 数据流;未连接或断开后为 null。</summary>
    public IShellStreamWrapper? ShellStream { get; private set; }

    /// <summary>连接 shell 流与终端模拟器的桥接器;未连接时为 null。</summary>
    public SshTerminalBridge? Bridge { get; private set; }

    /// <summary>
    /// 本标签的连接状态。赋值时同步 <see cref="IsConnected" />、在连接成功时清除错误,
    /// 并刷新失败/断开覆盖层的可见性与文案。
    /// </summary>
    public new SessionStatus ConnectionStatus
    {
        get => base.ConnectionStatus;
        set
        {
            base.ConnectionStatus = value;
            IsConnected = value == SessionStatus.Connected;

            // 连接成功即清除上次失败信息(隐去覆盖层);其余状态变化都要刷新覆盖层可见性。
            if (value == SessionStatus.Connected)
            {
                ConnectionError = null;
            }
            this.RaisePropertyChanged(nameof(ShowDisconnectedOverlay));
            this.RaisePropertyChanged(nameof(DisconnectOverlayTitle));
            this.RaisePropertyChanged(nameof(DisconnectOverlayDetail));
        }
    }

    /// <summary>
    /// 最近一次连接失败的原因;非空时覆盖层显示为“连接失败”,否则为掉线的
    /// “连接已断开”(设计 yxjmg / dcOverlay)。
    /// </summary>
    public string? ConnectionError
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(HasConnectionError));
            this.RaisePropertyChanged(nameof(DisconnectOverlayTitle));
            this.RaisePropertyChanged(nameof(DisconnectOverlayDetail));
        }
    }

    /// <summary>是否存在连接错误(决定覆盖层显示“连接失败”还是“连接已断开”)。</summary>
    public bool HasConnectionError => !string.IsNullOrEmpty(ConnectionError);

    /// <summary>
    /// 标签页内失败/断开覆盖层(设计 yxjmg)的可见性:未连接(断开/错误)且是一个
    /// 真实会话标签(SSH 或本地)时显示。连接中/已连接不显示。
    /// </summary>
    public bool ShowDisconnectedOverlay =>
        !_disposed
        && ConnectionStatus is SessionStatus.Disconnected or SessionStatus.Error
        && (Profile is not null || LocalShell is not null);

    /// <summary>失败/断开覆盖层的标题:有错误时为“连接失败”,否则为“连接已断开”。</summary>
    public string DisconnectOverlayTitle =>
        HasConnectionError
            ? Strings.Get("Msg_ConnectionFailedTitle")
            : Strings.Get("Msg_ConnectionClosedTitle");

    /// <summary>失败/断开覆盖层的详情:有错误时显示具体原因,否则显示掉线主机提示。</summary>
    public string DisconnectOverlayDetail =>
        HasConnectionError
            ? ConnectionError!
            : Strings.Format("Msg_SshConnectionLostDetail", OverlayHostLabel);

    private string OverlayHostLabel => Profile is { } p ? $"{p.Host}:{p.Port}" : Title;

    /// <summary>最近一次测得的连接延迟;未知时为 null。</summary>
    public TimeSpan? Latency
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>是否处于已连接状态(随 <see cref="ConnectionStatus" /> 联动)。</summary>
    public bool IsConnected
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>已尝试的自动重连次数,连接成功或重置时归零。</summary>
    public int ReconnectAttempts
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// 最大自动重连次数,唯一来源是设置 → 常规 → 自动重连(General.MaxRetries),
    /// 由宿主在断开处理时同步下发;默认值与设置模型一致,仅作未注入时的兜底。
    /// </summary>
    public int MaxReconnectAttempts
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = 3;

    /// <summary>是否仍允许自动重连(重连次数未达上限)。</summary>
    public bool CanReconnect => ReconnectAttempts < MaxReconnectAttempts;

    /// <summary>断开实时传输,但保留标签(及其缓冲区)以便重连。</summary>
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }

    /// <summary>请求对已断开标签就地重连(等同于 Enter / Ctrl+R)。</summary>
    public ReactiveCommand<Unit, Unit> ReconnectCommand { get; }

    /// <summary>从标签页内失败/断开覆盖层(设计 yxjmg)关闭标签页。</summary>
    public ReactiveCommand<Unit, Unit> CloseTabCommand { get; }

    /// <summary>
    /// 释放标签资源:解绑事件、即时销毁终端模拟器(UI 安全),并把耗时的网络拆除
    /// (取消读循环、关闭 SSH 通道)放到后台线程,避免关闭标签时卡住 UI(#18)。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // 立即标记为未连接:同步频道转发(WriteSyncInput)以 IsConnected 为闸门。
        // 频道内其他标签的输入可能与本标签的关闭并发,不复位 IsConnected 的话,
        // 转发会向已释放的桥写入而与释放竞争。
        IsConnected = false;
        TerminalEmulator.PtySizeChanged -= OnPtySizeChanged;
        TerminalEmulator.TypedInput -= OnUserInputForTracker;
        TerminalEmulator.WorkingDirectoryChanged -= OnEmulatorWorkingDirectoryChanged;
        InputTracker.CommandSubmitted -= OnTrackedCommandSubmitted;
        InputTracker.UnknownLineSubmitted -= OnUnknownLineSubmitted;

        // 即时、UI 安全的拆除,使标签立即关闭:这里只解绑模拟器的
        // Updated 事件处理,不涉及网络 I/O。
        TerminalEmulator.Dispose();

        // 网络拆除(取消读循环、关闭 SSH 通道)最多可能阻塞数秒,因此放到
        // 调用方(UI)线程之外执行 —— 此时标签已经消失。
        // 修复了“关闭标签卡住 UI”的问题(#18)。Bridge.Dispose 是幂等的。
        SshTerminalBridge? bridge = Bridge;
        if (bridge is not null)
        {
            bridge.Closed -= OnBridgeClosed;
            Bridge = null;
            Task.Run(bridge.Dispose);
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 会话断开时(远端关闭了通道)触发,使 UI 显示断开覆盖层并提供重连(#19)。
    /// </summary>
    public event EventHandler? Disconnected;

    /// <summary>用户请求重连已断开标签时触发(Enter / Ctrl+R)。</summary>
    public event EventHandler? ReconnectRequested;

    /// <summary>标签页内失败/断开覆盖层(设计 yxjmg)的“关闭标签页”触发,由宿主移除该标签。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>请求重连,但仅在已断开状态下有效(否则为空操作)。</summary>
    public void RequestReconnect()
    {
        if (ConnectionStatus == SessionStatus.Disconnected)
        {
            ReconnectRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 通过正常用户输入通道把快捷命令文本发送到终端。只发正文不附加回车——
    /// 快捷命令可能是不完整的模板(如缺少参数),由用户在终端里补全后自行按 Enter 执行。
    /// </summary>
    /// <returns>文本已发送时为 true;终端未连接或命令为空时为 false。</returns>
    public bool TrySendCommandText(string command)
    {
        if (!IsConnected || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }
        string payload = command.TrimEnd('\r', '\n');

        // 快捷命令可能同时下发给同频道的多个标签,若再经同步频道转发,频道内
        // 每个标签都会收到重复注入;WriteInput 同步触发 TypedInput,压制窗口有效。
        // IsProgrammaticInput 同理:跟踪器仍需感知注入文本以保持行状态一致,
        // 但补全弹层不该把程序注入当作用户键入而弹出。
        IsSyncForwardSuppressed = true;
        IsProgrammaticInput = true;
        try
        {
            TerminalEmulator.WriteInput(Encoding.UTF8.GetBytes(payload));
        }
        finally
        {
            IsSyncForwardSuppressed = false;
            IsProgrammaticInput = false;
        }
        return true;
    }

    /// <summary>
    /// 程序注入输入(快捷命令下发等)期间为 true。注入与用户键入共用 TypedInput
    /// 通道(行跟踪必需),视图据此区分两者,注入期间不弹命令补全。
    /// </summary>
    public bool IsProgrammaticInput { get; private set; }

    /// <summary>
    /// 把初始化命令注入远端 shell 并静默执行:发送前在桥上装回显抑制器,
    /// 把 PTY 回显的这一行从输出流剥掉,不在界面显示。前导空格让
    /// HISTCONTROL=ignoreboth 不记历史;抑制针 needle 不含该空格(空格太常见,
    /// 不适合做流匹配锚点),残留的空格与光标位置由命令本身的补行脚本消化。
    /// </summary>
    public void SendSilentCommand(string command)
    {
        string payload = command.Trim();
        if (Bridge is null || payload.Length == 0)
        {
            return;
        }
        Bridge.SuppressEchoOnce(Encoding.UTF8.GetBytes(payload + "\r\n"));

        // 直写 PTY(SendRaw)而非 WriteInput:注入不是用户键入,不得进入命令补全的
        // 行跟踪——补行脚本里的 ESC 字节曾把跟踪器打进未知态,SSH 标签建议全灭。
        Bridge.SendRaw(Encoding.UTF8.GetBytes(" " + payload + "\n"));
    }

    /// <summary>启动桥接的 I/O 泵送(幂等;无桥或已启动时为空操作)。</summary>
    public void Start()
    {
        if (_started || Bridge is null)
        {
            return;
        }
        Bridge.Start();
        _started = true;
    }

    /// <summary>
    /// 挂载实时 shell 流并准备 I/O 泵送(之后调用 <see cref="Start" />)。
    /// 会先在后台拆除任何先前的传输,因此它同时充当复用同一标签与回滚
    /// 缓冲区的重连入口(#19)。
    /// </summary>
    public void AttachTransport(IShellStreamWrapper shellStream)
    {
        ArgumentNullException.ThrowIfNull(shellStream);
        ObjectDisposedException.ThrowIf(_disposed, this);
        DetachTransport();
        UserRequestedDisconnect = false;
        ShellStream = shellStream;
        var bridge = new SshTerminalBridge(TerminalEmulator, shellStream);
        bridge.Closed += OnBridgeClosed;
        AttachZModemRouter(bridge, shellStream);
        Bridge = bridge;
        _started = false;

        // 通道以固定的默认网格(120×32)打开。到此时控件通常已经布局到真实视口,
        // 但承载该尺寸的 PtySizeChanged 在 ShellStream 仍为 null 时触发并被丢弃了。
        // 把模拟器当前网格推送到新流,使远程 PTY 的 winsize 与实际可见区域一致 ——
        // 否则全屏程序(htop/nano)会读取过期的 32 行尺寸,把页脚画在屏幕中央,
        // 导致终端下半部分空白。
        SyncPtySize();
    }

    /// <summary>
    /// 把模拟器当前的网格尺寸重新发送到实时 shell 流,使远程 PTY 的
    /// winsize 与真实视口一致,而非通道打开时的固定尺寸。
    /// </summary>
    private void SyncPtySize()
    {
        if (TerminalEmulator is { Columns: > 0, Rows: > 0 })
        {
            OnPtySizeChanged(TerminalEmulator.Columns, TerminalEmulator.Rows);
        }
    }

    /// <summary>在 UI 线程外拆除当前传输,同时保留标签与缓冲区完好。</summary>
    public void DetachTransport()
    {
        SshTerminalBridge? bridge = Bridge;
        if (bridge is null)
        {
            return;
        }
        bridge.Closed -= OnBridgeClosed;
        // 拆除传输前先取消进行中的 ZMODEM 会话(向对端发 ZCAN),避免后台接收任务悬空。
        bridge.ZModemRouter?.CancelActiveSession();
        Bridge = null;
        ShellStream = null;
        _started = false;

        // Bridge.Dispose 也会释放 shell 流;放到调用方线程之外执行。
        Task.Run(bridge.Dispose);
    }

    /// <summary>
    /// 为新建的桥装配 ZMODEM 路由器(仅当目录选择委托、传输面板与设置委托都已注入时)。
    /// 必须在 <see cref="Start" /> 之前调用;每个会话经 sinkFactory / sourceFactory 新建一个实例,
    /// 因此目录与文件选择都不跨会话缓存。上传选择器可选:未注入时遇到 <c>rz</c> 不接管。
    /// </summary>
    private void AttachZModemRouter(SshTerminalBridge bridge, IShellStreamWrapper shellStream)
    {
        Func<ZModemFolderPromptRequest, CancellationToken, Task<string?>>? picker = ZModemDownloadFolderPicker;
        FileTransferViewModel? transfer = FileTransfer;
        Func<Task<AppSettings>>? settings = GetSettingsAsync;
        if (picker is null || transfer is null || settings is null)
        {
            return;
        }
        Func<bool, CancellationToken, Task<IReadOnlyList<string>>>? uploadPicker = ZModemUploadFilePicker;
        var observer = new ZModemTransferObserver(transfer);
        bridge.ZModemRouter = new Terminal.ZModem.ZModemTerminalRouter(
            shellStream,
            () => new FolderZModemFileSink(picker, settings),
            uploadPicker is null ? null : () => new FileZModemFileSource(uploadPicker),
            Core.ZModem.Model.ZModemOptions.Default,
            observer);
    }

    private void OnBridgeClosed()
    {
        // 在读线程上触发;将响应式状态变更封送到 UI 线程。
        if (Dispatcher.UIThread.CheckAccess())
        {
            MarkDisconnected();
        }
        else
        {
            Dispatcher.UIThread.Post(() => MarkDisconnected());
        }
    }

    /// <summary>
    /// 初始连接失败:保留标签并在标签页内显示失败覆盖层(设计 yxjmg),不销毁标签、
    /// 不弹全局框。置为断开态以启用“重新连接”;不触发 <see cref="Disconnected" /> 事件——
    /// 初始连接失败不应自动重连(尤其认证失败),由用户手动决定重连或关闭。
    /// </summary>
    public void MarkConnectionFailed(string message)
    {
        if (_disposed)
        {
            return;
        }

        // 先记录原因(覆盖层显示“连接失败”),再切断开态刷新覆盖层可见性。
        ConnectionError = message;
        ConnectionStatus = SessionStatus.Disconnected;
    }

    /// <summary>
    /// 将标签切换到断开状态并通知监听者(幂等)。
    /// <paramref name="reason" /> 非空时(如重连失败)覆盖层显示为“连接失败 + 具体原因”,
    /// 否则为普通掉线的“连接已断开”。
    /// </summary>
    public void MarkDisconnected(string? reason = null)
    {
        if (_disposed || ConnectionStatus == SessionStatus.Disconnected)
        {
            return;
        }
        if (!string.IsNullOrEmpty(reason))
        {
            ConnectionError = reason;
        }
        ConnectionStatus = SessionStatus.Disconnected;
        FeedDisconnectNotice();
        Disconnected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 向终端打印一行红色“连接已关闭”横幅以及重连提示,让用户知道按 Enter /
    /// 点“重新连接”按钮即可恢复会话。手动断开与远端关闭时都会执行。
    /// </summary>
    private void FeedDisconnectNotice()
    {
        string notice =
            $"\r\n\u001b[0m\u001b[31m● {Strings.TerminalDisconnectedNotice}\u001b[0m\r\n\u001b[90m{Strings.TerminalReconnectHint}\u001b[0m\r\n";
        try
        {
            TerminalEmulator.Feed(Encoding.UTF8.GetBytes(notice));
        }
        catch
        {
            // 纯属装饰;绝不让提示破坏断开流程。
        }
    }

    /// <summary>
    /// 在 UI 线程外把网格变化按严格顺序转发到 SSH 通道,并合并突发为最新尺寸。
    /// 原先每次事件都 fire-and-forget 地 Task.Run,在拖拽风暴期间可能乱序送达尺寸,
    /// 导致远程 shell 拿到过期网格 —— 其随后的提示符重绘便会破坏缓冲区。
    /// </summary>
    private void OnPtySizeChanged(int columns, int rows)
    {
        if (_disposed || ShellStream is null || !ShellStream.CanWrite)
        {
            return;
        }
        lock (_ptyResizeGate)
        {
            _pendingPtySize = (columns, rows);
            if (_ptyResizeSending)
            {
                return;
            }
            _ptyResizeSending = true;
        }
        _ = Task.Run(DrainPtyResizeQueue);
    }

    private void DrainPtyResizeQueue()
    {
        while (true)
        {
            (int Columns, int Rows) size;
            lock (_ptyResizeGate)
            {
                if (_pendingPtySize is null)
                {
                    _ptyResizeSending = false;
                    return;
                }
                size = _pendingPtySize.Value;
                _pendingPtySize = null;
            }
            IShellStreamWrapper? stream = ShellStream;
            if (_disposed || stream is null || !stream.CanWrite)
            {
                lock (_ptyResizeGate)
                {
                    _pendingPtySize = null;
                    _ptyResizeSending = false;
                }
                return;
            }
            try
            {
                stream.Resize(size.Columns, size.Rows);
            }
            catch
            {
                // 在已拆除或不支持的通道上调整尺寸是非致命的。
            }
        }
    }

    /// <summary>自动重连尝试计数加一。</summary>
    public void IncrementReconnectAttempt()
    {
        ReconnectAttempts++;
    }

    /// <summary>把自动重连尝试计数归零(连接成功后调用)。</summary>
    public void ResetReconnectAttempts()
    {
        ReconnectAttempts = 0;
    }
}
