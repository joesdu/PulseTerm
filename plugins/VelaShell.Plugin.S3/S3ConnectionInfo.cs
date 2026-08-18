using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.S3;

/// <summary>
/// 一条 S3 会话的连接参数:端点、端口、凭据与协议设置。
/// 由宿主送来的 <see cref="ProtocolConnectRequest" /> 归一化而成。
/// </summary>
public sealed class S3ConnectionInfo
{
    /// <summary>服务端点主机名(不含协议与路径)。</summary>
    public required string Endpoint { get; init; }

    /// <summary>端口;未给出时按是否 TLS 取 443 / 80。</summary>
    public int Port { get; init; } = S3Settings.DefaultPort;

    /// <summary>Access Key ID;匿名访问时为空串。</summary>
    public string AccessKeyId { get; init; } = string.Empty;

    /// <summary>Secret Access Key;匿名访问时为空串。</summary>
    public string SecretAccessKey { get; init; } = string.Empty;

    /// <summary>协议设置。</summary>
    public S3Settings Settings { get; init; } = new();

    /// <summary>会话的展示名称(仅用于日志与提示)。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// 是否匿名访问。**两者都空 = 公开只读桶的匿名访问**,是一条正当路径;
    /// 只有「填了 Access Key 却没有 Secret」才是真的缺凭据(那一步由宿主弹登录框处理)。
    /// </summary>
    public bool IsAnonymous => AccessKeyId.Length == 0 && SecretAccessKey.Length == 0;

    /// <summary>拼出服务基址(<c>scheme://host:port</c>)。</summary>
    public Uri BaseUri => new($"{(Settings.UseTls ? "https" : "http")}://{Endpoint}:{Port}");

    /// <summary>
    /// 从宿主的连接请求归一化出连接参数。
    /// <para>
    /// 「主机」框里粘一整条 URL 是最常见的输入(从浏览器地址栏复制而来),
    /// 因此这里剥掉协议与路径,并在用户没单独填端口时用 URL 里的端口。
    /// 带协议的输入还会覆盖「使用 HTTPS」开关 —— 用户明写了 <c>http://</c>
    /// 却因为一个默认勾选去走 TLS,只会得到一个费解的握手失败。
    /// </para>
    /// </summary>
    /// <param name="request">宿主的连接请求。</param>
    /// <returns>连接参数。</returns>
    /// <exception cref="VelaS3ConnectionException">端点为空。</exception>
    public static S3ConnectionInfo FromRequest(ProtocolConnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = S3Settings.FromRequest(request);
        string raw = request.Host?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            throw new VelaS3ConnectionException("The S3 endpoint is empty.");
        }

        int? portFromUrl = null;
        if (Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https")
        {
            settings.UseTls = uri.Scheme == "https";
            raw = uri.Host;
            if (!uri.IsDefaultPort)
            {
                portFromUrl = uri.Port;
            }
        }
        else
        {
            // 没有协议头时 Uri.TryCreate 认不出来,自己剥一次尾巴上的路径。
            int slash = raw.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                raw = raw[..slash];
            }
            int colon = raw.LastIndexOf(':');
            if (colon > 0 && int.TryParse(raw[(colon + 1)..], out int inlinePort) && inlinePort is > 0 and <= 65535)
            {
                portFromUrl = inlinePort;
                raw = raw[..colon];
            }
        }

        settings.Endpoint = raw;
        int port = request.Port is > 0 and <= 65535
            ? request.Port
            : portFromUrl ?? (settings.UseTls ? S3Settings.DefaultPort : S3Settings.DefaultHttpPort);
        // 用户没改过端口而 URL 里写了另一个,以 URL 为准:粘 `http://host:9000` 却连到 80
        // 是最容易让人以为"服务没起"的一种失败。
        if (portFromUrl is { } explicitPort && request.Port is S3Settings.DefaultPort or S3Settings.DefaultHttpPort or 0)
        {
            port = explicitPort;
        }

        return new()
        {
            Endpoint = raw,
            Port = port,
            AccessKeyId = request.Username?.Trim() ?? string.Empty,
            SecretAccessKey = request.Password ?? string.Empty,
            Settings = settings,
            DisplayName = request.DisplayName,
        };
    }
}
