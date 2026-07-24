using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VelaShell.Core.Models;

/// <summary>应用全局设置的根模型:序列化为 SonnetDB app_config 文档,聚合各页面的分组选项。</summary>
public class AppSettings
{
    /// <summary>界面显示语言(BCP-47 文化名,如 zh-CN);切换后实时应用。</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>界面主题标识(如 dark / light)。</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>强调色覆盖(十六进制字符串,如 "#00D4AA");空 = 使用主题默认值。</summary>
    public string AccentColor { get; set; } = "#E91E63";

    /// <summary>
    /// 终端渲染使用的等宽字体族名。默认内置的 Cascadia Mono(随程序分发,Linux/macOS
    /// 无需安装;无连字——本终端按格钉排,连字无法正确呈现)。也可填任何系统已装字体;
    /// 已保存的旧值(如 JetBrains Mono)不受影响。CJK 走系统回退。
    /// </summary>
    public string TerminalFont { get; set; } = "Cascadia Mono";

    /// <summary>终端字体字号(磅)。</summary>
    public int TerminalFontSize { get; set; } = 14;

    /// <summary>
    /// 终端可回滚保留的最大历史行数。默认 10000(对齐 Windows Terminal 量级):
    /// 满载时每标签约 20 行宽 KB 级——旧默认 50000 满载单标签可达 100-240MB,
    /// 十个长会话标签就是 GB 级,是全应用内存占用的第一大头。
    /// 已保存 50000 的旧配置不强改(用户可自行调整),仅影响新配置。
    /// </summary>
    public int ScrollbackLines { get; set; } = 10000;

    /// <summary>新建 SSH 连接的默认端口。</summary>
    public int DefaultPort { get; set; } = 22;

    /// <summary>向对端宣告的终端模拟类型(即 TERM 变量,默认 xterm-256color)。</summary>
    public string TerminalType { get; set; } = "xterm-256color";

    /// <summary>用于解码对端输出内容的字符编码(默认 UTF-8)。</summary>
    public string TerminalEncoding { get; set; } = "UTF-8";

    // —— 设计 §14 各页面的分组选项(SonnetDB app_config 文档,JSON 嵌套) ——
    // 部分选项当前仅持久化,由后续功能消费。

    /// <summary>「常规」页的分组选项。</summary>
    public GeneralOptions General { get; set; } = new();

    /// <summary>「外观」页的分组选项。</summary>
    public AppearanceOptions Appearance { get; set; } = new();

    /// <summary>「终端」页的行为分组选项。</summary>
    public TerminalBehaviorOptions TerminalBehavior { get; set; } = new();

    /// <summary>「文件传输」页的分组选项。</summary>
    public TransferOptions Transfer { get; set; } = new();

    /// <summary>「安全审计」页的分组选项。</summary>
    public SecurityOptions Security { get; set; } = new();

    /// <summary>「密钥管理」页的分组选项。</summary>
    public KeyOptions Keys { get; set; } = new();

    /// <summary>
    /// 载入后的规整(由设置服务在反序列化后调用):把旧字段迁移到唯一权威字段,
    /// 保证每个行为只有一个数据来源(设置审计 C-01/M-01)。
    /// </summary>
    public void Normalize()
    {
        // 旧版独立“视觉闪烁”开关会覆盖 BellMode;迁移为三态 BellMode 后清除旧值。
        if (TerminalBehavior.VisualBell)
        {
            TerminalBehavior.BellMode = "visual";
            TerminalBehavior.VisualBell = false;
        }
    }
}

/// <summary>
/// 分组选项基类:实现 INPC,设置页直接 TwoWay 绑定选项对象时,
/// 单项修改可被其它绑定(条件显隐、即时预览)实时观察到。
/// </summary>
public abstract class ObservableOptions : INotifyPropertyChanged
{
    /// <summary>属性值变化时触发,用于设置页 TwoWay 绑定的实时观察。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>为字段赋值:值有变化时更新并触发 <see cref="PropertyChanged" />,值相同则跳过。</summary>
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}

/// <summary>设置 - 常规(设计 2BIRD)。</summary>
public class GeneralOptions : ObservableOptions
{
    // 启动
    /// <summary>是否开机自启动应用。</summary>
    public bool LaunchAtStartup
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>启动时是否恢复上次退出前已连接的会话。</summary>
    public bool RestoreSessionsOnStartup
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>启动时是否检查应用更新。</summary>
    public bool CheckUpdatesOnStartup
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>是否最小化到系统托盘而非任务栏。</summary>
    public bool MinimizeToTray
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>“恢复会话”的持久化槽位(不出现在设置界面):退出时已连接会话的配置 id。</summary>
    public List<Guid> LastOpenProfileIds { get; set; } = [];

    // 连接默认值
    /// <summary>建立连接的超时时间(秒)。</summary>
    public int ConnectTimeoutSeconds
    {
        get;
        set => Set(ref field, value);
    } = 30;

    /// <summary>连接保活心跳间隔(秒)。</summary>
    public int KeepAliveSeconds
    {
        get;
        set => Set(ref field, value);
    } = 60;

    /// <summary>最大自动重连次数(自动重连的唯一次数来源,设置审计 C-02/N-06)。</summary>
    public int MaxRetries
    {
        get;
        set => Set(ref field, value);
    } = 3;

    // 数据与存储
    /// <summary>是否记录终端会话日志。</summary>
    public bool SessionLogging
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>终端会话日志保留天数(区别于文件传输日志的保留天数,设置审计 N-08/N-09)。</summary>
    public int LogRetentionDays
    {
        get;
        set => Set(ref field, value);
    } = 30;

    // 更新
    /// <summary>更新通道(如 stable / beta)。</summary>
    public string UpdateChannel
    {
        get;
        set => Set(ref field, value);
    } = "stable";

    /// <summary>是否自动下载可用更新。</summary>
    public bool AutoDownloadUpdates
    {
        get;
        set => Set(ref field, value);
    } = true;

    // 行为
    /// <summary>退出应用前确认;开启“最小化到托盘”后点关闭按钮只隐藏窗口,不触发本确认(设置审计 C-03)。</summary>
    public bool ConfirmBeforeClose
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>切换终端标签时是否在资源管理器中展开、选中并滚动到对应连接。</summary>
    public bool FollowActiveTerminalInExplorer
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>连接断开时是否弹出通知。</summary>
    public bool NotifyOnDisconnect
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>连接断开后是否自动尝试重连。</summary>
    public bool AutoReconnect
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>自动重连的间隔时间(秒)。</summary>
    public int ReconnectIntervalSeconds
    {
        get;
        set => Set(ref field, value);
    } = 5;

    // 通知
    /// <summary>连接断开时播放提示音(作用域是 SSH 断开事件,设置审计 N-01)。</summary>
    public bool SoundAlerts
    {
        get;
        set => Set(ref field, value);
    }

    // 隐私与安全
    /// <summary>是否启用主密码保护本地凭据。</summary>
    public bool MasterPasswordProtection
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>是否记住并保存连接密码。</summary>
    public bool RememberPasswords
    {
        get;
        set => Set(ref field, value);
    } = true;
}

/// <summary>
/// 设置 - 外观(设计 ZAbb9)。设置页直接 TwoWay 绑定本对象,
/// 单项修改需要能被设置 VM 观察到,用于外观「即时预览」与颜色色块的实时刷新。
/// </summary>
public class AppearanceOptions : ObservableOptions
{
    /// <summary>应用界面(非终端)使用的字体族名。</summary>
    public string UiFont
    {
        get;
        set => Set(ref field, value);
    } = "Inter";

    /// <summary>应用界面字体字号(磅)。</summary>
    public int UiFontSize
    {
        get;
        set => Set(ref field, value);
    } = 13;

    /// <summary>窗口整体不透明度(百分比,100 = 不透明)。</summary>
    public int WindowOpacityPercent
    {
        get;
        set => Set(ref field, value);
    } = 100;

    /// <summary>
    /// 应用背景图片的本地文件绝对路径。空 = 不使用背景图(默认,行为与旧版完全一致)。
    /// 设置后图片铺在窗口最底层(UniformToFill),透过半透明的终端与文件面板背景显示。
    /// </summary>
    public string BackgroundImagePath
    {
        get;
        set => Set(ref field, value ?? "");
    } = "";

    /// <summary>背景图片图层的不透明度(百分比,0-100);越低图片越淡。仅在设置了背景图时生效。</summary>
    public int BackgroundImageOpacity
    {
        get;
        set => Set(ref field, value);
    } = 100;

    /// <summary>
    /// 内容背景(整窗遮罩)的不透明度(百分比,20-100);越高越盖住背景图、内容越实,越低越透出背景图。
    /// 统一作用于终端、SFTP 面板、侧边栏等所有内容区(由一层整窗 scrim 遮罩承担,不分区)。仅在设置了背景图时生效。
    /// </summary>
    public int ContentBackgroundOpacity
    {
        get;
        set => Set(ref field, value);
    } = 85;

    /// <summary>标签栏位置(如 top / bottom)。</summary>
    public string TabBarPosition
    {
        get;
        set => Set(ref field, value);
    } = "top";

    /// <summary>是否显示菜单栏。</summary>
    public bool ShowMenuBar
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>侧边栏位置(如 left / right)。</summary>
    public string SidebarPosition
    {
        get;
        set => Set(ref field, value);
    } = "left";

    /// <summary>是否在左侧栏显示快捷命令面板;默认隐藏。</summary>
    public bool ShowQuickCommandsPanel
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>启动时的窗口状态(如 remember / maximized / normal)。</summary>
    public string StartupWindowState
    {
        get;
        set => Set(ref field, value);
    } = "remember";

    // “记住上次”窗口状态的持久化槽位(不出现在设置界面,由主窗口关闭时回写)。
    /// <summary>「记住上次」窗口宽度的持久化槽位(由主窗口关闭时回写)。</summary>
    public double LastWindowWidth
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>「记住上次」窗口高度的持久化槽位(由主窗口关闭时回写)。</summary>
    public double LastWindowHeight
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>「记住上次」窗口是否最大化的持久化槽位(由主窗口关闭时回写)。</summary>
    public bool LastWindowMaximized
    {
        get;
        set => Set(ref field, value);
    }

    // 终端颜色(默认 = Dracula 官方 Windows Terminal 方案)
    /// <summary>终端前景(文本)颜色(十六进制)。</summary>
    public string TerminalForeground
    {
        get;
        set => Set(ref field, value);
    } = "#F8F8F2";

    /// <summary>终端背景颜色(十六进制)。</summary>
    public string TerminalBackground
    {
        get;
        set => Set(ref field, value);
    } = "#282A36";

    /// <summary>终端光标颜色(十六进制)。</summary>
    public string CursorColor
    {
        get;
        set => Set(ref field, value);
    } = "#F8F8F2";

    /// <summary>终端选区高亮颜色(十六进制)。</summary>
    public string SelectionColor
    {
        get;
        set => Set(ref field, value);
    } = "#44475A";

    /// <summary>ANSI 标准色(0-7)调色板,共 8 个十六进制颜色。</summary>
    public List<string> AnsiNormal
    {
        get;
        set => Set(ref field, value);
    } = ["#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2"];

    /// <summary>ANSI 高亮色(8-15)调色板,共 8 个十六进制颜色。</summary>
    public List<string> AnsiBright
    {
        get;
        set => Set(ref field, value);
    } = ["#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"];
}

/// <summary>设置 - 终端(设计 08FpM;字体/字号/回滚沿用 AppSettings 顶层字段)。</summary>
public class TerminalBehaviorOptions : ObservableOptions
{
    /// <summary>行间距倍数;1.0 = 字体自然行高(与历史版本渲染一致)。</summary>
    public double LineHeight
    {
        get;
        set => Set(ref field, value);
    } = 1.0;

    /// <summary>
    /// 新连接的远程会话是否自动打开 SFTP 文件浏览器。只决定标签首次连接时面板的
    /// 初始状态,保存后即对之后的新连接生效;每个标签此后的开/关由用户在该标签上
    /// 的操作各自记忆,互不影响。面板不显示时不做任何 SFTP 拉取(文件列表、
    /// 属主/属组等),首次展开才加载。
    /// </summary>
    public bool AutoOpenFileBrowser
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    /// 本地回显:终端把键入的可见字符自己显示一份,不等对端回传。
    /// <para>
    /// 默认关闭。SSH 场景下远端 shell 自己会回显,开了会**每个字符显示两遍**;
    /// 它是给对端不回显的链路用的(Telnet 半双工、串口设备)。
    /// 主机若以 <c>CSI 12 l</c> 复位 SRM 显式要求终端回显,即便本项为关也会生效。
    /// </para>
    /// </summary>
    public bool LocalEcho
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>光标形状(如 bar / block / underline)。</summary>
    public string CursorStyle
    {
        get;
        set => Set(ref field, value);
    } = "bar";

    /// <summary>光标是否闪烁。</summary>
    public bool CursorBlink
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>终端响铃行为(唯一权威):system(系统提示音)/ none(不提示)/ visual(视觉闪烁)。</summary>
    public string BellMode
    {
        get;
        set => Set(ref field, value);
    } = "system";

    /// <summary>后台标签收到响铃时是否闪烁标签提示。</summary>
    public bool TabFlashAlert
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    /// 旧版“视觉闪烁”独立开关,仅作为迁移槽位保留(读旧配置用);
    /// 运行时不再消费,载入时经 <see cref="AppSettings.Normalize" /> 并入 <see cref="BellMode" />。
    /// </summary>
    public bool VisualBell { get; set; }

    /// <summary>开 = 新输出把翻看历史的视图拉回底部;关 = 保持锚定(#15 的既有行为)。</summary>
    public bool ScrollOnOutput
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>开 = 每行左侧显示 [HH:mm:ss] 收行时间(默认关,占用左侧宽度)。与 <see cref="ShowLineNumber" /> 独立。</summary>
    public bool ShowLineTimestamp
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>开 = 每行左侧显示缓冲区行号(默认关,占用左侧宽度)。与 <see cref="ShowLineTimestamp" /> 独立。</summary>
    public bool ShowLineNumber
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>开 = 侧栏显示折叠标记列,可折叠标记之前的历史内容(WindTerm 式,默认关)。</summary>
    public bool ShowFoldMarker
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>开 = 在侧栏与命令输出之间插入约 5px 的空白间隔(默认关)。</summary>
    public bool GutterBlank
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>按键输入时是否把视图滚回底部。</summary>
    public bool ScrollOnKeystroke
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    /// 选中即复制(默认开,设计 §8):开 = 松开鼠标/双击选词后选中内容自动进剪贴板;
    /// 关 = 选中只高亮不复制,复制需 Ctrl+Shift+C。
    /// </summary>
    public bool CopyOnSelect
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>是否右键点击即粘贴剪贴板内容。</summary>
    public bool RightClickPaste
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>复制时是否去除每行末尾空白。</summary>
    public bool TrimTrailingWhitespaceOnCopy
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>双击是否按单词选中。</summary>
    public bool DoubleClickSelectsWord
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>粘贴多行内容前是否弹出确认。</summary>
    public bool ConfirmMultilinePaste
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>是否启用输入法(IME)支持。</summary>
    public bool ImeSupport
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    /// 选中时 Ctrl+C 复制(默认关):开 = 有选区时 Ctrl+C 复制选中内容而不发送中断,
    /// 无选区仍发送中断;关 = Ctrl+C 始终发送中断信号 ^C。
    /// </summary>
    public bool CtrlCCopiesWhenSelected
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>
    /// 连接/重连成功后追加执行的用户初始化命令(空 = 无)。它会被拼接在内置的
    /// bash 提示符补行脚本之后,一并静默注入远端 shell(回显被抑制,不在终端显示)。
    /// </summary>
    public string StartupCommand
    {
        get;
        set => Set(ref field, value);
    } = "";
}

/// <summary>设置 - 文件传输(设计 HGwa7)。</summary>
public class TransferOptions : ObservableOptions
{
    /// <summary>下载文件保存到的本地默认目录。</summary>
    public string LocalDownloadDirectory
    {
        get;
        set => Set(ref field, value);
    } = "~/Downloads";

    /// <summary>同时进行的最大传输任务数。</summary>
    public int MaxConcurrentTransfers
    {
        get;
        set => Set(ref field, value);
    } = 3;

    /// <summary>传输时是否保留文件的原始时间戳。</summary>
    public bool PreserveTimestamps
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>传输完成时是否弹出通知。</summary>
    public bool NotifyOnComplete
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>ask / overwrite / skip / rename。</summary>
    public string ConflictPolicy
    {
        get;
        set => Set(ref field, value);
    } = "ask";

    /// <summary>显示隐藏文件:文件浏览器工具栏切换会写回本设置(设置审计 C-04)。</summary>
    public bool ShowHiddenFiles
    {
        get;
        set => Set(ref field, value);
    }

    // —— 文件浏览器的列显示(表头右键切换即写回,与工具栏开关同构,设置审计 C-04)。
    //    “文件名”列不在其列:它是行的标识,关掉就只剩一排没有主语的元数据。

    /// <summary>文件浏览器是否显示“大小”列。</summary>
    public bool ShowSizeColumn
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>文件浏览器是否显示“权限”列。</summary>
    public bool ShowPermissionsColumn
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>文件浏览器是否显示“所有者”列。</summary>
    public bool ShowOwnerColumn
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>文件浏览器是否显示“用户组”列。</summary>
    public bool ShowGroupColumn
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>文件浏览器是否显示“类型”列。</summary>
    public bool ShowTypeColumn
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>文件浏览器是否显示“修改时间”列。</summary>
    public bool ShowModifiedColumn
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>遗留字段(设置审计 M-02):早期与 <see cref="ResumeEnabled" /> 并存的重复开关,仅保留持久化兼容,运行时不消费。</summary>
    public bool AutoResume { get; set; } = true;

    /// <summary>是否启用传输带宽限速。</summary>
    public bool BandwidthLimitEnabled
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>上传速度上限(MB/s),0 = 不限。</summary>
    public int UploadLimitMBps
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>下载速度上限(MB/s),0 = 不限。</summary>
    public int DownloadLimitMBps
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>是否记录文件传输日志。</summary>
    public bool TransferLogging
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>文件传输日志保留天数(区别于终端会话日志的保留天数,设置审计 N-08/N-09)。</summary>
    public int TransferLogRetentionDays
    {
        get;
        set => Set(ref field, value);
    } = 30;

    /// <summary>传输日志的存放目录。</summary>
    public string LogDirectory
    {
        get;
        set => Set(ref field, value);
    } = "~/.velashell/logs";

    /// <summary>
    /// 断点续传(设置 → 文件传输 → 恢复中断的传输):检测到部分传输文件时自动从上次位置续传。
    /// 开启时失败/取消留下的半截文件<b>有意保留</b>(它是续传素材);起点核实与安全回退见
    /// SftpService.ResolveUploadResumeAsync / ResolveDownloadResumeAsync。
    /// </summary>
    public bool ResumeEnabled
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>规划中(传输失败重试):仅持久化,当前无运行时消费者,不出现在设置界面(设置审计 R-09)。</summary>
    public int TransferMaxRetries { get; set; } = 3;

    /// <summary>
    /// 临时文件清理(设置 → 文件传输):失败/取消时删除半截目标文件(上传删远端、下载删本地)。
    /// 仅在 <see cref="ResumeEnabled" /> 关闭时生效 —— 续传开启时半截文件是续传素材,不清理。
    /// </summary>
    public bool AutoCleanTempFiles
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>
    /// SFTP「使用默认编辑器打开」调用的程序(命令名或完整路径,如 notepad、
    /// notepad++、"C:\Program Files\Notepad++\notepad++.exe")。空 = 未配置。
    /// </summary>
    public string DefaultEditorPath
    {
        get;
        set => Set(ref field, value);
    } = "";
}

/// <summary>设置 - 安全审计(设计 glqQE;策略项持久化,审计数据在 SonnetDB audit_log)。</summary>
public class SecurityOptions : ObservableOptions
{
    /// <summary>规划中(会话录制):仅持久化,当前无运行时消费者,不出现在设置界面(设置审计 R-11)。</summary>
    public bool RecordProductionSessions { get; set; } = true;

    /// <summary>规划中(输入脱敏,依赖会话录制):仅持久化,不出现在设置界面(设置审计 R-12)。</summary>
    public bool MaskSensitiveInput { get; set; } = true;

    /// <summary>首次连接主机时是否需确认其指纹。</summary>
    public bool ConfirmFirstFingerprint
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>主机指纹变化时是否阻止连接。</summary>
    public bool BlockOnFingerprintChange
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>安全事件发生时是否在应用内弹出告警。</summary>
    public bool AlertInApp
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>安全事件发生时播放系统提示音(作用域是主机指纹等安全事件,设置审计 N-02)。</summary>
    public bool AlertSound
    {
        get;
        set => Set(ref field, value);
    } = true;

    /// <summary>安全事件发生时是否推送到 Webhook。</summary>
    public bool AlertWebhook
    {
        get;
        set => Set(ref field, value);
    }

    /// <summary>接收安全告警的 Webhook 地址(空 = 未配置)。</summary>
    public string WebhookUrl
    {
        get;
        set => Set(ref field, value);
    } = "";
}

/// <summary>设置 - 密钥管理(设计 UBP59)。</summary>
public class KeyOptions : ObservableOptions
{
    /// <summary>
    /// 新建连接时默认使用的密钥名称(~/.ssh 下的文件名)。null 归一为空串:
    /// “默认认证密钥”下拉在列表未就位/密钥被删除时会把 SelectedItem=null 写回来,
    /// 不允许它破坏模型的非空不变量。
    /// </summary>
    public string DefaultKeyName
    {
        get;
        set => Set(ref field, value ?? "");
    } = "";

    /// <summary>规划中(ssh-agent 集成):仅持久化,当前无运行时消费者,不出现在设置界面(设置审计 R-06)。</summary>
    public bool AutoLoadToAgent { get; set; } = true;
}
