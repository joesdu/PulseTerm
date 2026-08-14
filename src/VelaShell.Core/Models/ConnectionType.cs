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

    // 3 是退役值:曾短暂用于内建的 S3,后者已改由插件提供(见 docs/S3协议插件化设计.md)。
    // 不复用它,免得某位用户本地那条老配置被读成另一种协议。

    /// <summary>
    /// 由插件提供的远程文件协议(S3、WebDAV、…);具体是哪一种由
    /// <see cref="SessionProfile.PluginProtocolId" /> 决定,参数在
    /// <see cref="SessionProfile.PluginSettings" />。
    /// <para>
    /// 宿主对这些协议一无所知 —— 它只负责路由与界面,协议实现在插件里。
    /// 因此新增一种协议不需要动这个枚举。
    /// </para>
    /// </summary>
    Plugin = 4,
}
