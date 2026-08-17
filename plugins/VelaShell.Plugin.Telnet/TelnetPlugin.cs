using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Telnet;

/// <summary>
/// Telnet 插件的入口。
/// <para>
/// 经 manifest 的 <c>onProtocol:velashell.telnet</c> **惰性激活**:用户在连接配置页点到
/// Telnet 页签(或打开一条 Telnet 会话)才装载本程序集。Telnet 曾是宿主里两个禁用的
/// 占位页签,现在以插件形式提供 —— 宿主不再认识任何一种具体协议。
/// </para>
/// <para>
/// 激活只做一件事:把协议注册成**终端协议**。此后终端桥、VT 引擎、回滚、搜索、
/// 会话日志、会话录制与 ZMODEM 全部由宿主原样复用,插件只实现字节双工
/// (<see cref="IProtocolTerminalSession" />)与 RFC 854 的选项协商。
/// </para>
/// </summary>
[VelaPlugin]
public sealed class TelnetPlugin : IVelaPlugin
{
    private IPluginContext? _context;
    private TelnetTerminal? _terminal;
    private IDisposable? _registration;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _terminal = new(context);
        _registration = context.Protocols.Register(BuildDescriptor(context), _terminal);
        // 语言切换后重注册:表单标签是插件自己的文案,宿主不会替我们翻。
        context.Events.LocaleChanged += _ => Reregister();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _registration?.Dispose();
        _registration = null;
        _terminal = null;
        _context = null;
        return Task.CompletedTask;
    }

    private void Reregister()
    {
        if (_context is not { } context || _terminal is not { } terminal)
        {
            return;
        }
        // **先注册后释放**(同 S3 插件):先 Dispose 会触发注销事件,把用户正开着的
        // 会话一起掐掉,而这里只是换了个界面语言。
        IDisposable next = context.Protocols.Register(BuildDescriptor(context), terminal);
        _registration?.Dispose();
        _registration = next;
    }

    /// <summary>
    /// 协议描述:页签、默认端口、连接表单。宿主按这份声明渲染界面,
    /// 因此插件没有一行连接对话框的界面代码。
    /// </summary>
    private static ProtocolDescriptor BuildDescriptor(IPluginContext context)
    {
        var loc = new Loc(context.Host.Locale);
        return new()
        {
            Id = context.PluginId,
            DisplayName = "Telnet",
            DefaultPort = 23,
            HostLabel = loc["Telnet_Host"],
            HostPlaceholder = loc["Telnet_HostPlaceholder"],
            // Telnet 没有协议级凭据:登录是带内的(对端打印 login: 提示,用户直接敲)。
            // 声明 NoCredentials 让宿主收起用户名/口令两栏 —— 摆在那儿只会让人以为填了就能自动登录。
            Features = ProtocolFeatures.AnonymousAccess | ProtocolFeatures.NoCredentials,
            Fields =
            [
                new()
                {
                    Key = TelnetFields.TerminalType,
                    Label = loc["Telnet_TerminalType"],
                    Placeholder = "xterm-256color",
                    Hint = loc["Telnet_TerminalTypeHint"],
                },
                new()
                {
                    Key = TelnetFields.EnterMode,
                    Label = loc["Telnet_EnterMode"],
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "crlf",
                    Hint = loc["Telnet_EnterModeHint"],
                    Choices =
                    [
                        new("crlf", loc["Telnet_EnterCrLf"]),
                        new("crnul", loc["Telnet_EnterCrNul"]),
                        new("cr", loc["Telnet_EnterCr"]),
                    ],
                },
                new()
                {
                    Key = TelnetFields.Binary,
                    Label = loc["Telnet_Binary"],
                    Kind = ProtocolSettingKind.Boolean,
                    DefaultValue = "true",
                    IsAdvanced = true,
                    Hint = loc["Telnet_BinaryHint"],
                },
                new()
                {
                    Key = TelnetFields.LocalEcho,
                    Label = loc["Telnet_LocalEcho"],
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "auto",
                    IsAdvanced = true,
                    Hint = loc["Telnet_LocalEchoHint"],
                    Choices =
                    [
                        new("auto", loc["Telnet_EchoAuto"]),
                        new("on", loc["Telnet_EchoOn"]),
                        new("off", loc["Telnet_EchoOff"]),
                    ],
                },
            ],
        };
    }
}
