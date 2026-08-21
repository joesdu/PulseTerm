using VelaShell.PluginSdk;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>插件的运行时状态。</summary>
public enum PluginState
{
    /// <summary>已发现且清单校验通过,尚未激活。</summary>
    Discovered,

    /// <summary>目录内存在 <c>.disabled</c> 标记文件,跳过激活。</summary>
    Disabled,

    /// <summary>清单缺失/非法,或与已装插件 id 冲突。</summary>
    Invalid,

    /// <summary>apiLevel 或 minHostVersion 与宿主不兼容。</summary>
    Incompatible,

    /// <summary>已激活并在运行。</summary>
    Active,

    /// <summary>装载或激活失败,或崩溃重启超限(原因见 <see cref="PluginDescriptor.Error" />)。</summary>
    Failed,

    /// <summary>隔离进程意外退出,等待退避重启(仅隔离模式)。</summary>
    Crashed,

    /// <summary>已正常停用(停机路径)。</summary>
    Deactivated
}

/// <summary>一个已发现插件的描述:清单、目录与当前状态。对外只读快照。</summary>
public sealed class PluginDescriptor
{
    /// <summary>插件清单;清单本身非法时为 <see langword="null" />。</summary>
    public PluginManifest? Manifest { get; init; }

    /// <summary>插件所在目录(绝对路径)。</summary>
    public required string Directory { get; init; }

    /// <summary>当前状态。</summary>
    public PluginState State { get; internal set; }

    /// <summary>失败/拒绝原因(状态为 Invalid/Incompatible/Failed 时非空)。</summary>
    public string? Error { get; internal set; }

    /// <summary>
    /// 是否来自开发期插件根(<see cref="PluginManagerOptions.DevPluginRoots" />):
    /// 直接挂载的插件工程输出目录,未经打包与安装。管理页据此显示 DEV 角标。
    /// </summary>
    public bool IsDevelopment { get; internal set; }

    /// <summary>
    /// 实际装载所用的目录。通常与 <see cref="Directory" /> 相同;开发期插件启用影子拷贝时
    /// 指向 <c>&lt;数据根&gt;/dev-shadow/&lt;id&gt;/gen-N</c>(见
    /// <see cref="PluginManagerOptions.DevShadowRootDirectory" />)。未激活时为 <see langword="null" />。
    /// </summary>
    public string? LoadDirectory { get; internal set; }

    /// <summary>装载时入口程序集的写入时间(UTC);开发期自动重载据此判断"是否重编过"。</summary>
    internal DateTime? EntryTimestampUtc { get; set; }

    /// <summary>插件 id;清单非法时退化为目录名。</summary>
    public string Id => Manifest?.Id ?? Path.GetFileName(Directory);
}
