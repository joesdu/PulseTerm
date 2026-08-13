namespace VelaShell.Core.Models;

/// <summary>连接配置使用的协议类型。</summary>
public enum ConnectionType
{
    /// <summary>SSH 终端连接,也是历史数据的默认值。</summary>
    SSH = 0,

    /// <summary>SFTP 文件连接。</summary>
    SFTP = 1,

    /// <summary>FTP / FTPS 文件连接;是否加密由 <see cref="FtpSettings.EncryptionMode" /> 决定。</summary>
    FTP = 2,
}
