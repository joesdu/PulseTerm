using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaShell.PluginSdk.Hosting;

/// <summary>
/// 一台机器上某一份 VelaShell 安装的自我描述。宿主每次启动把自己登记进
/// <c>~/.velashell/host.json</c>,插件工具链(<c>vela-plugin</c>)据此知道
/// "宿主装在哪、是什么版本、带的是哪一版 SDK 与 Avalonia"。
/// <para>
/// 为什么不让工具去探测:三个平台三套安装位置(注册表卸载键、<c>/Applications</c>、
/// 各种 Linux 前缀),还有便携版与自更新换过位置的情形 —— 探测逻辑既长又常年失准。
/// 宿主自报家门只要一次 <see cref="File.WriteAllText(string,string)" />,而且报的一定是真的。
/// </para>
/// </summary>
public sealed class HostRegistryEntry
{
    /// <summary>宿主可执行文件的绝对路径(IDE 启动配置直接用它)。</summary>
    public string ExePath { get; set; } = "";

    /// <summary>隔离插件宿主进程的绝对路径;缺席时为 <see langword="null" />。</summary>
    public string? PluginHostPath { get; set; }

    /// <summary>宿主版本(<c>AssemblyInformationalVersion</c>,可带预发布后缀)。</summary>
    public string Version { get; set; } = "";

    /// <summary>宿主支持的插件 apiLevel 代际。</summary>
    public int ApiLevel { get; set; }

    /// <summary>宿主内置的插件 SDK 版本(<see cref="VelaPluginApi.SdkVersion" />)。</summary>
    public string SdkVersion { get; set; } = "";

    /// <summary>
    /// 宿主实际加载的 Avalonia 版本。插件必须与它完全一致(装载器强制共享宿主那一套),
    /// 所以这一项比构建包里硬编码的那个值更可信 —— 用户装的宿主可能比 SDK 包新或旧。
    /// </summary>
    public string? AvaloniaVersion { get; set; }

    /// <summary>宿主使用的数据根目录(<c>plugins.dev.txt</c>、插件数据、影子目录都在其下)。</summary>
    public string DataRoot { get; set; } = "";

    /// <summary>用户插件安装目录(<c>.vpx</c> 落此处)。</summary>
    public string? UserPluginRoot { get; set; }

    /// <summary>运行标识(<c>win-x64</c> / <c>osx-arm64</c> / <c>linux-x64</c>)。</summary>
    public string? RuntimeIdentifier { get; set; }

    /// <summary>最近一次启动时间(UTC)。多份安装并存时以此择新。</summary>
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>
/// <c>host.json</c> 的文件形态:一台机器上可能同时存在多份 VelaShell
/// (正式安装 + 便携版 + 预览版),因此存的是数组而不是单条 —— 只留最近一条的话,
/// 装了预览版的开发者每次开一下预览版,工具链就会指到预览版上去。
/// </summary>
public sealed class HostRegistryFile
{
    /// <summary>文件结构版本。</summary>
    public int Schema { get; set; } = 1;

    /// <summary>已知的 VelaShell 安装,按 <see cref="HostRegistryEntry.LastSeen" /> 倒序。</summary>
    public List<HostRegistryEntry> Hosts { get; set; } = [];
}

/// <summary>
/// <c>host.json</c> 的读写。宿主写、<c>vela-plugin</c> 读,双方共用这一份实现,
/// 不存在"工具认的字段宿主没写"这种缝。
/// <para>
/// 全部读路径都对损坏/缺失容错(返回空表):这个文件只是加速工具链的缓存,
/// 坏掉的后果应当是"工具让你手动指一下路径",而不是任何一侧起不来。
/// </para>
/// </summary>
public static class HostRegistry
{
    /// <summary>文件名。</summary>
    public const string FileName = "host.json";

    /// <summary>保留的安装条目上限(超出时丢弃最旧的)。</summary>
    private const int MaxEntries = 8;

    /// <summary>默认数据根目录(<c>~/.velashell</c>),与宿主的 <c>VelaShellStoragePaths</c> 一致。</summary>
    public static string DefaultDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".velashell");

    /// <summary>默认注册表路径(<c>~/.velashell/host.json</c>)。</summary>
    public static string DefaultPath => Path.Combine(DefaultDataRoot, FileName);

    /// <summary>路径比较口径:Windows 大小写不敏感。</summary>
    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>读取注册表;文件缺失或损坏时返回空表(绝不抛)。</summary>
    /// <param name="path">注册表路径;缺省为 <see cref="DefaultPath" />。</param>
    public static HostRegistryFile Read(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
            {
                return new();
            }
            return JsonSerializer.Deserialize(File.ReadAllText(path), HostRegistryJsonContext.Default.HostRegistryFile)
                   ?? new HostRegistryFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    /// <summary>
    /// 已登记的安装列表(按最近启动倒序)。<paramref name="onlyExisting" /> 为真时
    /// 剔除可执行文件已不存在的条目(卸载/挪走过的旧安装)。
    /// </summary>
    public static IReadOnlyList<HostRegistryEntry> List(string? path = null, bool onlyExisting = true)
    {
        List<HostRegistryEntry> hosts = Read(path).Hosts;
        IEnumerable<HostRegistryEntry> query = hosts.Where(h => !string.IsNullOrWhiteSpace(h.ExePath));
        if (onlyExisting)
        {
            query = query.Where(h => File.Exists(h.ExePath));
        }
        return [.. query.OrderByDescending(h => h.LastSeen)];
    }

    /// <summary>
    /// 选出一份安装:<paramref name="selector" /> 为空取最近启动的一份;
    /// 否则按可执行文件路径、版本号(前缀匹配)依次尝试。选不中返回 <see langword="null" />。
    /// </summary>
    /// <param name="selector">可执行文件路径或版本号(如 <c>1.4</c>)。</param>
    /// <param name="path">注册表路径;缺省为 <see cref="DefaultPath" />。</param>
    public static HostRegistryEntry? Resolve(string? selector = null, string? path = null)
    {
        IReadOnlyList<HostRegistryEntry> hosts = List(path);
        if (string.IsNullOrWhiteSpace(selector))
        {
            return hosts.FirstOrDefault();
        }
        string wanted = selector.Trim().Trim('"');
        return hosts.FirstOrDefault(h => PathComparer.Equals(h.ExePath, wanted))
               ?? hosts.FirstOrDefault(h => h.Version.Equals(wanted, StringComparison.OrdinalIgnoreCase))
               ?? hosts.FirstOrDefault(h => h.Version.StartsWith(wanted + ".", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 登记(或刷新)一份安装。按 <see cref="HostRegistryEntry.ExePath" /> 去重,
    /// 顺带剔除可执行文件已消失的旧条目并截断到 <see cref="MaxEntries" /> 条。
    /// <para>
    /// 写入是"临时文件 + 覆盖移动",于是并发写最坏的结果是某一次登记丢失,
    /// 而不是留下一个半截的 JSON 让工具链读不懂。任何异常都被吞掉:
    /// 登记失败只该让 <c>vela-plugin</c> 少一条捷径,不该影响宿主启动。
    /// </para>
    /// </summary>
    /// <returns>写入成功为 <see langword="true" />。</returns>
    public static bool Upsert(HostRegistryEntry entry, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.ExePath))
        {
            return false;
        }
        path ??= DefaultPath;
        try
        {
            HostRegistryFile file = Read(path);
            file.Schema = 1;
            List<HostRegistryEntry> hosts =
            [
                entry,
                .. file.Hosts.Where(h =>
                    !string.IsNullOrWhiteSpace(h.ExePath)
                    && !PathComparer.Equals(h.ExePath, entry.ExePath)
                    && File.Exists(h.ExePath))
            ];
            file.Hosts = [.. hosts.OrderByDescending(h => h.LastSeen).Take(MaxEntries)];

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, HostRegistryJsonContext.Default.HostRegistryFile));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>STJ 源生成上下文:注册表读写不走反射。</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HostRegistryFile))]
internal sealed partial class HostRegistryJsonContext : JsonSerializerContext;
