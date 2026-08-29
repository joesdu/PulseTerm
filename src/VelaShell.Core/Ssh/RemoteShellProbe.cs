using System.Collections.Concurrent;

namespace VelaShell.Core.Ssh;

/// <summary>
/// 远端默认 shell 的 POSIX 探针:回答"能不能往这台机器的交互式 shell 里喂一段 sh 代码"。
/// <para>
/// 目录上报钩子(OSC 7,见 MainWindowViewModel.WorkingDirectoryReportHook)是给 Linux/类 Unix
/// 用的 —— 文件浏览器「跟随终端目录」靠它拿 cwd。可它是**盲注**的:连上就写进去,
/// 对端是什么 shell 无所谓。Windows OpenSSH 的默认 shell 是 cmd.exe,那一整行于是被当成
/// 一条命令执行,屏幕上留下 <c>'test' 不是内部或外部命令</c>(#305);PowerShell 作默认 shell
/// 同理。钩子里那个 <c>test -n "$BASH_VERSION"</c> 守卫只挡得住 fish/csh 这类**POSIX 世界内部**
/// 的差异,挡不住根本不认 sh 语法的 shell。
/// </para>
/// <para>
/// 所以先问一句再注入。探针走**独立的 exec 通道**,不碰用户的交互式 shell,终端里一个字符都看不见;
/// 用完即关,排在开交互 shell 之前,连 <c>MaxSessions 1</c> 的服务端也只需要一个通道名额。
/// 代价是每台主机的**首次**连接多一次往返 —— 结论按主机缓存,重连与新标签直接取用。
/// </para>
/// </summary>
public static class RemoteShellProbe
{
    /// <summary>
    /// 探针命令。要求三件只有 POSIX shell 才同时做得到的事:有 <c>printf</c>、
    /// 会做 <c>$((...))</c> 算术展开、认 <c>${var:-默认值}</c> 的默认值展开。
    /// 三样凑齐才拼得出 <see cref="PosixMarker" />(实测 bash/zsh/dash/sh 均通过):
    /// <list type="bullet">
    /// <item>cmd.exe:<c>'printf' 不是内部或外部命令</c>,退出码非 0。</item>
    /// <item>PowerShell:找不到 printf 命令,退出码非 0。</item>
    /// <item>cmd.exe 而 PATH 上恰好有 MSYS/Git 的 printf.exe(常见于装了 Git for Windows 的机器):
    /// 命令跑通了,但 cmd 两种展开都不做,打出的是字面量 —— 标记对不上,仍判为非 POSIX。</item>
    /// <item>PowerShell 而 PATH 上有 printf.exe:PowerShell 的 <c>$(...)</c> 子表达式**会**把
    /// <c>$((6*7))</c> 算成 42,但 <c>${vela_probe_ok:-ok}</c> 被它当成驱动器限定的变量名而展开为空 ——
    /// 少了后半截,标记同样对不上。两种展开缺一不可,正是为了堵住这一条。</item>
    /// <item>fish/csh 不支持这两种展开,一并落在"非 POSIX"这边;它们本就不吃 bash 钩子,不吃亏。</item>
    /// </list>
    /// </summary>
    public const string ProbeCommand =
        """
        printf 'vela-posix-%s%s\n' "$((6*7))" "${vela_probe_ok:-ok}"
        """;

    /// <summary>探针输出里必须原样出现的标记(两种展开都做对了才拼得出来)。</summary>
    public const string PosixMarker = "vela-posix-42ok";

    /// <summary>
    /// 探针超时。给足一次 exec 通道往返即可;超时按"探不到"处理 —— 宁可丢掉目录跟随,
    /// 也不能把 sh 代码糊到一个不认它的 shell 上。
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>主机 → 探测结论。同一台机器只问一次,重连与新标签直接取缓存。</summary>
    private static readonly ConcurrentDictionary<string, bool> Results = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>缓存键:同一主机换了用户可能换了默认 shell,故用户名也进键。</summary>
    public static string CacheKey(string? host, int port, string? user) =>
        $"{user ?? string.Empty}@{host ?? string.Empty}:{port}";

    /// <summary>
    /// 判定一次探针执行的结果。退出码必须为 0 <b>且</b>标准输出里出现标记 ——
    /// 只看其一都不够:ForceCommand 会让退出码为 0 但输出的是别的东西,
    /// 而 cmd.exe 把命令原样回显时标记也对不上。
    /// </summary>
    public static bool IsPosixShell(RemoteCommandResult? result) =>
        result is { IsSuccess: true } && result.StandardOutput.Contains(PosixMarker, StringComparison.Ordinal);

    /// <summary>
    /// 探测(带缓存)。任何失败 —— exec 被禁、通道开不出来、超时、连接已断 —— 一律返回 false
    /// 且**不进缓存**:那些是环境噪声,不是"这台机器不是 POSIX"的结论,下次连接值得再问一次。
    /// </summary>
    /// <param name="client">已连接的 SSH 客户端。</param>
    /// <param name="cacheKey">见 <see cref="CacheKey" />;空串表示不缓存。</param>
    /// <param name="cancellationToken">取消令牌(连接被取消时一并放弃探测)。</param>
    public static async Task<bool> IsPosixShellAsync(
        ISshClientWrapper client,
        string cacheKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (cacheKey.Length > 0 && Results.TryGetValue(cacheKey, out bool cached))
        {
            return cached;
        }
        bool isPosix;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            isPosix = IsPosixShell(await client
                .RunCommandDetailedAsync(ProbeCommand, timeout.Token)
                .ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[VelaShell] POSIX shell probe failed: {ex.Message}");
            return false;
        }
        if (cacheKey.Length > 0)
        {
            Results[cacheKey] = isPosix;
        }
        return isPosix;
    }

    /// <summary>清空缓存(单元测试用;主机换了默认 shell 时重启应用即可)。</summary>
    public static void ClearCache() => Results.Clear();
}
