using System.Globalization;
using System.Text.RegularExpressions;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Redis;

/// <summary>在一台远端主机上探到的一个 Redis 实例。</summary>
/// <param name="Host">监听地址(通常是 127.0.0.1)。</param>
/// <param name="Port">端口。</param>
/// <param name="Version">服务器版本;探不到时为空串。</param>
/// <param name="ConfigPath">对应的配置文件路径;没找到时为空串。</param>
/// <param name="HasPassword">配置里是否设了 <c>requirepass</c>。</param>
/// <param name="UsesTls">是否是 <c>tls-port</c> 监听。</param>
public sealed record RedisDiscoveredInstance(
    string Host,
    int Port,
    string Version,
    string ConfigPath,
    bool HasPassword,
    bool UsesTls)
{
    /// <summary>
    /// 探到的口令。**不进 <c>ToString</c>、不进日志** —— 它只在提议连接的那一刻被交给宿主,
    /// 由宿主按「记住密码」策略加密落盘。
    /// </summary>
    internal string Password { get; init; } = "";

    /// <summary>列表里的一行显示文本。</summary>
    public string Display =>
        $"{Host}:{Port}"
        + (Version.Length > 0 ? $"  ·  {Version}" : string.Empty)
        + (HasPassword ? "  ·  requirepass" : string.Empty)
        + (UsesTls ? "  ·  TLS" : string.Empty);
}

/// <summary>
/// 从一条**已连接的 SSH 会话**里探测远端的 Redis 实例。
/// <para>
/// 这是本插件唯一做得到、而独立 Redis 图形客户端永远做不到的事:用户已经 SSH 在那台机器上,
/// 于是主机名不用抄、端口不用记、密码不用翻配置、隧道不用手开。
/// 用的全是现成能力(<see cref="IRemoteExecApi" /> + <c>IRemoteFsApi</c>)。
/// </para>
/// <para>
/// 纪律:探测命令**一次 exec 批量执行**,不逐条敲远端(SDK §9 的"远端友好");
/// 读到的 <c>requirepass</c> 不写日志、不进任何 <c>ToString</c>。
/// </para>
/// </summary>
internal sealed partial class RedisDiscovery(IPluginContext context)
{
    /// <summary>形如 <c>127.0.0.1:6379</c> / <c>*:6379</c> / <c>[::1]:6379</c> 的监听端点。</summary>
    [GeneratedRegex(@"(?<host>\[[0-9a-fA-F:]+\]|[0-9.*]+):(?<port>\d{1,5})\s")]
    private static partial Regex ListenEndpoint();

    /// <summary>配置文件里的一行 <c>key value</c>(忽略注释与空行)。</summary>
    [GeneratedRegex(@"^\s*(?<key>[A-Za-z-]+)\s+(?<value>.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex ConfigLine();

    /// <summary>
    /// 在给定会话上探测。
    /// </summary>
    /// <param name="sessionId">已连接的 SSH 会话 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>探到的实例(按端口排序);一个都没有时为空列表。</returns>
    public async Task<IReadOnlyList<RedisDiscoveredInstance>> ProbeAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        // 一次 exec 干完四件事:找监听端口、找进程命令行、问版本、列出候选配置文件。
        // 分段之间用一个不可能出现在输出里的标记分隔 —— 比四次往返省得多,
        // 而远端只被打扰一次。
        const string separator = "###VELA###";
        string script = string.Join($"; echo {separator}; ",
        [
            // ss 在新系统上有,netstat 在老系统上有:两条都跑,谁有输出算谁的。
            "(ss -lntp 2>/dev/null || netstat -lntp 2>/dev/null) | grep -i redis",
            "ps -eo args= 2>/dev/null | grep '[r]edis-server'",
            "(redis-server --version 2>/dev/null || redis-cli --version 2>/dev/null)",
            "ls -1 /etc/redis/*.conf /etc/redis.conf /usr/local/etc/redis.conf 2>/dev/null"
        ]);

        ExecResult probe;
        try
        {
            probe = await context.RemoteExec.RunAsync(
                sessionId, script,
                new ExecOptions { Timeout = TimeSpan.FromSeconds(15) },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            context.Log.Info($"Probing session '{sessionId}' failed: {ex.Message}");
            return [];
        }

        string[] sections = probe.Output.Split(separator);
        string listening = sections.Length > 0 ? sections[0] : string.Empty;
        string processes = sections.Length > 1 ? sections[1] : string.Empty;
        string version = ParseVersion(sections.Length > 2 ? sections[2] : string.Empty);
        string[] configPaths = sections.Length > 3
            ? sections[3].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        var ports = new SortedSet<int>(CollectPorts(listening));
        foreach (int port in CollectPortsFromProcesses(processes))
        {
            ports.Add(port);
        }
        if (ports.Count == 0)
        {
            return [];
        }

        // 逐个候选配置读一次(通常只有一份),把端口 → 配置对上。
        var byPort = new Dictionary<int, (string Path, string Password, bool Tls)>();
        foreach (string path in configPaths)
        {
            (int? port, string password, int? tlsPort) = await ReadConfigAsync(sessionId, path, cancellationToken)
                .ConfigureAwait(false);
            if (port is { } plain)
            {
                byPort[plain] = (path, password, false);
            }
            if (tlsPort is { } tls and > 0)
            {
                byPort[tls] = (path, password, true);
            }
        }

        var found = new List<RedisDiscoveredInstance>();
        foreach (int port in ports)
        {
            byPort.TryGetValue(port, out (string Path, string Password, bool Tls) config);
            found.Add(new(
                "127.0.0.1",
                port,
                version,
                config.Path ?? string.Empty,
                (config.Password ?? string.Empty).Length > 0,
                config.Tls)
            {
                Password = config.Password ?? string.Empty
            });
        }
        return found;
    }

    /// <summary>会话列表里可供探测的会话(只有连上的才探得动)。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已连接的会话。</returns>
    public async Task<IReadOnlyList<SessionInfo>> ConnectedSessionsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SessionInfo> sessions = await context.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. sessions.Where(session => session.State == SessionState.Connected)];
    }

    /// <summary>
    /// 读一份 redis.conf。
    /// <para>
    /// 走 <c>RemoteFs</c> 而不是 <c>cat</c>:配置文件常常只有 root 可读,而这里读不到
    /// 不该表现成"探测失败" —— 拿不到密码只意味着用户要自己填一次。
    /// </para>
    /// </summary>
    private async Task<(int? Port, string Password, int? TlsPort)> ReadConfigAsync(
        string sessionId,
        string path,
        CancellationToken cancellationToken)
    {
        string text;
        try
        {
            byte[] bytes = await context.RemoteFs
                .ReadAllBytesAsync(sessionId, path, cancellationToken: cancellationToken).ConfigureAwait(false);
            text = System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            // 权限不足 / 文件不在:如实降级,不写文件内容进日志。
            context.Log.Info($"Cannot read '{path}': {ex.Message}");
            return (null, string.Empty, null);
        }

        int? port = null;
        int? tlsPort = null;
        string password = string.Empty;
        foreach (Match match in ConfigLine().Matches(text))
        {
            string key = match.Groups["key"].Value.ToLowerInvariant();
            string value = match.Groups["value"].Value.Trim().Trim('"');
            switch (key)
            {
                case "port" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed):
                    port = parsed > 0 ? parsed : null;
                    break;
                case "tls-port" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tls):
                    tlsPort = tls > 0 ? tls : null;
                    break;
                case "requirepass":
                    password = value;
                    break;
                default:
                    break;
            }
        }
        return (port, password, tlsPort);
    }

    private static IEnumerable<int> CollectPorts(string listening)
    {
        foreach (Match match in ListenEndpoint().Matches(listening + "\n"))
        {
            if (int.TryParse(match.Groups["port"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
                && port is > 0 and <= 65535)
            {
                yield return port;
            }
        }
    }

    /// <summary>从 <c>redis-server *:6379</c> 这样的进程命令行里取端口。</summary>
    private static IEnumerable<int> CollectPortsFromProcesses(string processes)
    {
        foreach (string line in processes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (Match match in ListenEndpoint().Matches(line + "\n"))
            {
                if (int.TryParse(match.Groups["port"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port)
                    && port is > 0 and <= 65535)
                {
                    yield return port;
                }
            }
        }
    }

    /// <summary>从 <c>Redis server v=7.2.4 ...</c> 或 <c>redis-cli 7.2.4</c> 里取版本号。</summary>
    private static string ParseVersion(string output)
    {
        foreach (string token in output.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = token.StartsWith("v=", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
            if (candidate.Count(c => c == '.') >= 2
                && candidate.All(c => char.IsAsciiDigit(c) || c == '.'))
            {
                return candidate;
            }
        }
        return string.Empty;
    }
}
