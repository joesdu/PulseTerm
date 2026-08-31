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

    /// <summary>
    /// 开发期插件的影子副本根目录。非空时,来自 <see cref="DevPluginRoots" /> 的插件
    /// 在装载前被整份复制到 <c>&lt;root&gt;/&lt;pluginId&gt;/</c> 再从副本加载。
    /// <para>
    /// 为的是 Windows 上的文件锁:ALC 用 <c>LoadFromAssemblyPath</c> 装载,插件活着时
    /// 入口 dll 的句柄不放,于是内环退化成"关掉宿主 → 重编 → 再启动"。从副本装载后,
    /// 工程的 <c>bin</c> 随时可以重编,改完点一下"重新加载"即可(见 <see cref="PluginManager.ReloadAsync" />)。
    /// </para>
    /// <para>缺省(<see langword="null" />)时就地装载,行为与影子拷贝引入前一致。</para>
    /// </summary>
    public string? DevShadowRootDirectory { get; init; }

    /// <summary>
    /// 开发期插件的禁用登记文件(每行一个插件 id)。已安装插件的禁用标记写在插件目录里,
    /// 但开发期插件的目录是构建产物目录,写进去只会让人困惑(重编后标记还在)。
    /// 缺省时开发期插件的禁用状态不持久(仅本次运行有效)。
    /// </summary>
    public string? DevDisabledStateFile { get; init; }

    /// <summary>
    /// 是否监视开发期插件根,检测到构建产物变化后自动重载(启动参数 <c>--dev-watch</c>)。
    /// 默认关:文件监视器在共享盘/网络盘上会抖,不该是所有人默认承担的成本。
    /// </summary>
    public bool DevAutoReload { get; init; }

    /// <summary>
    /// 诊断文件目录(等待调试器的隔离插件在此落一个 <c>plugin-host-&lt;id&gt;.pid</c>)。
    /// 缺省时不落文件,pid 仍会打进日志。
    /// </summary>
    public string? DiagnosticsDirectory { get; init; }

    /// <summary>插件私有数据根目录:每插件一个 <c>&lt;root&gt;/&lt;pluginId&gt;/</c> 子目录。</summary>
    public required string DataRootDirectory { get; init; }

    /// <summary>
    /// 用户可写的插件安装根目录(<c>.vpx</c> 安装落此处;仅此目录下的插件可卸载)。
    /// 应用自带插件(<c>&lt;应用目录&gt;/plugins</c>)只读,不可卸载。缺省时无安装/卸载能力。
    /// </summary>
    public string? UserPluginRoot { get; init; }

    /// <summary>
    /// 受信的包签名公钥(Base64 SPKI)。为空表示没有可信发布者;有效自签名包仍属于不受信来源,
    /// 必须由安装入口取得用户的单次明确授权。
    /// </summary>
    public IReadOnlyCollection<string>? TrustedPackageKeys { get; init; }

    /// <summary>受 AES-GCM 完整性保护的发布者信任与安装凭据仓储。</summary>
    public PluginTrustRepository? TrustRepository { get; init; }

    /// <summary>
    /// 是否只安装带受信签名的包。打开后未签名与不受信签名的包都会被拒,不能通过单次授权绕过。
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
    /// 系统是否偏好暗色(由 UI 层提供:Avalonia 的 <c>ActualThemeVariant</c>)。
    /// 只在用户选了“跟随系统”时用得到 —— 那时 <see cref="IThemeService.CurrentTheme" />
    /// 是字面量 <c>system</c>,光看它不知道此刻该报哪一套主题给插件。
    /// 缺席时按暗色兜底(与 <c>UiThemeCatalog.Resolve</c> 对未知值的兜底一致)。
    /// </summary>
    public SystemDarkModeProbe? SystemPrefersDark { get; init; }

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
    /// 终端视图能力(由 UI 层提供:出借宿主的终端仿真器控件);缺席时报不可用。
    /// <para>
    /// 与 <see cref="TerminalFactory" /> 不同,这一项**不按插件分实例** ——
    /// 它不持有任何每插件状态,每次 <c>Create</c> 都新建一个独立的控件,
    /// 生命周期归调用它的插件自己管(<c>Dispose</c> 即销毁)。
    /// </para>
    /// </summary>
    public PluginSdk.TerminalView.ITerminalViewApi? TerminalView { get; init; }

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

    /// <summary>
    /// 后台活动账本(状态栏右下角的圆环据此显示)。缺席时插件加载静默进行,
    /// 行为与引入指示器之前完全一致 —— headless 测试与无界面宿主不受影响。
    /// </summary>
    public IBackgroundActivityService? Activity { get; init; }

    /// <summary>
    /// 是否对惰性等待中的插件做冷启动预读(把程序集抬进操作系统文件缓存)。
    /// <para>
    /// **只读文件,不装载程序集、不跑 <c>ActivateAsync</c>** —— 惰性激活的语义分毫不动,
    /// 省下的是用户点击那一刻的磁盘时间。默认开;
    /// 环境变量 <c>VELASHELL_DISABLE_PLUGIN_PREWARM=1</c> 为排障急停开关。
    /// </para>
    /// </summary>
    public bool PrewarmLazyPlugins { get; init; } = true;

    /// <summary>预读的起始延时:让主窗口先把首帧画完,预读绝不与启动争磁盘。</summary>
    public TimeSpan PrewarmDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>预读的总字节上限:超过即停,避免为一个巨型插件把文件缓存整个冲掉。</summary>
    public long PrewarmByteBudget { get; init; } = 128L * 1024 * 1024;

    /// <summary>单插件停用时限;应用退出路径上超时即放弃等待。</summary>
    public TimeSpan DeactivationTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
