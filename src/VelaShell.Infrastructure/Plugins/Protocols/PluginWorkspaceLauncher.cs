using System.Collections.Concurrent;
using System.Diagnostics;
using VelaShell.Core.Models;
using VelaShell.Core.Protocols;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Infrastructure.Plugins.Protocols;

/// <summary>
/// 宿主代为建好的端点(走 SSH 隧道时用)。
/// <para>
/// 由界面层准备:建 SSH 会话要走宿主既有的两步认证、指纹校验与 ProxyJump 链路,
/// 那些都在界面层。启动器只负责把它替换进连接请求 —— 插件因此只看到一个
/// 已经能连的本地端点,一次凭据都不用见。
/// </para>
/// </summary>
/// <param name="Host">本地转发监听地址。</param>
/// <param name="Port">本地转发端口。</param>
/// <param name="TargetHost">隧道另一端的真实目标主机(仅供界面显示来路)。</param>
/// <param name="TargetPort">真实目标端口。</param>
/// <param name="JumpDisplayName">跳板会话的展示名。</param>
public sealed record WorkspaceEndpoint(
    string Host,
    int Port,
    string TargetHost,
    int TargetPort,
    string JumpDisplayName);

/// <summary>一条已打开的工作台会话(交给界面挂标签页用)。</summary>
/// <param name="SessionId">宿主分配的会话 id。</param>
/// <param name="TypeName">连接类型展示名(如 <c>Redis</c>)。</param>
/// <param name="Document">插件交出的文档。</param>
public sealed record PluginWorkspaceSession(Guid SessionId, string TypeName, IWorkspaceDocument Document);

/// <summary>
/// 工作台会话的宿主侧启动器:解析注册表 → 组装连接请求 → 调插件 → 把 SDK 异常翻成
/// Core 中立异常族。
/// <para>
/// 与 <see cref="PluginProtocolFileService" /> 的分工:那个把插件的文件系统翻译成宿主的
/// <c>ISftpService</c>(因为宿主要**自己画**文件浏览器);这里不需要翻译任何数据面 ——
/// 界面是插件自己的。所以本类只做三件宿主该做的事:异常翻译、会话登记、
/// 以及"插件被停用时把它名下还开着的文档关掉"。
/// </para>
/// </summary>
/// <param name="registry">连接类型注册表。</param>
public sealed class PluginWorkspaceLauncher(PluginProtocolRegistry registry)
{
    private readonly ConcurrentDictionary<Guid, (string TypeId, IWorkspaceDocument Document)> _sessions = new();

    /// <summary>
    /// 某个连接类型被注销(插件停用/卸载)时触发,参数是该类型名下还开着的会话 id。
    /// 界面据此关掉对应标签页 —— 不做的话用户面对的是一个再也不会应答的面板。
    /// </summary>
    public event Action<Guid>? SessionAbandoned;

    /// <summary>打开一条工作台会话。**返回时首次连接已完成**,失败以 Core 异常族抛出。</summary>
    /// <param name="profile">用户的连接配置(凭据已解密)。</param>
    /// <param name="endpoint">
    /// 宿主代为建好的端点(走 SSH 隧道时非空)。给了它就用它,配置里的主机/端口只作为
    /// 界面上显示的"真实目标"。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已打开的会话。</returns>
    /// <exception cref="PluginProtocolUnavailableException">插件未安装/被禁用/激活失败。</exception>
    public async Task<PluginWorkspaceSession> OpenAsync(
        SessionProfile profile,
        WorkspaceEndpoint? endpoint = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string? typeId = profile.PluginProtocolId;
        PluginWorkspaceRegistration? registration = await registry.ResolveWorkspaceAsync(typeId).ConfigureAwait(false);
        if (registration is null)
        {
            throw new PluginProtocolUnavailableException(typeId,
                string.IsNullOrWhiteSpace(typeId)
                    ? "This session profile does not name a plugin connection type."
                    : $"Connection type '{typeId}' is not available. Install or enable the plugin that provides it.");
        }

        var sessionId = Guid.NewGuid();
        var request = new WorkspaceConnectRequest
        {
            SessionId = sessionId.ToString("N"),
            // 走隧道时递给插件的是**本地转发端点**;真实目标只进 Tunnel 供界面显示来路。
            Host = endpoint?.Host ?? profile.Host,
            Port = endpoint?.Port ?? profile.Port,
            Username = profile.Username,
            Password = profile.Password ?? string.Empty,
            Settings = BuildSettings(registration.Descriptor, profile),
            DisplayName = profile.Name,
            Tunnel = endpoint is null
                ? null
                : new(endpoint.TargetHost, endpoint.TargetPort, endpoint.JumpDisplayName)
        };

        IWorkspaceDocument document;
        try
        {
            document = await registration.Provider.OpenAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex, registration.Descriptor);
        }
        if (document is null)
        {
            throw new PluginProtocolConnectionException(
                $"Connection type '{registration.Descriptor.Id}' returned no document.");
        }
        _sessions[sessionId] = (registration.Descriptor.Id, document);
        return new(sessionId, registration.Descriptor.DisplayName, document);
    }

    /// <summary>把一条会话从登记表里摘掉(界面关闭标签页后调用;文档本身由界面释放)。</summary>
    /// <param name="sessionId">会话 id。</param>
    public void Forget(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>
    /// 某种连接类型被注销:通知界面关掉它名下的会话。
    /// <para>
    /// 这里只发通知、不直接 <c>DisposeAsync</c>:文档的释放要与标签页的关闭同一条路径走,
    /// 否则会出现"面板还在界面上、后面的连接已经没了"的半死状态。
    /// </para>
    /// </summary>
    /// <param name="connectionTypeId">被注销的连接类型 id。</param>
    public void OnUnregistered(string connectionTypeId)
    {
        foreach (KeyValuePair<Guid, (string TypeId, IWorkspaceDocument Document)> pair in _sessions.ToArray())
        {
            if (!pair.Value.TypeId.Equals(connectionTypeId, StringComparison.Ordinal)
                || !_sessions.TryRemove(pair.Key, out _))
            {
                continue;
            }
            try
            {
                SessionAbandoned?.Invoke(pair.Key);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[PluginWorkspace] Abandon handler for '{connectionTypeId}' threw: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 声明的默认值 → 用户存的明文值 → 用户存的机密值,后者覆盖前者。
    /// 与协议侧共用同一套口径(见 <see cref="PluginProtocolFileService" />):
    /// 缺失字段补默认值,这样插件加了新字段之后老配置照旧能连。
    /// </summary>
    private static Dictionary<string, string> BuildSettings(WorkspaceDescriptor descriptor, SessionProfile profile)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ProtocolSettingField field in descriptor.Fields)
        {
            if (field.DefaultValue is { } value)
            {
                settings[field.Key] = value;
            }
        }
        if (profile.PluginSettings is { } stored)
        {
            foreach (KeyValuePair<string, string> entry in stored)
            {
                settings[entry.Key] = entry.Value;
            }
        }
        if (profile.PluginSecrets is { } secrets)
        {
            foreach (KeyValuePair<string, string> entry in secrets)
            {
                settings[entry.Key] = entry.Value;
            }
        }
        return settings;
    }

    /// <summary>
    /// SDK 异常 → Core 中立异常族(与协议侧同一张表)。认不出的异常原样放行 ——
    /// 包装成"未知错误"只会把插件给出的可读信息埋掉。
    /// </summary>
    private static Exception Translate(Exception ex, WorkspaceDescriptor descriptor) =>
        ex switch
        {
            OperationCanceledException => ex,
            ProtocolAuthenticationException auth => new PluginProtocolAuthenticationException(auth.Message, auth),
            ProtocolCertificateTrustException cert => new PluginProtocolCertificateException(
                cert.Message, cert.Subject, cert.Issuer, cert.ExpiresOn, cert.Thumbprint, cert.PolicyErrors,
                descriptor.TrustedThumbprintSettingKey),
            ProtocolConnectionException conn => new PluginProtocolConnectionException(conn.Message, conn),
            ProtocolUnsupportedException unsupported => new NotSupportedException(unsupported.Message, unsupported),
            _ => ex
        };
}
