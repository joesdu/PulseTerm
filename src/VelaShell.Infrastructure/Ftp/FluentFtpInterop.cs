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
            _ => ex,
        };
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
