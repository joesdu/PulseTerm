namespace VelaShell.Core.Models;

/// <summary>消息的种类,决定列表里的图标与配色。</summary>
public enum NotificationKind
{
    /// <summary>产品公告、版本说明、订阅到的资讯。</summary>
    News,

    /// <summary>有可用的应用更新。</summary>
    Update,

    /// <summary>安全资讯(上游组件的漏洞公告一类的**新闻**)。</summary>
    /// <remarks>
    /// 与本机的安全事件(主机指纹变更等)不是一回事:那些走
    /// <see cref="Ssh.ISecurityAlertService" /> 当场弹窗,要的是立即打断,
    /// 而不是躺在列表里等人来看。
    /// </remarks>
    Security,

    /// <summary>运营/推广类消息。</summary>
    Promotion,

    /// <summary>
    /// 应用自身的诊断消息(上次异常退出、日志异常一类)。
    /// </summary>
    /// <remarks>
    /// 走消息中心而不是启动弹窗:崩溃提示要的是"下次打开时能看到并取证",
    /// 不是在用户想干活的那一刻挡在前面。
    /// </remarks>
    System
}

/// <summary>消息的轻重,决定列表里的强调程度。</summary>
public enum NotificationSeverity
{
    /// <summary>一般信息。</summary>
    Info,

    /// <summary>需要留意。</summary>
    Warning,

    /// <summary>需要尽快处理。</summary>
    Critical
}

/// <summary>
/// 消息中心里的一条消息(侧边栏铃铛)。
/// <para>
/// 这里装的是**要留存、可回看**的东西 —— 有新版本了、订阅源发了篇公告。
/// 转瞬即逝的运行时状态(某个标签断了、某次传输完了)不进这里:它们各有自己的
/// 当场反馈(状态栏、标签闪烁、传输面板),塞进消息中心只会把真正要读的东西淹掉。
/// </para>
/// </summary>
public sealed class NotificationItem
{
    /// <summary>
    /// 稳定标识。远端资讯用源里给的 id,本地生成的用确定性字符串
    /// (如 <c>update:1.4.0</c>)—— 每次启动都重新投递同一条时,靠它去重。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>消息种类。</summary>
    public required NotificationKind Kind { get; init; }

    /// <summary>消息轻重。</summary>
    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Info;

    /// <summary>标题(一行,列表里加粗显示)。</summary>
    public required string Title { get; init; }

    /// <summary>正文(可选,列表里最多显示两行)。</summary>
    public string? Body { get; init; }

    /// <summary>消息发布时间(UTC)。列表按它倒序,不是按收到的时间。</summary>
    public required DateTime PublishedAt { get; init; }

    /// <summary>过期时间(UTC);到点后不再展示。null 表示不过期。</summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>是否已读。</summary>
    public bool IsRead { get; set; }

    /// <summary>点击这条消息去哪;null 表示没有可去的地方(纯告知)。</summary>
    public NotificationLink? Link { get; init; }
}

/// <summary>
/// 一条消息的去处。二选一:应用内跳到某条已注册命令,或在浏览器里打开一个网址。
/// 两个都给时优先走应用内 —— 站内能办的事不该把人赶去浏览器。
/// </summary>
public sealed class NotificationLink
{
    /// <summary>动作文案(如「查看更新」「阅读全文」)。</summary>
    public required string Label { get; init; }

    /// <summary>
    /// 应用内命令 id(Presentation 层那套命令注册表里的)。走注册表而不是各处硬编码:
    /// 菜单、命令面板、快捷键本就共享它,消息中心跟着用,跳转目标就不会与其它入口跑偏。
    /// </summary>
    public string? CommandId { get; init; }

    /// <summary>
    /// 外部网址。**只接受 https** —— 这个字段的内容来自远端源,
    /// 放行 http 等于允许投递方把用户导去一条可被中间人改写的链路。
    /// </summary>
    public string? Url { get; init; }
}
