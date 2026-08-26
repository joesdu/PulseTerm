namespace VelaShell.Core.FileTransfer.Model;

/// <summary>终端内文件传输的方向。</summary>
public enum FileTransferDirection
{
    /// <summary>接收:远端发文件到本地(<c>sz</c> / <c>sb</c> / <c>sx</c>)。</summary>
    Receive,

    /// <summary>发送:本地上传文件到远端(<c>rz</c> / <c>rb</c> / <c>rx</c>)。</summary>
    Send
}

/// <summary>一次会话 / 单个文件的传输状态。</summary>
public enum FileTransferState
{
    /// <summary>尚未开始。</summary>
    Pending,

    /// <summary>正在传输。</summary>
    Transferring,

    /// <summary>已成功完成。</summary>
    Completed,

    /// <summary>被跳过(接收方拒收或文件已存在)。</summary>
    Skipped,

    /// <summary>失败(CRC 反复失败、IO 错误、协议错误)。</summary>
    Failed,

    /// <summary>被取消(用户中止或收到取消序列)。</summary>
    Cancelled
}

/// <summary>接收方对发送方所提供文件的处置决定。</summary>
public enum TransferFileDisposition
{
    /// <summary>从头接收该文件。</summary>
    Accept,

    /// <summary>跳过该文件(ZMODEM 回 ZSKIP;XMODEM/YMODEM 无跳过语义,退化为中止)。</summary>
    Skip,

    /// <summary>中止整个会话。</summary>
    Abort
}

/// <summary>终端内可用的文件传输协议。</summary>
public enum TerminalTransferProtocol
{
    /// <summary>ZMODEM:自动启动、支持批量与断点续传,与 lrzsz <c>sz</c>/<c>rz</c> 互操作。</summary>
    ZModem,

    /// <summary>XMODEM:128 字节块 + CRC16,单文件、无文件名,与 <c>sx</c>/<c>rx</c> 互操作。</summary>
    XModem,

    /// <summary>XMODEM-1K:同 XMODEM,但数据块为 1024 字节(STX 引导)。</summary>
    XModem1K,

    /// <summary>YMODEM(批量):1K 块 + 0 号块携带文件名/大小,与 <c>sb</c>/<c>rb</c> 互操作。</summary>
    YModem,

    /// <summary>YMODEM-G:YMODEM 的流式变体,不逐块应答,依赖无错链路(SSH 天然满足)。</summary>
    YModemG
}
