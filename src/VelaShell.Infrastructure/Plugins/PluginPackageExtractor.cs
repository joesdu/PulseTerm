using System.Buffers;
using System.IO.Compression;

namespace VelaShell.Infrastructure.Plugins;

/// <summary>
/// 把插件包的 zip 载荷解到目标目录,并挡住三类恶意包。
/// </summary>
/// <remarks>
/// <para>
/// 从 <see cref="PluginManager" /> 拆出来的一簇(Q-01)。这是**安全边界**上的代码:
/// 挡的是路径逃逸(zip-slip)、解压炸弹、条目数轰炸。混在 2600 行的管理器里时,
/// 想单独验一条"谎报长度的炸弹包会被拦住"就得走完整条安装流程 ——
/// 而要造出触发上限的夹具,那条路上得先有一个合法签名的 <c>.vpx</c> 容器。
/// </para>
/// <para>
/// 三道闸各挡各的,少一道都不行:条目数闸挡"一百万个空文件"(每个都要建目录项),
/// 字节预算挡"一个条目吐十 GB",路径校验挡"写到目标目录之外去"。
/// </para>
/// </remarks>
public static class PluginPackageExtractor
{
    /// <summary>单包条目数上限。</summary>
    public const int MaxPackageEntries = 10_000;

    /// <summary>单包解压后总字节上限(解压炸弹防护:压缩比可以做到上千倍)。</summary>
    public const long MaxUnpackedBytes = 512L * 1024 * 1024;

    /// <summary>
    /// 解压到目标目录。
    /// </summary>
    /// <param name="archive">已打开的 zip 载荷。</param>
    /// <param name="destination">目标目录(不存在会按需创建)。</param>
    /// <exception cref="InvalidOperationException">
    /// 条目数超限、解压后体积超限,或某个条目试图写到目标目录之外。
    /// </exception>
    public static void ExtractSafely(ZipArchive archive, string destination)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string root = Path.GetFullPath(destination + Path.DirectorySeparatorChar);
        if (archive.Entries.Count > MaxPackageEntries)
        {
            throw new InvalidOperationException(
                $"Rejected package: it has {archive.Entries.Count} entries (limit {MaxPackageEntries}).");
        }
        long budget = MaxUnpackedBytes;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string targetPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!targetPath.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Rejected unsafe package entry (path escape): {entry.FullName}");
            }
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using Stream source = entry.Open();
            using FileStream target = File.Create(targetPath);
            // 按实际写出的字节数记账,而不是信 entry.Length —— 中央目录里的长度是包自己写的,
            // 炸弹包大可以谎报 1 KB 再吐出 10 GB。
            budget -= CopyBounded(source, target, budget, entry.FullName);
        }
    }

    /// <summary>把条目内容拷进目标流,超出预算即中止并抛出。返回实际写出的字节数。</summary>
    /// <remarks>本方法按 zip 条目逐个调用,缓冲走池:上千条目的包不必扔掉上千个 80 KB 数组。</remarks>
    private static long CopyBounded(Stream source, Stream destination, long budget, string entryName)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long written = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > budget)
                {
                    throw new InvalidOperationException(
                        $"Rejected package: unpacked size exceeds {MaxUnpackedBytes} bytes (while extracting '{entryName}').");
                }
                destination.Write(buffer, 0, read);
            }
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
