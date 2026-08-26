using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Core.FileTransfer.Abstractions;

/// <summary>
/// 终端内文件传输的进度观察者:引擎在会话 / 文件生命周期各节点回调,供 UI 更新进度。
/// 回调可能发生在后台线程,实现方需自行 marshal 到 UI 线程(默认实现全部为空,便于选择性覆盖)。
/// </summary>
public interface IFileTransferObserver
{
    /// <summary>会话开始(首帧握手完成)。</summary>
    /// <param name="session">当前会话。</param>
    void OnSessionStarted(FileTransferSession session) { }

    /// <summary>一个文件开始传输。</summary>
    /// <param name="item">该文件项。</param>
    void OnFileStarted(FileTransferItem item) { }

    /// <summary>文件进度更新(已传输字节增加)。</summary>
    /// <param name="item">该文件项。</param>
    void OnFileProgress(FileTransferItem item) { }

    /// <summary>一个文件成功完成。</summary>
    /// <param name="item">该文件项。</param>
    void OnFileCompleted(FileTransferItem item) { }

    /// <summary>一个文件被跳过。</summary>
    /// <param name="item">该文件项。</param>
    void OnFileSkipped(FileTransferItem item) { }

    /// <summary>整个会话成功完成。</summary>
    /// <param name="session">当前会话。</param>
    void OnSessionCompleted(FileTransferSession session) { }

    /// <summary>会话失败或被取消。</summary>
    /// <param name="session">当前会话。</param>
    /// <param name="error">导致失败的异常;主动取消时可为 <c>null</c>。</param>
    void OnSessionFailed(FileTransferSession session, Exception? error) { }
}
