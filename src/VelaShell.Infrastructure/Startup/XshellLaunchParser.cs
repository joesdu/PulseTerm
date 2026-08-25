using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using VelaShell.Core.Models;
using VelaShell.Infrastructure.Import;

namespace VelaShell.Infrastructure.Startup;

/// <summary>
/// Xshell 兼容的启动入口解析:把 <c>Xshell.exe</c> 那套命令行(以及网页里点出来的
/// <c>ssh://</c> 链接)翻译成 <see cref="ExternalLaunchRequest" />。
/// <para>
/// 这条路径存在的唯一理由是第三方安全软件(SSO/堡垒机客户端):它们只会按 Xshell 的调用约定
/// 发起登录 —— 要么 <c>-url ssh://user:一次性密码@host:port</c>,要么落一个临时 <c>.xsh</c>
/// 再 <c>-f</c> 打开。因此这里认的是**它们发得出的写法**,而不是我们喜欢的写法。
/// </para>
/// <para>
/// 认识的选项:<c>-url</c>、<c>-newtab</c>、<c>-f</c>、<c>-l</c>、<c>-p</c>、<c>-pw</c>、<c>-i</c>,
/// 外加一个裸 URL 位置参数(URL 协议关联走的就是这条:注册表里写的是 <c>"exe" -url "%1"</c>,
/// 但有的调用方会把 URL 直接甩在第一个参数上)。认不出的一律忽略 —— argv 是与 Avalonia、
/// 与 <see cref="VelaShellStartupArguments" /> 共用的。
/// </para>
/// </summary>
public static class XshellLaunchParser
{
    /// <summary>解析 argv;没有任何连接意图时返回 <see langword="null" />(正常启动)。</summary>
    public static ExternalLaunchRequest? TryParse(IReadOnlyList<string>? args)
    {
        string? url = null;
        string? sessionFile = null;
        string? user = null;
        string? password = null;
        string? keyFile = null;
        int port = 0;

        for (int i = 0; i < (args?.Count ?? 0); i++)
        {
            string arg = args![i];
            // 我们自己的长选项(--dev-root / --data-root …)不在这套语法里,直接跳过,
            // 免得 "--p" 之类被下面的单破折号分支误吞掉后面那个值。
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            if (!arg.StartsWith('-'))
            {
                // 裸位置参数:只有看着像 URL 才收,其余(Avalonia 自己的东西、拖拽进来的路径)放过。
                if (url is null && LooksLikeUrl(arg))
                {
                    url = Unquote(arg);
                }
                continue;
            }

            (string name, string? inline) = SplitOption(arg);
            switch (name)
            {
                case "-url":
                case "-newtab":
                    // -newtab 在 Xshell 里既可带 URL,也可只是「开新标签」的开关;带值才当 URL 用。
                    if ((inline ?? PeekValue(args, ref i)) is { Length: > 0 } newUrl && LooksLikeUrl(newUrl))
                    {
                        url ??= Unquote(newUrl);
                    }
                    break;
                case "-f":
                case "-file":
                    sessionFile ??= Unquote(inline ?? PeekValue(args, ref i) ?? "");
                    break;
                case "-l":
                case "-user":
                    user ??= Unquote(inline ?? PeekValue(args, ref i) ?? "");
                    break;
                case "-p":
                case "-port":
                    if (int.TryParse(inline ?? PeekValue(args, ref i), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int parsedPort))
                    {
                        port = parsedPort;
                    }
                    break;
                case "-pw":
                case "-password":
                    password ??= Unquote(inline ?? PeekValue(args, ref i) ?? "");
                    break;
                case "-i":
                case "-identity":
                    keyFile ??= Unquote(inline ?? PeekValue(args, ref i) ?? "");
                    break;
            }
        }

        ExternalLaunchRequest? request = url is not null
            ? ParseUrl(url, LooksLikeProtocolInvocation(args) ? ExternalLaunchOrigin.UrlProtocol : ExternalLaunchOrigin.CommandLine)
            : sessionFile is { Length: > 0 } file
                ? ParseSessionFile(file)
                : null;

        if (request is null)
        {
            // 光有 -l/-p/-pw 而没有目标主机 —— 连谁不知道,不是一次拉起请求。
            return null;
        }

        // 显式选项覆盖 URL 里的同名字段(Xshell 也是这个优先级)。
        return new ExternalLaunchRequest
        {
            Kind = ExternalLaunchKind.Connect,
            Scheme = request.Scheme,
            ConnectionType = request.ConnectionType,
            IsSupported = request.IsSupported,
            Host = request.Host,
            Port = port > 0 ? port : request.Port,
            Username = user is { Length: > 0 } ? user : request.Username,
            Password = password is { Length: > 0 } ? password : request.Password,
            PrivateKeyPath = keyFile is { Length: > 0 } ? keyFile : request.PrivateKeyPath,
            Origin = request.Origin
        };
    }

    /// <summary>
    /// 解析一条 <c>scheme://[user[:password]@]host[:port][/…]</c> 形式的 URL。
    /// 解析不出主机时返回 <see langword="null" />。
    /// </summary>
    /// <remarks>
    /// 刻意手写而不是丢给 <see cref="Uri" />:堡垒机现发的一次性密码里出现未转义的
    /// <c>@ : / #</c> 是常态,<see cref="Uri" /> 遇到就直接判非法 URI 或把主机截错;
    /// 而这里按「最后一个 @ 才是主机分界」来切,天然容得下密码里的 @。
    /// </remarks>
    public static ExternalLaunchRequest? ParseUrl(string? url, ExternalLaunchOrigin origin = ExternalLaunchOrigin.UrlProtocol)
    {
        string text = Unquote(url ?? "");
        if (text.Length == 0)
        {
            return null;
        }

        string scheme = "ssh";
        int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            scheme = text[..schemeEnd].Trim().ToLowerInvariant();
            text = text[(schemeEnd + 3)..];
        }

        // 主机之后的路径/查询/片段与登录无关(<c>ssh://u@h:22/?foo=1</c> 这类调用方常见)。
        int authorityEnd = text.AsSpan().IndexOfAny('/', '?', '#');
        if (authorityEnd >= 0)
        {
            text = text[..authorityEnd];
        }
        if (text.Length == 0)
        {
            return null;
        }

        string username = string.Empty;
        string? password = null;
        int at = text.LastIndexOf('@');
        if (at >= 0)
        {
            string userInfo = text[..at];
            text = text[(at + 1)..];
            int colon = userInfo.IndexOf(':', StringComparison.Ordinal);
            username = Decode(colon >= 0 ? userInfo[..colon] : userInfo);
            if (colon >= 0)
            {
                // 密码里的 : 不再切分(只认第一个),与 Xshell 一致。
                password = Decode(userInfo[(colon + 1)..]);
            }
        }

        if (!TrySplitHostPort(text, out string host, out int port) || host.Length == 0)
        {
            return null;
        }

        (ConnectionType type, bool supported, int defaultPort) = MapScheme(scheme);
        return new ExternalLaunchRequest
        {
            Kind = ExternalLaunchKind.Connect,
            Scheme = scheme,
            ConnectionType = type,
            IsSupported = supported,
            Host = host,
            Port = port > 0 ? port : defaultPort,
            Username = username,
            Password = string.IsNullOrEmpty(password) ? null : password,
            Origin = origin
        };
    }

    /// <summary>
    /// 解析 <c>-f</c> 指向的 Xshell <c>.xsh</c> 会话文件(部分堡垒机落一个临时文件再拉起)。
    /// 只取主机/端口/用户名/协议:文件里的密码是 Xshell 按**它自己**的用户身份加密的,
    /// 我们既解不开也不该去解,缺凭据时照常走应用内的登录弹窗。
    /// </summary>
    private static ExternalLaunchRequest? ParseSessionFile(string path)
    {
        XshellSessionFile parsed;
        try
        {
            // .xsh 是 UTF-16 INI(带 BOM),按 UTF-8 读会得到一串夹着 \0 的乱码,主机字段解析成空。
            using var reader = new StreamReader(path, System.Text.Encoding.Unicode, detectEncodingFromByteOrderMarks: true);
            parsed = XshellIniParser.Parse(ReadAllLines(reader));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(parsed.Host))
        {
            return null;
        }
        string scheme = (parsed.Protocol ?? "ssh").Trim().ToLowerInvariant();
        (ConnectionType type, bool supported, int defaultPort) = MapScheme(scheme);
        return new ExternalLaunchRequest
        {
            Kind = ExternalLaunchKind.Connect,
            Scheme = scheme.Length == 0 ? "ssh" : scheme,
            ConnectionType = type,
            IsSupported = supported,
            Host = parsed.Host!.Trim(),
            Port = parsed.Port > 0 ? parsed.Port : defaultPort,
            Username = parsed.UserName?.Trim() ?? string.Empty,
            PrivateKeyPath = string.IsNullOrWhiteSpace(parsed.UserKey) ? null : parsed.UserKey.Trim(),
            Origin = ExternalLaunchOrigin.SessionFile
        };
    }

    /// <summary>scheme → 连接类型 / 是否受支持 / 该 scheme 的默认端口。</summary>
    private static (ConnectionType Type, bool Supported, int DefaultPort) MapScheme(string scheme) =>
        scheme switch
        {
            "ssh" or "" => (ConnectionType.SSH, true, 22),
            "sftp" => (ConnectionType.SFTP, true, 22),
            "ftp" => (ConnectionType.FTP, true, FtpSettings.DefaultPort),
            "ftps" => (ConnectionType.FTP, true, FtpSettings.DefaultPort),
            "telnet" => (ConnectionType.SSH, false, 23),
            "rlogin" => (ConnectionType.SSH, false, 513),
            _ => (ConnectionType.SSH, false, 22)
        };

    /// <summary>切分 <c>host[:port]</c>,兼容 IPv6 的 <c>[::1]:22</c> 写法。</summary>
    private static bool TrySplitHostPort(string authority, out string host, out int port)
    {
        host = authority.Trim();
        port = 0;
        if (host.StartsWith('['))
        {
            int close = host.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }
            string bracketed = host[1..close];
            string rest = host[(close + 1)..];
            host = bracketed;
            return rest.Length == 0
                   || (rest[0] == ':' && int.TryParse(rest[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port));
        }
        int colon = host.LastIndexOf(':');
        if (colon >= 0
            && int.TryParse(host[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            port = parsed;
            host = host[..colon];
        }
        return true;
    }

    /// <summary>逐行读出一个已按 UTF-16 打开的 <c>.xsh</c>。</summary>
    private static List<string> ReadAllLines(TextReader reader)
    {
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// 这次拉起是不是系统 URL 协议触发的(网页里点了 <c>ssh://</c>)。协议关联写进注册表的命令行
    /// 固定是 <c>exe -url "%1"</c>,而人工/脚本调用通常还会带上 <c>-l</c>、<c>-pw</c> 之类。
    /// 仅用于确认弹窗里显示来源,不参与放行判断 —— 判歪了也只是那行文案略糙。
    /// </summary>
    private static bool LooksLikeProtocolInvocation(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count is 0 or > 2)
        {
            return false;
        }
        return args.Count == 1 || args[0] is "-url" or "-newtab";
    }

    private static bool LooksLikeUrl(string value) =>
        value.Contains("://", StringComparison.Ordinal)
        || value.Contains('@', StringComparison.Ordinal) && !value.Contains(' ', StringComparison.Ordinal);

    /// <summary>取下一个参数作为值;下一个已是选项(或没有下一个)时返回 null 并保持位置不动。</summary>
    private static string? PeekValue(IReadOnlyList<string> args, ref int index)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith('-'))
        {
            return null;
        }
        return args[++index];
    }

    /// <summary>把 <c>-name=value</c> 拆成名字与内联值;没有等号时内联值为 null。</summary>
    private static (string Name, string? Inline) SplitOption(string arg)
    {
        int equals = arg.IndexOf('=', StringComparison.Ordinal);
        return equals > 0
            ? (arg[..equals].ToLowerInvariant(), arg[(equals + 1)..])
            : (arg.ToLowerInvariant(), null);
    }

    [return: NotNullIfNotNull(nameof(value))]
    private static string? Unquote(string? value) => value?.Trim().Trim('"');

    /// <summary>URL 百分号解码;不是合法转义序列时原样返回(调用方发来的密码常常没转义)。</summary>
    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
