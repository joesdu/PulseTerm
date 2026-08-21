namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 集中解析并暴露 VelaShell 各项持久化文件与目录的绝对路径(以 ~/.velashell 为根)。
/// </summary>
public sealed class VelaShellStoragePaths
{
    /// <summary>
    /// 数据根目录的进程级覆盖(启动参数 <c>--data-root</c>)。**必须在 <c>Main</c> 里、
    /// 任何一次路径解析之前赋值**,之后再改不会影响已构造出来的实例。
    /// <para>
    /// 存在的理由是插件开发内环:开发者日常开着一个 VelaShell,调试实例若共用同一个数据根,
    /// 会先撞上单实例互斥体("已在运行"然后干净退出),就算绕过去也会撞 SonnetDB 的 WAL 独占锁。
    /// 换一个数据根,两个实例就能并存,调试用的连接与设置也不会污染日常配置。
    /// </para>
    /// </summary>
    public static string? RootDirectoryOverride { get; set; }

    /// <summary>
    /// 构造所有存储路径。根目录取 <paramref name="rootDirectory" />、
    /// <see cref="RootDirectoryOverride" />、<c>~/.velashell</c>(依次回退)。
    /// </summary>
    /// <param name="rootDirectory">显式指定的数据根;<see langword="null" /> 时按上面的顺序回退。</param>
    public VelaShellStoragePaths(string? rootDirectory = null)
    {
        string root = ResolveRoot(rootDirectory ?? RootDirectoryOverride);
        RootDirectory = root;
        HostRegistryFile = Path.Combine(root, PluginSdk.Hosting.HostRegistry.FileName);
        DevPluginListFile = Path.Combine(root, "plugins.dev.txt");
        DevPluginDisabledFile = Path.Combine(root, "plugins.dev.disabled");
        DevPluginShadowDirectory = Path.Combine(root, "dev-shadow");
        LogsDirectory = Path.Combine(root, "logs");
        SettingsFile = Path.Combine(root, "settings.json");
        StateFile = Path.Combine(root, "state.json");
        SessionsFile = Path.Combine(root, "sessions.json");
        SonnetDbDirectory = Path.Combine(root, "sonnetdb");
        SecretKeyFile = Path.Combine(root, "secret.key");
        RenderModeFile = Path.Combine(root, "render.mode");
        GeoIpDirectory = Path.Combine(root, "geoip");
        UserPluginDirectory = Path.Combine(root, "plugins");
        LegacyLocalAppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VelaShell"
        );
        LegacyQuickCommandsFile = Path.Combine(root, "quick-commands.json");
    }

    /// <summary>VelaShell 所有持久化数据的根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>
    /// 宿主自我登记文件(<c>host.json</c>):可执行文件路径、版本、SDK 与 Avalonia 版本。
    /// <c>vela-plugin</c> 读它来生成 IDE 启动配置,免去三平台各自的安装位置探测。
    /// </summary>
    public string HostRegistryFile { get; } = "";

    /// <summary>开发期插件根登记文件(<c>plugins.dev.txt</c>,每行一个目录)。</summary>
    public string DevPluginListFile { get; } = "";

    /// <summary>
    /// 开发期插件的禁用登记(每行一个插件 id)。
    /// <para>
    /// 已安装插件的禁用标记是插件目录里的 <c>.disabled</c> 文件,但开发期插件的"目录"
    /// 就是工程的 <c>bin/Debug/net11.0</c> —— 标记写进去,<c>dotnet build</c> 既不会清掉它,
    /// 也不会在 <c>clean</c> 之外提醒你它还在,于是表现为"我明明重编了怎么还是禁用状态"。
    /// 所以开发期插件的禁用状态记在数据根这一侧,构建产物保持干净。
    /// </para>
    /// </summary>
    public string DevPluginDisabledFile { get; } = "";

    /// <summary>
    /// 开发期插件的影子副本目录。装载前把插件目录整份复制到这里再从副本加载,
    /// 于是 Windows 上宿主运行时不再锁住工程的 <c>bin</c>,可以边跑边重编。
    /// </summary>
    public string DevPluginShadowDirectory { get; } = "";

    /// <summary>日志目录(会话日志、传输日志与调试期的插件进程 pid 文件)。</summary>
    public string LogsDirectory { get; } = "";

    /// <summary>
    /// 渲染模式标记文件。渲染后端必须在 Avalonia 初始化之前定下来,而那时 DI 与
    /// SonnetDB 都还没起来 —— 为此把这一项设置额外镜像成一个单行小文件,
    /// 启动路径只做一次 File.ReadAllText,不引入任何数据库初始化开销。
    /// </summary>
    public string RenderModeFile { get; }

    /// <summary>离线 IP 归属地数据库(*.mmdb)的存放目录。</summary>
    public string GeoIpDirectory { get; }

    /// <summary>历史 JSON 设置文件(仅用于一次性迁移导入)。</summary>
    public string SettingsFile { get; }

    /// <summary>历史 JSON 状态文件(仅用于一次性迁移导入)。</summary>
    public string StateFile { get; }

    /// <summary>历史 JSON 会话文件(仅用于一次性迁移导入)。</summary>
    public string SessionsFile { get; }

    /// <summary>嵌入式 SonnetDB 数据目录(唯一的持久化存储)。</summary>
    public string SonnetDbDirectory { get; }

    /// <summary>AES-256 敏感字段加密的本地密钥文件。</summary>
    public string SecretKeyFile { get; }

    /// <summary>用户安装插件目录(<c>~/.velashell/plugins</c>)。</summary>
    public string UserPluginDirectory { get; }

    /// <summary>旧版应用数据根目录(仅供首次迁移读取并在成功后删除)。</summary>
    public string LegacyLocalAppDataDirectory { get; }

    /// <summary>早期版本快捷命令 JSON 文件(仅用于一次性迁移导入)。</summary>
    public string LegacyQuickCommandsFile { get; }

    /// <summary>默认数据根(<c>~/.velashell</c>)。</summary>
    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velashell");

    /// <summary>
    /// 归一化数据根:展开环境变量与起首的 <c>~</c>,取绝对路径。
    /// 解析不出来时退回默认根 —— 一个写坏的 <c>--data-root</c> 该表现为"参数没生效",
    /// 而不是让应用起不来。
    /// </summary>
    private static string ResolveRoot(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return DefaultRootDirectory;
        }
        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            if (expanded is "~" || expanded.StartsWith("~/", StringComparison.Ordinal)
                               || expanded.StartsWith("~\\", StringComparison.Ordinal))
            {
                expanded = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    expanded[1..].TrimStart('/', '\\'));
            }
            return expanded.Length == 0 ? DefaultRootDirectory : Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DefaultRootDirectory;
        }
    }
}
