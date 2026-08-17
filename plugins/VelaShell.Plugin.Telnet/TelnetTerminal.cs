using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Telnet;

/// <summary>连接表单里各字段的键。发布后不可更改 —— 它们会落进用户的会话配置。</summary>
internal static class TelnetFields
{
    /// <summary>上报给对端的 TERM(留空 = 跟随宿主的终端类型设置)。</summary>
    public const string TerminalType = "terminalType";

    /// <summary>回车改写方式:<c>crlf</c> / <c>crnul</c> / <c>cr</c>。</summary>
    public const string EnterMode = "enterMode";

    /// <summary>是否请求 BINARY(8 位透明)。</summary>
    public const string Binary = "binary";

    /// <summary>本地回显:<c>auto</c> / <c>on</c> / <c>off</c>。</summary>
    public const string LocalEcho = "localEcho";
}

/// <summary>
/// Telnet 的 <see cref="IProtocolTerminal" /> 实现:把连接表单翻成一份
/// <see cref="TelnetConfig" />,建立会话即交给宿主 —— 会话的生命周期由终端标签持有,
/// 插件这边不留会话表(与文件协议不同,终端没有"用 sessionId 反查"的调用)。
/// </summary>
/// <param name="context">插件上下文(取日志)。</param>
internal sealed class TelnetTerminal(IPluginContext context) : IProtocolTerminal
{
    /// <inheritdoc />
    public async Task<IProtocolTerminalSession> ConnectAsync(
        ProtocolConnectRequest request,
        ProtocolTerminalOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Host))
        {
            throw new ProtocolConnectionException("Telnet requires a host name or IP address.");
        }
        var config = new TelnetConfig(
            // 表单留空就跟随宿主的终端类型(设置 → 终端 → TERM),这是绝大多数情况下的正解;
            // 填了则以表单为准(老设备只认 vt100 / ansi)。
            TerminalType: request.GetString(TelnetFields.TerminalType, options.TerminalType),
            EnterMode: ParseEnterMode(request.GetString(TelnetFields.EnterMode, "crlf")),
            RequestBinary: request.GetBoolean(TelnetFields.Binary, true),
            LocalEcho: ParseLocalEcho(request.GetString(TelnetFields.LocalEcho, "auto")));
        return await TelnetSession.ConnectAsync(
            request.Host.Trim(),
            request.Port,
            config,
            options,
            context.Log,
            cancellationToken).ConfigureAwait(false);
    }

    private static TelnetEnterMode ParseEnterMode(string value) => value switch
    {
        "crnul" => TelnetEnterMode.CrNul,
        "cr" => TelnetEnterMode.Cr,
        _ => TelnetEnterMode.CrLf
    };

    private static TelnetLocalEcho ParseLocalEcho(string value) => value switch
    {
        "on" => TelnetLocalEcho.On,
        "off" => TelnetLocalEcho.Off,
        _ => TelnetLocalEcho.Auto
    };
}
