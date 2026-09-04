using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VelaShell.Plugin.Ai.Auth;

/// <summary>
/// 授权完成后浏览器会被打回本机 —— 这就是接住那一下的东西:一个只活一次的环回 HTTP 端口。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是裸 <see cref="TcpListener" /> 而不是 <c>HttpListener</c></b>:后者在 Windows 上走
/// http.sys,前缀注册要看 URL ACL 的脸色(非管理员在某些策略下直接 <c>AccessDenied</c>),
/// 而且它在部分平台上被标为不支持。我们要收的只是<b>一条 GET 请求行</b>,
/// 自己读一行、回一页,反而比引一整套服务端可靠 —— 而且三个平台上行为一致。
/// </para>
/// <para>
/// 只绑 <see cref="IPAddress.Loopback" />:授权码是短命凭据,但也不该出网卡。
/// 端口传 0 时由系统分配空闲端口 —— 除非供应商要求回调地址逐字节一致,那时才固定端口。
/// </para>
/// </remarks>
public sealed class LoopbackRedirectListener : IDisposable
{
    /// <summary>请求头读取上限:授权回调就是一条 GET,几百字节;超了必是喂垃圾,直接掐。</summary>
    private const int MaxRequestBytes = 16 * 1024;

    private readonly TcpListener _listener;
    private readonly string _path;
    private bool _disposed;

    private readonly string _host;

    /// <summary>起监听。</summary>
    /// <param name="port">固定端口;0 = 让系统挑一个空闲的。</param>
    /// <param name="path">回调路径(要与授权请求里给出的一致),如 <c>/callback</c>。</param>
    /// <param name="host">
    /// 写进 <c>redirect_uri</c> 的主机名(<c>127.0.0.1</c> 或 <c>localhost</c>)。
    /// <b>两者在严格比对的服务端那里不是一回事</b>,注册了哪个就得写哪个;
    /// 监听端无论如何只绑环回网卡。
    /// </param>
    public LoopbackRedirectListener(int port, string path, string host = "127.0.0.1")
    {
        _path = string.IsNullOrWhiteSpace(path) ? "/callback" : "/" + path.Trim().Trim('/');
        _host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        _listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            _listener.Start();
        }
        catch (SocketException ex) when (port != 0)
        {
            // 固定端口是被供应商钉死的(回调地址要逐字节一致),换一个也没用 ——
            // 所以这里给一句能照着做的话,而不是让一个 SocketException 冒到界面上
            _listener.Dispose();
            throw new OAuthException("port_in_use",
                $"Port {port} is already in use, and this provider requires exactly that port for its callback. " +
                $"Close whatever is holding it (often that vendor's own CLI) and sign in again. ({ex.SocketErrorCode})");
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>实际监听的端口(构造时传 0 的话,这里才是真正的那个)。</summary>
    public int Port { get; }

    /// <summary>要写进授权请求的回调地址。</summary>
    public string RedirectUri => $"http://{_host}:{Port}{_path}";

    /// <summary>
    /// 等浏览器打回来,返回回调地址上的查询参数(<c>code</c> / <c>state</c> / <c>error</c> …)。
    /// </summary>
    /// <remarks>
    /// 路径对不上的请求(浏览器顺手要的 <c>/favicon.ico</c> 最常见)回 404 之后<b>继续等</b> ——
    /// 拿它当结果的话,用户还没点同意就先失败了。
    /// </remarks>
    /// <param name="pageTitle">回给浏览器那一页的标题(已登录成功的提示)。</param>
    /// <param name="pageBody">页面正文(告诉用户可以关掉这个标签页了)。</param>
    /// <param name="fragment">
    /// 隐式流:结果在地址的 <c>#fragment</c> 里。
    /// <b>片段根本不会随请求发过来</b>(这是浏览器的规矩,不是哪家的实现细节),
    /// 所以这里先回一页只做一件事的 HTML:把 <c>location.hash</c> 原样再请求一次。
    /// 第二次进来时它就变成普通查询串了,后面的路径与授权码流程完全一致。
    /// </param>
    /// <param name="cancellationToken">取消(用户点了"取消登录",或超时)。</param>
    public async Task<Dictionary<string, string>> WaitAsync(string pageTitle, string pageBody,
        bool fragment = false, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using NetworkStream stream = client.GetStream();
            string? target = await ReadRequestTargetAsync(stream, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                continue; // 连上就断 / 不是 HTTP:不是我们要等的东西
            }
            int split = target.IndexOf('?');
            string path = split < 0 ? target : target[..split];
            if (!string.Equals(path, _path, StringComparison.Ordinal))
            {
                await WriteAsync(stream, "404 Not Found", "<p>Not found.</p>", cancellationToken).ConfigureAwait(false);
                continue;
            }
            // 隐式流的第一跳:什么参数都没有(令牌全在 # 后面,服务端看不见)。
            // 出错时对方是拿<b>查询串</b>回的(?error=…),那一跳照常当结果收,别再去要片段
            if (fragment && split < 0)
            {
                await WriteAsync(stream, pageTitle, FragmentBootstrap(_path), cancellationToken).ConfigureAwait(false);
                continue;
            }
            Dictionary<string, string> query = split < 0
                ? []
                : OAuthClient.ParseQuery(target[(split + 1)..]);
            await WriteAsync(stream, pageTitle, $"<p>{Escape(pageBody)}</p>", cancellationToken).ConfigureAwait(false);
            return query;
        }
    }

    /// <summary>
    /// 把 <c>#fragment</c> 搬成查询串再请求一次的那一小段脚本。
    /// </summary>
    /// <remarks>
    /// 用 <c>location.replace</c> 而不是 <c>assign</c>:别在用户的历史记录里留下一条带令牌的地址。
    /// 片段为空(用户直接手敲了这个地址)时不跳转 —— 否则会和自己来回弹。
    /// </remarks>
    private static string FragmentBootstrap(string path) => $$"""
        <p>Finishing sign-in…</p>
        <script>
        (function () {
          var h = window.location.hash;
          if (h && h.length > 1) {
            window.location.replace({{Json(path)}} + "?" + h.substring(1));
          }
        })();
        </script>
        """;

    /// <summary>把一个字符串安全地嵌进 <c>&lt;script&gt;</c> 里(转义 <c>&lt;</c> 防提前闭合标签)。</summary>
    private static string Json(string text) =>
        System.Text.Json.JsonSerializer.Serialize(text).Replace("<", "\\u003c", StringComparison.Ordinal);

    /// <summary>读到请求头结束,取出请求行里的目标(<c>GET /callback?... HTTP/1.1</c> 的中间那段)。</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        var request = new StringBuilder();
        while (request.Length < MaxRequestBytes)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            // 只要拿到第一行就够了 —— 但要等到空行,免得请求行本身被拆包截断
            if (request.ToString().Contains("\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }
        string text = request.ToString();
        int lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        string line = lineEnd < 0 ? text : text[..lineEnd];
        string[] parts = line.Split(' ');
        return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
    }

    /// <summary>回一页极简 HTML。样式内联:这一页由用户的浏览器渲染,取不到本程序的任何资源。</summary>
    private static async Task WriteAsync(NetworkStream stream, string title, string bodyHtml,
        CancellationToken cancellationToken)
    {
        string html = $"""
            <!doctype html><html><head><meta charset="utf-8"><title>{Escape(title)}</title></head>
            <body style="font-family:system-ui,-apple-system,Segoe UI,sans-serif;background:#191A21;color:#F8F8F2;
            display:flex;align-items:center;justify-content:center;height:100vh;margin:0">
            <div style="text-align:center"><h2 style="font-weight:600;margin:0 0 8px">{Escape(title)}</h2>
            <div style="color:#6272A4;font-size:14px">{bodyHtml}</div></div></body></html>
            """;
        byte[] body = Encoding.UTF8.GetBytes(html);
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>页面上要显示的都是本程序自己的文案,但供应商的错误串也会走这里 —— 一律转义。</summary>
    private static string Escape(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _listener.Stop();
        _listener.Dispose();
    }
}
