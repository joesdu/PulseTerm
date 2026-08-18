using VelaShell.Core.Localization;
using VelaShell.Core.Services;
using VelaShell.Core.Sftp;
using VelaShell.Core.Ssh;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Rpc;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// <see cref="PluginManager" /> 的装配选项。可选服务缺席时对应能力退化
/// (会话列表为空、远程能力抛"能力不可用"),插件不因此崩溃 —— 便于 headless 测试。
/// </summary>
public sealed class PluginManagerOptions
{
    /// <summary>插件发现根目录(按序;同 id 先到先得)。不存在的目录自动跳过。</summary>
    public required IReadOnlyList<string> PluginRoots { get; init; }

    /// <summary>
    /// 开发期插件根目录:与 <see cref="PluginRoots" /> 同样被扫描(排在其后),但发现出的插件
    /// 会被标记为 <see cref="PluginDescriptor.IsDevelopment" />,在管理页显示 DEV 角标。
    /// 用途是把插件工程的 <c>bin/Debug/net11.0</c> 直接挂进宿主 —— 改完 <c>dotnet build</c>
    /// 重启即生效,不必打包、不必安装。来源见 <see cref="DevPluginRootResolver" />。
    /// </summary>
    public IReadOnlyList<string> DevPluginRoots { get; init; } = [];

    /// <summary>插件私有数据根目录:每插件一个 <c>&lt;root&gt;/&lt;pluginId&gt;/</c> 子目录。</summary>
    public required string DataRootDirectory { get; init; }

    /// <summary>
    /// 用户可写的插件安装根目录(<c>.vpx</c> 安装落此处;仅此目录下的插件可卸载)。
    /// 应用自带插件(<c>&lt;应用目录&gt;/plugins</c>)只读,不可卸载。缺省时无安装/卸载能力。
    /// </summary>
    public string? UserPluginRoot { get; init; }

    /// <summary>
    /// 受信的包签名公钥(Base64 SPKI)。为空表示不做来源判定 —— 此时任何**有效**签名都算受信,
    /// 但签名对不上的包依然被拒(那是篡改,不是来源问题)。
    /// </summary>
    public IReadOnlyCollection<string>? TrustedPackageKeys { get; init; }

    /// <summary>
    /// 是否只安装带受信签名的包(默认否:第一方/自装插件场景信任即安装,见蓝图 10 的分期决策)。
    /// 打开后未签名与不受信签名的包都会被拒。
    /// </summary>
    public bool RequireTrustedPackageSignature { get; init; }

    /// <summary>
    /// 需要等待调试器的插件 id 集合(<c>"*"</c> 表示全部)。命中的隔离插件:
    /// 子进程在装载插件程序集**之前**挂起等调试器附加,同时宿主放宽激活超时并停掉心跳 ——
    /// 否则你停在断点上的那几分钟会被判成"插件挂死"而遭强杀。
    /// 由环境变量 <c>VELA_PLUGIN_WAIT_DEBUGGER</c> 填充,默认空(生产路径分文不动)。
    /// </summary>
    public IReadOnlyCollection<string> DebugPluginIds { get; init; } = [];

    /// <summary>宿主版本(用于 minHostVersion 兼容检查与 <see cref="IHostInfo.AppVersion" />)。</summary>
    public string HostVersion { get; init; } = "0.0.0";

    /// <summary>SSH 连接服务(会话/远程执行能力与会话事件的来源)。</summary>
    public ISshConnectionService? Connections { get; init; }

    /// <summary>SFTP 服务(远程文件能力的来源)。</summary>
    public ISftpService? Sftp { get; init; }

    /// <summary>主题服务(宿主信息与主题事件)。</summary>
    public IThemeService? Theme { get; init; }

    /// <summary>本地化服务(宿主信息与语言事件)。</summary>
    public ILocalizationService? Localization { get; init; }

    /// <summary>
    /// 每插件命令能力工厂(由 UI 层提供,桥到命令注册表);实例若实现
    /// <see cref="IDisposable" />,停用插件时被释放(注销其全部命令)。
    /// </summary>
    public Func<string, IPluginLogger, ICommandsApi>? CommandsFactory { get; init; }

    /// <summary>
    /// 每插件界面能力工厂(由 UI 层提供,呈现插件自建的 Avalonia 控件);实例若实现
    /// <see cref="IDisposable" />,停用插件时被释放(关闭其全部面板)。
    /// </summary>
    public Func<string, IPluginLogger, IUiApi>? UiFactory { get; init; }

    /// <summary>
    /// 主题令牌快照提供者(由 UI 层提供,按当前明暗变体解析 <c>Vela*</c> 资源):
    /// 隔离插件握手后与每次主题切换时下发,使 <c>{DynamicResource VelaXxx}</c>
    /// 跨进程同样生效。缺席时隔离插件只拿到明暗变体跟随。
    /// </summary>
    public Func<Task<IReadOnlyList<ThemeTokenDto>>>? ThemeTokensProvider { get; init; }

    /// <summary>
    /// 插件数据后端(SonnetDB):KV 与机密按插件 id 命名空间化落库,卸载可整体清除。
    /// 缺席时退回插件数据目录下的 JSON/加密 JSON 文件(headless 测试路径)。
    /// </summary>
    public IPluginDataStore? DataStore { get; init; }

    /// <summary>机密保护器(<see cref="DataStore" /> 缺席时文件路径机密能力的加密后端);缺席时机密能力报不可用,绝不明文兜底。</summary>
    public Core.Data.ISecretProtector? SecretProtector { get; init; }

    /// <summary>剪贴板能力实现(由 UI 层提供);缺席时报不可用。</summary>
    public PluginSdk.Clipboard.IClipboardApi? Clipboard { get; init; }

    /// <summary>停靠嵌入宿主(由 UI 层提供,仅 Windows);缺席时隔离插件的停靠请求回退为独立窗口。</summary>
    public Isolated.IPluginEmbedHost? EmbedHost { get; init; }

    /// <summary>每插件终端能力工厂(由 UI 层提供:缓冲读取 + 授权回写);缺席时报不可用。</summary>
    public Func<string, IPluginLogger, PluginSdk.Terminal.ITerminalApi>? TerminalFactory { get; init; }

    /// <summary>
    /// 插件协议注册表:清单声明的协议页签在发现期登记于此,插件激活后再补上实现。
    /// 缺席时协议能力报不可用(headless 测试路径),声明的页签也不会出现。
    /// </summary>
    public Protocols.PluginProtocolRegistry? ProtocolRegistry { get; init; }

    /// <summary>
    /// 隔离插件崩溃后的重启退避序列(第 N 次崩溃等待第 N 项);
    /// <see cref="CrashRestartWindow" /> 内崩溃次数超过序列长度即判 Failed 不再重启。
    /// </summary>
    public IReadOnlyList<TimeSpan> CrashRestartBackoff { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    /// <summary>崩溃计数的滑动窗口(窗口外的历史崩溃不计入退避上限)。</summary>
    public TimeSpan CrashRestartWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>隔离插件心跳间隔;连续两次 ping 失败判挂死并强杀重启。零或负值关闭心跳。</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 空闲回收阈值(蓝图 04,仅隔离模式 + <c>idlePolicy: "recyclable"</c>):
    /// 连续无 RPC 往来且无打开面板达到该时长即停用回收进程,占位命令留守待再触发。
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>空闲巡检间隔。</summary>
    public TimeSpan IdleCheckInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>单插件激活时限;超时判 Failed 并卸载。</summary>
    public TimeSpan ActivationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>单插件停用时限;应用退出路径上超时即放弃等待。</summary>
    public TimeSpan DeactivationTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
