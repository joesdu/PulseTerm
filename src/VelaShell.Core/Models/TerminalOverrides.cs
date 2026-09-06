namespace VelaShell.Core.Models;

/// <summary>
/// 一条连接配置对全局终端设置的覆盖项;每个字段 null = 跟随全局。
/// </summary>
/// <remarks>
/// <para>
/// 全局设置只能有一套,但机器不是一样的:堡垒机的输出是 GBK、开发机是 UTF-8;生产环境希望
/// 标签一眼看出是红的、测试环境是绿的;某台老设备只认 <c>vt220</c>。这些差异按机器走,
/// 挤进一个全局开关里就只能二选一。
/// </para>
/// <para>
/// <b>每个字段都是可空的,null 表示"这一项不覆盖"</b> —— 而不是用哨兵值(空串、0、
/// "跟随全局"这样的伪枚举项)。哨兵值分不清"用户明确选了空"和"用户没选",
/// 而这两件事在设置迁移与界面回填时行为不同。
/// </para>
/// </remarks>
public sealed class TerminalOverrides
{
    /// <summary>
    /// 解码对端输出用的编码(取值来自 <see cref="TerminalEncodings.All" />);null = 跟随全局。
    /// </summary>
    /// <remarks>
    /// 覆盖需求最强的一项:同一个人同时连 UTF-8 的容器和 GBK 的老服务器,全局只能配一个,
    /// 另一边就是满屏乱码。
    /// </remarks>
    public string? Encoding { get; set; }

    /// <summary>向对端宣告的 <c>TERM</c>(如 <c>xterm-256color</c>);null = 跟随全局。</summary>
    public string? TerminalType { get; set; }

    /// <summary>
    /// 终端配色方案名(须存在于 <see cref="TerminalColorScheme.BuiltIn" />);null = 跟随全局。
    /// </summary>
    public string? ColorScheme { get; set; }

    /// <summary>
    /// 标签页强调色(<c>#RRGGBB</c>);null = 按配置 id 自动配色。
    /// </summary>
    /// <remarks>
    /// 「生产标红、测试标绿」是运维最常提的诉求。此前这个颜色是按 profileId 哈希在 8 色里
    /// 取一个,用户完全选不了 —— 而自动配色恰恰保证了同一批机器颜色各不相同,
    /// 正好与"按环境分色"的意图相反。
    /// </remarks>
    public string? TabColor { get; set; }

    /// <summary>登录后自动切换到的目录;null 或空 = 不切换。</summary>
    public string? StartupDirectory { get; set; }

    /// <summary>保活心跳间隔(秒,0 = 关闭);null = 跟随全局。</summary>
    /// <remarks>
    /// 上限与全局设置同为 3600:配置文件可以手改,一个 <c>99999</c> 等于悄悄关掉了保活。
    /// </remarks>
    public int? KeepAliveSeconds
    {
        get;
        set => field = value is null ? null : Math.Clamp(value.Value, 0, MaxKeepAliveSeconds);
    }

    /// <summary>保活心跳间隔的上限秒数;与 <c>AppSettings.General.KeepAliveSeconds</c> 同一口径。</summary>
    public const int MaxKeepAliveSeconds = 3600;

    /// <summary>是否一项都没覆盖(等价于整个对象为 null)。</summary>
    /// <remarks>
    /// 界面把六项都清空之后应当存回 <c>null</c> 而不是一个全空对象:后者会让每条老配置
    /// 的落盘 JSON 平白多出一段,也让"有没有覆盖"这件事有了两种表示。
    /// </remarks>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Encoding)
        && string.IsNullOrWhiteSpace(TerminalType)
        && string.IsNullOrWhiteSpace(ColorScheme)
        && string.IsNullOrWhiteSpace(TabColor)
        && string.IsNullOrWhiteSpace(StartupDirectory)
        && KeepAliveSeconds is null;

    /// <summary>返回本对象的副本。</summary>
    /// <returns>与本实例等值的新实例。</returns>
    public TerminalOverrides Clone() =>
        new()
        {
            Encoding = Encoding,
            TerminalType = TerminalType,
            ColorScheme = ColorScheme,
            TabColor = TabColor,
            StartupDirectory = StartupDirectory,
            KeepAliveSeconds = KeepAliveSeconds
        };
}
