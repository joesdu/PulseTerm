using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Terminal.FileTransfer;

/// <summary>从用户敲下的命令行推断出的传输意图。</summary>
/// <param name="Protocol">将要使用的协议变体。</param>
/// <param name="Direction">本地扮演的方向(远端 <c>s*</c> 发文件 = 本地接收)。</param>
public readonly record struct TransferCommandIntent(
    TerminalTransferProtocol Protocol,
    FileTransferDirection Direction);

/// <summary>
/// 把用户键入的命令行解析成传输意图。
/// <para>
/// 存在的理由:XMODEM / YMODEM 在链路上<b>没有任何引导序列</b> —— <c>sb</c>/<c>sx</c> 启动后静默
/// 等接收方发 <c>'C'</c>,<c>rb</c>/<c>rx</c> 只吐裸 <c>'C'</c>,在终端输出里与普通字符毫无区别,
/// 任何基于输出流的自动检测都必然误触发。但「用户敲了什么命令」是另一路完全独立的信号:
/// <see cref="Input.TerminalInputTracker" /> 已经逐字重建了命令行,拿来判断即可。WindTerm 对
/// <c>rx</c>/<c>sx</c>/<c>rb</c>/<c>sb</c> 做自动触发,只可能是同一思路。
/// </para>
/// <para>
/// 对 ZMODEM 则不用它启动会话(输出流里有引导,自动检测更可靠),只用来在其后的一小段时间里
/// 放宽检测判据(见 <see cref="ZModemDetector.AcceptUnanchoredHeader" />)。
/// </para>
/// </summary>
public static class TransferCommandParser
{
    /// <summary>
    /// 解析一行命令;不是传输命令时返回 <c>null</c>。
    /// </summary>
    /// <param name="commandLine">用户键入并提交的整行命令。</param>
    public static TransferCommandIntent? Parse(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }
        string[] tokens = commandLine.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int i = SkipPrefixTokens(tokens);
        if (i >= tokens.Length)
        {
            return null;
        }

        // 允许带路径调用(/usr/bin/sz);取基名后必须精确等于已知工具名,
        // 免得 "myszcript"、"echo sz" 之类被误判。
        string tool = BaseName(tokens[i]);
        FileTransferDirection direction;
        TerminalTransferProtocol protocol;
        switch (tool)
        {
            case "sz":
                (direction, protocol) = (FileTransferDirection.Receive, TerminalTransferProtocol.ZModem);
                break;
            case "rz":
                (direction, protocol) = (FileTransferDirection.Send, TerminalTransferProtocol.ZModem);
                break;
            case "sx":
                (direction, protocol) = (FileTransferDirection.Receive, TerminalTransferProtocol.XModem);
                break;
            case "rx":
                (direction, protocol) = (FileTransferDirection.Send, TerminalTransferProtocol.XModem);
                break;
            case "sb":
                (direction, protocol) = (FileTransferDirection.Receive, TerminalTransferProtocol.YModem);
                break;
            case "rb":
                (direction, protocol) = (FileTransferDirection.Send, TerminalTransferProtocol.YModem);
                break;
            default:
                return null;
        }

        // lrzsz 的 sz/rz 可以用开关切到 X/YMODEM(sz -X file、rz --ymodem)。
        return new(OverrideProtocol(protocol, tokens.AsSpan(i + 1)), direction);
    }

    /// <summary>跳过环境变量赋值前缀与 <c>sudo</c>,定位到真正的命令名。</summary>
    private static int SkipPrefixTokens(ReadOnlySpan<string> tokens)
    {
        int i = 0;
        while (i < tokens.Length)
        {
            string token = tokens[i];
            bool isEnvAssignment = token.Contains('=', StringComparison.Ordinal)
                && !token.StartsWith('-');
            if (isEnvAssignment || BaseName(token) is "sudo" or "command")
            {
                i++;
                continue;
            }
            break;
        }
        return i;
    }

    /// <summary>按 <c>-X</c> / <c>--ymodem</c> 之类的开关改写协议变体;<c>--</c> 之后不再解析。</summary>
    private static TerminalTransferProtocol OverrideProtocol(
        TerminalTransferProtocol protocol,
        ReadOnlySpan<string> arguments)
    {
        foreach (string argument in arguments)
        {
            if (argument == "--")
            {
                break;
            }
            protocol = argument switch
            {
                "-X" or "--xmodem" => TerminalTransferProtocol.XModem,
                "-Y" or "--ymodem" => TerminalTransferProtocol.YModem,
                "-Z" or "--zmodem" => TerminalTransferProtocol.ZModem,
                _ => protocol
            };
        }
        return protocol;
    }

    private static string BaseName(string token)
    {
        int cut = token.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? token : token[(cut + 1)..];
    }
}
