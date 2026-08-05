using VelaShell.Core.ZModem.Abstractions;
using VelaShell.Core.ZModem.Model;

namespace VelaShell.Services.ZModem;

/// <summary>
/// 基于原生文件选择框的 ZMODEM 上传来源:远端跑 <c>rz</c> 时弹一次多选文件框,
/// 用户选中的文件按顺序发往远端。远端文件名只取纯文件名(不带本地目录),
/// 与 <c>sz</c> 的行为一致。由 <c>ZModemTerminalRouter</c> 每会话经 sourceFactory 新建一个实例。
/// </summary>
internal sealed class FileZModemFileSource(
    Func<bool, CancellationToken, Task<IReadOnlyList<string>>> pickFilesAsync) : IZModemFileSource
{
    private readonly Func<bool, CancellationToken, Task<IReadOnlyList<string>>> _pickFilesAsync =
        pickFilesAsync ?? throw new ArgumentNullException(nameof(pickFilesAsync));

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ZModemOutgoingFile>> GetFilesAsync(CancellationToken cancellationToken)
    {
        // 第一个 bool 参数 = 是否为首次取消后的重试。
        IReadOnlyList<string> paths = await _pickFilesAsync(false, cancellationToken).ConfigureAwait(false);

        // 防误触:首次取消(未选任何文件)可能是不小心点了关闭/取消,再给一次机会;二次取消才真正中止。
        if (paths.Count == 0)
        {
            paths = await _pickFilesAsync(true, cancellationToken).ConfigureAwait(false);
        }

        var files = new List<ZModemOutgoingFile>(paths.Count);
        foreach (string path in paths)
        {
            try
            {
                FileInfo info = new(path);
                if (!info.Exists)
                {
                    continue;
                }
                files.Add(new(
                    info.FullName,
                    info.Name,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
            catch (Exception)
            {
                // 路径不可读(权限 / 已删除 / 被锁定):跳过,不拖垮整批。
                continue;
            }
        }
        return files;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(ZModemOutgoingFile file, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        // 引擎在 ZRPOS 续传时需要 Seek,故必须是可定位的文件流。
        Stream stream = new FileStream(
            file.LocalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }
}
