using FluentFTP.Exceptions;
using VelaShell.Core.Ftp;

namespace VelaShell.Infrastructure.Ftp;

/// <summary>
/// FluentFTP 异常 → Core 的 <see cref="VelaFtpClientException" /> 族的一处翻译。
/// 与 SSH 侧的 <c>TmdsSshInterop</c> 同样的约定:具体库的异常类型不得越过 Infrastructure 边界
/// (见 docs/架构设计.md 的分层硬规则)。
/// </summary>
internal static class FluentFtpInterop
{
    /// <summary>把 FluentFTP/Socket 的异常翻译成库中立异常;已是中立异常的原样返回。</summary>
    public static Exception Translate(Exception ex, string operation)
    {
        return ex switch
        {
            VelaFtpClientException or OperationCanceledException => ex,
            FtpAuthenticationException auth => new VelaFtpAuthenticationException(
                                $"FTP login failed: {auth.CompletionCode} {auth.Message}", auth),
            FtpCommandException cmd => TranslateCommand(cmd, operation),
            FtpSecurityNotAvailableException tls => new VelaFtpConnectionException(
                                $"The server does not support the requested FTPS mode ({operation}).", tls),
            TimeoutException timeout => new VelaFtpConnectionException($"FTP {operation} timed out.", timeout),
            System.Net.Sockets.SocketException socket => new VelaFtpConnectionException($"FTP {operation} failed: {socket.Message}", socket),
            System.Security.Authentication.AuthenticationException tlsAuth => new VelaFtpConnectionException($"TLS handshake failed: {tlsAuth.Message}", tlsAuth),
            IOException io => new VelaFtpConnectionException($"FTP {operation} failed: {io.Message}", io),
            FtpException ftp => new VelaFtpOperationException($"FTP {operation} failed: {ftp.Message}", ftp),
            // FluentFTP 在**底层套接字已死**时会从内部抛出 NullReference / ObjectDisposed /
            // InvalidOperation —— 它并不总把断线包成 FtpException。不翻译的话,用户双击一个
            // 远端文件只会看到「Object reference not set to an instance of an object」
            // (实机反馈的「null 错误」就是它),既不知道发生了什么,也不知道该重连。
            NullReferenceException or ObjectDisposedException or InvalidOperationException =>
                new VelaFtpConnectionException(
                    $"FTP connection was lost during {operation}; reconnect and try again.", ex),
            _ => ex,
        };
    }

    /// <summary>该异常是否代表「连接已失效」——据此把会话标记为离线并丢弃池中的坏连接。</summary>
    public static bool IsConnectionLost(Exception ex) => ex is VelaFtpConnectionException;

    /// <summary>
    /// 服务器是不是在说「同一时刻只能有一个(连接/传输)」。
    /// <para>
    /// 用于把该会话就地收成单连接后重试一次 —— 用户报的现象是批量上传时
    /// 第一个文件成功、其余全失败(服务端只支持单线程上传)。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 各家服务器的措辞与应答码都不统一,所以两条线一起看:
    /// 应答码 421(服务不可用/用户数超限)、425(开不了数据连接)、450(文件/传输忙),
    /// 以及中英文关键词。判错的代价很小:无非是这一项退化成排队重试一次;
    /// 判漏的代价才大 —— 用户看到的是一批失败的传输。
    /// </remarks>
    public static bool IsConcurrencyRejection(Exception? ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is FtpCommandException { CompletionCode: "421" or "425" or "450" })
            {
                return true;
            }
            if (current.Message is { Length: > 0 } message && MentionsConcurrencyLimit(message))
            {
                return true;
            }
        }
        return false;
    }

    private static readonly string[] ConcurrencyKeywords =
    [
        "too many", "maximum number", "max clients", "connection limit", "already connected",
        "only one", "one transfer", "simultaneous", "concurrent", "busy", "try again later",
        "连接数", "同时", "并发", "超过最大", "已达上限",
    ];

    private static bool MentionsConcurrencyLimit(string message)
    {
        foreach (string keyword in ConcurrencyKeywords)
        {
            if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 按 FTP 应答码分类。5xx 里 550 既可能是「不存在」也可能是「没权限」,
    /// 服务器措辞不统一,因此再看一眼文本 —— 分不清时保守地归为一般操作失败。
    /// </summary>
    private static Exception TranslateCommand(FtpCommandException cmd, string operation)
    {
        string message = cmd.Message ?? string.Empty;
        bool denied = message.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
                      cmd.CompletionCode is "530" or "532";
        if (denied)
        {
            return new VelaFtpPermissionDeniedException($"FTP {operation} denied: {message}", cmd);
        }
        bool notFound = message.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
        return notFound
            ? new VelaFtpPathNotFoundException($"FTP {operation} failed: {message}", cmd)
            : new VelaFtpOperationException($"FTP {operation} failed: {message}", cmd);
    }
}
