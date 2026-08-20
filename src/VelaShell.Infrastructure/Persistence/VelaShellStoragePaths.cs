namespace VelaShell.Infrastructure.Persistence;

/// <summary>
/// 集中解析并暴露 VelaShell 各项持久化文件与目录的绝对路径(以 ~/.velashell 为根)。
/// </summary>
public sealed class VelaShellStoragePaths
{
    /// <summary>
    /// 基于当前用户的主目录构造所有存储路径。
    /// </summary>
    public VelaShellStoragePaths()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".velashell"
        );
        RootDirectory = root;
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
}
