using System.Security.Authentication;
using StackExchange.Redis;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// Redis 工作台提供方:按宿主的请求连上服务器,交出一个文档。
/// <para>
/// 这里唯一"多做"的一件事是**异常翻译**:StackExchange.Redis 的异常类型不得越过插件边界
/// (与宿主里 FluentFTP / Tmds.Ssh、S3 插件里 AWSSDK 同一条硬规则),出口一律翻成 SDK 的
/// <c>Protocol*</c> 四类,宿主据此决定"重弹登录框"还是"弹证书信任"还是"报连接失败"。
/// </para>
/// </summary>
/// <param name="context">插件上下文。</param>
/// <param name="loc">文案表(语言切换时由入口就地替换)。</param>
internal sealed class RedisWorkspaceProvider(IPluginContext context, Loc loc) : IWorkspaceProvider
{
    /// <summary>当前文案表。语言切换时由 <see cref="RedisPlugin" /> 就地替换。</summary>
    public Loc Loc { get; set; } = loc;

    /// <inheritdoc />
    public async Task<IWorkspaceDocument> OpenAsync(
        WorkspaceConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = RedisSettings.From(request);
        RedisTlsTrust? trust = settings.UseTls ? new(settings.TrustedThumbprint) : null;
        RedisConnection connection;
        try
        {
            connection = await RedisConnection.ConnectAsync(
                request.Host, request.Port, request.Username, request.Password, settings, trust, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, request, trust);
        }
        context.Log.Info(
            $"Connected to {request.Host}:{request.Port} " +
            $"({connection.Info.Flavor} {connection.Info.Version}, {connection.Info.Mode}, {connection.Info.Protocol}).");
        return new RedisWorkspaceDocument(connection, request, Loc, context);
    }

    /// <summary>
    /// 库异常 → SDK 异常族。
    /// <para>
    /// 认证失败必须单独认出来:宿主看到 <see cref="ProtocolAuthenticationException" /> 会重弹登录框,
    /// 而看到 <see cref="ProtocolConnectionException" /> 只会报一句连不上 —— 后者对"密码打错了"
    /// 这种最常见的失败是最无用的反馈。
    /// </para>
    /// <para>
    /// 认不出的异常不包装:把插件给出的可读信息埋进"未知错误"只会让排障更难。
    /// </para>
    /// </summary>
    private Exception Translate(Exception ex, WorkspaceConnectRequest request, RedisTlsTrust? trust)
    {
        // 库把握手期的失败统统裹进 RedisConnectionException,真正的原因在内层或消息里。
        Exception root = ex is RedisConnectionException && ex.InnerException is { } inner ? inner : ex;
        string endpoint = $"{request.Host}:{request.Port}";

        if (IsAuthFailure(ex) || IsAuthFailure(root))
        {
            return new ProtocolAuthenticationException(Loc.Format("Redis_AuthFailed", Describe(root)));
        }
        // TLS 校验失败:交给宿主走「提示 → 记指纹 → 重连」。
        // 拿不到证书本体时不能假装拿到了 —— 那会让信任对话框显示一张空证书,
        // 用户点"信任"却记下一串空指纹,下次照样连不上。
        if (FindTlsFailure(root) is { } tls)
        {
            return trust?.SeenCertificate is { } certificate
                ? new ProtocolCertificateTrustException(
                    Loc.Format("Redis_ConnectFailed", endpoint, Describe(tls)),
                    certificate.Subject,
                    certificate.Issuer,
                    certificate.NotAfter,
                    certificate.Thumbprint,
                    trust.PolicyErrors.ToString())
                : new ProtocolConnectionException(Loc.Format("Redis_ConnectFailed", endpoint, Describe(tls)), tls);
        }
        return ex switch
        {
            OperationCanceledException => ex,
            RedisConnectionException or RedisTimeoutException or System.Net.Sockets.SocketException =>
                new ProtocolConnectionException(Loc.Format("Redis_ConnectFailed", endpoint, Describe(root)), ex),
            RedisCommandException command =>
                new ProtocolUnsupportedException(Loc.Format("Redis_CommandDenied", Describe(command))),
            _ => ex
        };
    }

    private static bool IsAuthFailure(Exception ex) =>
        ex is RedisServerException
        && (ex.Message.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("invalid password", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("without any password", StringComparison.OrdinalIgnoreCase))
        || (ex is RedisConnectionException
            && (ex.Message.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase)));

    private static Exception? FindTlsFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is AuthenticationException)
            {
                return current;
            }
        }
        return null;
    }

    private static string Describe(Exception ex) => ex.Message.Trim();
}
