namespace VelaShell.Core.Models;

/// <summary>一条已保存的 SSH 连接配置,描述连接目标主机所需的地址、认证方式与凭据等信息。</summary>
public class SessionProfile
{
    /// <summary>
    /// 连接协议类型;缺失或未知值均按 SSH 处理。
    /// <para>
    /// 用 <see cref="Enum.IsDefined{TEnum}" /> 做白名单而非逐值三元:语义仍是「不认识的一律降级为 SSH」
    /// (旧数据兼容策略不变),但新增协议时不必再回来改这里 —— 之前正是这处三元把扩展口焊死了。
    /// </para>
    /// </summary>
    public ConnectionType ConnectionType
    {
        get;
        set => field = Enum.IsDefined(value) ? value : ConnectionType.SSH;
    } = ConnectionType.SSH;

    /// <summary>配置的全局唯一标识,创建时自动生成。</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>配置的显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>目标主机地址(主机名或 IP)。</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>SSH 端口,默认 22。</summary>
    public int Port { get; set; } = 22;

    /// <summary>登录用户名。</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>认证方式(密码 / 私钥等),默认使用密码认证。</summary>
    public AuthMethod AuthMethod { get; set; } = AuthMethod.Password;

    /// <summary>登录密码;仅在密码认证时使用,可为空。</summary>
    public string? Password { get; set; }

    /// <summary>是否记住密码(AES-256 加密落盘);为 false 时密码仅用于本次连接,不持久化。</summary>
    public bool RememberPassword { get; set; } = true;

    /// <summary>私钥文件路径;仅在私钥认证时使用,可为空。</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>私钥的解锁口令;私钥未加密时可为空。</summary>
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>所属分组的标识;未分组时为 null。</summary>
    public Guid? GroupId { get; set; }

    /// <summary>最近一次成功连接的时间;从未连接时为 null。</summary>
    public DateTime? LastConnectedAt { get; set; }

    /// <summary>用于分类与检索的标签集合。</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// 跳板主机(ProxyJump,§12 P1-2):引用另一条已保存配置作为堡垒机;
    /// 跳板配置自身还可以再配跳板,链式即多段跳。null = 直连。
    /// </summary>
    public Guid? JumpHostProfileId { get; set; }

    /// <summary>
    /// 本条配置专属的「认证后执行命令」:认证通过、shell 通道建好之后静默注入一次。
    /// <para>
    /// 与设置 → 终端 → 会话里那条「连接后执行命令」是两件事:那条对**所有**终端生效,
    /// 这条只对这一条配置生效 —— 每台机器登进去要做的事本来就不一样(堡垒机要 <c>sudo su -</c>,
    /// 开发机要 <c>tmux attach</c>),挤进同一个全局框里只能二选一。
    /// </para>
    /// <para>
    /// 空 / null = 不执行。两处都配了时先全局后本条,顺序即用户在两个界面上看到的顺序。
    /// </para>
    /// </summary>
    public string? PostAuthCommand { get; set; }

    /// <summary>
    /// 注入 <see cref="PostAuthCommand" /> 之前的等待秒数(0~<see cref="MaxPostAuthCommandDelaySeconds" />,默认 1)。
    /// <para>
    /// PTY 输入由内核缓冲,本不必等提示符;需要这个延迟是因为**对端登录后还会自己往终端里写东西**
    /// (motd 脚本、企业登录横幅、把 stdin 一起读掉的 banner)。立刻注入会被这些输出盖住甚至吞掉,
    /// 留一两秒才稳。0 = 不等,握手完立刻发。
    /// </para>
    /// <para>
    /// 钳位放在 setter 而不是只靠界面:配置文件是可以手改的,一个 <c>99999</c> 会让那条命令
    /// 看起来永远不执行,而用户完全无从知道自己在等什么。
    /// </para>
    /// </summary>
    public int PostAuthCommandDelaySeconds
    {
        get;
        set => field = Math.Clamp(value, 0, MaxPostAuthCommandDelaySeconds);
    } = 1;

    /// <summary>「认证后执行命令」延迟的上限秒数;界面与反序列化共用同一个钳位。</summary>
    public const int MaxPostAuthCommandDelaySeconds = 60;

    /// <summary>
    /// FTP / FTPS 的协议专属设置;仅在 <see cref="ConnectionType" /> 为
    /// <see cref="ConnectionType.FTP" /> 时有意义,其余协议为 null。
    /// </summary>
    public FtpSettings? Ftp { get; set; }

    /// <summary>
    /// 插件协议 id(如 <c>velashell.s3</c>);仅在 <see cref="ConnectionType" /> 为
    /// <see cref="ConnectionType.Plugin" /> 时有意义,其余协议为 null。
    /// <para>
    /// 它是这条配置与某个插件之间唯一的绑定。插件未安装/未启用时配置仍然完好保存,
    /// 只是连不上并给出「协议不可用」的提示 —— 卸载一个插件绝不该毁掉用户的连接配置。
    /// </para>
    /// </summary>
    public string? PluginProtocolId { get; set; }

    /// <summary>
    /// 插件协议的非机密设置:键为插件在 <c>ProtocolSettingField.Key</c> 里声明的字段键。
    /// <para>
    /// 刻意用字符串字典而不是给每个插件在 Core 里开一个强类型模型:Core 不认识任何具体协议,
    /// 这正是把 S3 之类的协议移出宿主的前提。
    /// </para>
    /// </summary>
    public Dictionary<string, string>? PluginSettings { get; set; }

    /// <summary>
    /// 插件协议的机密设置(声明为 <c>IsSecret</c> 的字段),整体加密落盘。
    /// <para>
    /// 与 <see cref="PluginSettings" /> 分成两个字典,而不是在一个字典里按字段标记区分:
    /// 仓储层在落盘那一刻并不知道某个协议的字段声明(那在插件里),
    /// 分开存才能做到「机密永远加密」这条不依赖任何查表的硬保证。
    /// </para>
    /// <para>
    /// 主凭据本身仍走通用字段(<see cref="Username" /> / <see cref="Password" />),
    /// 因此「记住密码」的加密落盘、登录弹窗、导入器的凭据还原对插件协议全部零改动。
    /// </para>
    /// </summary>
    public Dictionary<string, string>? PluginSecrets { get; set; }

    /// <summary>
    /// 会话级的终端覆盖项:编码、终端类型、配色、标签颜色、初始目录、心跳间隔。
    /// null(整个对象,或其中任一字段)= 跟随全局设置。
    /// </summary>
    /// <remarks>
    /// 单开一个对象而不是把六个字段平铺进来,是为了让「这条配置没有任何覆盖」在落盘时
    /// 就是一个 <c>null</c> —— 旧数据零迁移,新字段也不会给每条老配置塞六个空值。
    /// </remarks>
    public TerminalOverrides? Terminal { get; set; }

    /// <summary>
    /// 连接建立后自动开启的隧道 id 列表;null 或空 = 不自动开。
    /// </summary>
    /// <remarks>
    /// 存 id 而不是隧道定义本身:同一条隧道可能被多条配置引用,复制一份就会各改各的。
    /// </remarks>
    public List<Guid>? AutoStartTunnelIds { get; set; }

    /// <summary>返回协议专属设置的深拷贝(两个字典各一份);源为 null 时返回 null。</summary>
    /// <param name="source">源字典。</param>
    /// <returns>深拷贝,或 null。</returns>
    public static Dictionary<string, string>? CloneSettings(Dictionary<string, string>? source) =>
        source is null ? null : new Dictionary<string, string>(source, StringComparer.Ordinal);

    /// <summary>
    /// 返回本配置的深拷贝。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>加字段只改这一处。</b>此前全仓有四处逐字段手写拷贝(仓储的加密/解密两处、
    /// 会话树的「复制配置」、连接工作流剥密码那一处),每加一个字段就要四处同步补 —— 漏一处的
    /// 表现是"某个设置在复制配置之后莫名丢了",而且四处的症状各不相同,极难联想到同一个根因。
    /// </para>
    /// <para>
    /// 字典与列表一律深拷贝:副本要么落盘、要么交给别的界面改,与源共享可变对象迟早互相踩。
    /// <c>SessionProfileCloneTests</c> 用反射逐属性比对,漏拷一个就红。
    /// </para>
    /// </remarks>
    /// <returns>与本实例等值、但不共享任何可变对象的新实例。</returns>
    public SessionProfile Clone() =>
        new()
        {
            ConnectionType = ConnectionType,
            Id = Id,
            Name = Name,
            Host = Host,
            Port = Port,
            Username = Username,
            AuthMethod = AuthMethod,
            Password = Password,
            RememberPassword = RememberPassword,
            PrivateKeyPath = PrivateKeyPath,
            PrivateKeyPassphrase = PrivateKeyPassphrase,
            GroupId = GroupId,
            LastConnectedAt = LastConnectedAt,
            Tags = [.. Tags],
            JumpHostProfileId = JumpHostProfileId,
            PostAuthCommand = PostAuthCommand,
            PostAuthCommandDelaySeconds = PostAuthCommandDelaySeconds,
            Ftp = Ftp?.Clone(),
            PluginProtocolId = PluginProtocolId,
            PluginSettings = CloneSettings(PluginSettings),
            PluginSecrets = CloneSettings(PluginSecrets),
            Terminal = Terminal?.Clone(),
            AutoStartTunnelIds = AutoStartTunnelIds is null ? null : [.. AutoStartTunnelIds]
        };
}
