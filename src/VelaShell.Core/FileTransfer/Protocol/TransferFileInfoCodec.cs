using System.Globalization;
using System.Text;
using VelaShell.Core.FileTransfer.Model;

namespace VelaShell.Core.FileTransfer.Protocol;

/// <summary>
/// 文件信息字段的编解码。ZMODEM 的 ZFILE 数据子包与 YMODEM 的 0 号块用的是同一种格式
/// (Chuck Forsberg 定义,lrzsz 的 <c>sz</c> 与 <c>sb</c> 共用同一段代码生成):
/// <c>文件名 NUL 大小 修改时间(八进制) 模式(八进制) 串行 剩余文件数 剩余字节数 NUL</c>。
/// 两个协议共用这一份实现,避免两边各写一遍再各自跑偏。
/// </summary>
public static class TransferFileInfoCodec
{
    /// <summary>ZFILE / YMODEM 0 号块声明的默认 Unix 权限(八进制 0644)。</summary>
    private const int DefaultUnixMode = 0b110_100_100;

    // 严格 UTF-8 解码器:遇到非法序列抛异常而不是替换成 U+FFFD,以便干净地回退到 Latin1。
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// 编码文件信息字段(不含块 / 子包封装)。文件名按 UTF-8 上链 —— 该字段是裸字节、
    /// 不带编码声明,而 Linux 文件名本身就是字节串,UTF-8 是现代系统的实际编码。
    /// </summary>
    /// <param name="fileName">纯文件名(不含目录)。</param>
    /// <param name="size">文件字节数。</param>
    /// <param name="modifiedUtc">修改时间;<c>null</c> 表示不声明(写 0)。</param>
    /// <param name="filesRemaining">本批中当前文件之后还剩几个。</param>
    /// <param name="bytesRemaining">本批中当前文件之后还剩多少字节。</param>
    /// <returns>可直接放进 ZFILE 子包 / YMODEM 0 号块的字节。</returns>
    public static byte[] Encode(
        string fileName,
        long size,
        DateTimeOffset? modifiedUtc,
        int filesRemaining,
        long bytesRemaining)
    {
        ArgumentNullException.ThrowIfNull(fileName);

        var info = new List<byte>(128);
        info.AddRange(Encoding.UTF8.GetBytes(fileName));
        info.Add(0);

        long mtime = modifiedUtc?.ToUnixTimeSeconds() ?? 0;
        string meta = string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2} 0 {3} {4}",
            size,
            Convert.ToString(mtime, 8),
            Convert.ToString(DefaultUnixMode, 8),
            filesRemaining,
            bytesRemaining);
        info.AddRange(Encoding.ASCII.GetBytes(meta));
        info.Add(0);
        return [.. info];
    }

    /// <summary>
    /// 解析文件信息字段:首段为 NUL 结尾的文件名,其后是以空格分隔的可选字段
    /// (大小、修改时间(八进制)、模式(八进制)、串行号、批中剩余文件数、剩余字节数)。
    /// </summary>
    /// <param name="data">文件信息字段的原始字节。</param>
    /// <returns>解析出的文件元数据。</returns>
    public static TransferFileMetadata Parse(ReadOnlySpan<byte> data)
    {
        int nul = data.IndexOf((byte)0);
        string fileName;
        string rest;
        if (nul < 0)
        {
            // 无 NUL:整段作为文件名(异常发送方的兜底)。
            fileName = DecodeFileName(data);
            rest = string.Empty;
        }
        else
        {
            fileName = DecodeFileName(data[..nul]);
            ReadOnlySpan<byte> tail = data[(nul + 1)..];
            // 元数据段以 NUL 结尾;取到下一个 NUL 或段尾。
            int restEnd = tail.IndexOf((byte)0);
            if (restEnd < 0)
            {
                restEnd = tail.Length;
            }
            rest = Encoding.ASCII.GetString(tail[..restEnd]).Trim();
        }

        long? size = null;
        DateTimeOffset? modified = null;
        int? mode = null;
        int? filesRemaining = null;

        if (rest.Length > 0)
        {
            string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sz))
            {
                size = sz;
            }
            if (parts.Length > 1 && TryParseOctal(parts[1], out long mtime) && mtime > 0)
            {
                modified = DateTimeOffset.FromUnixTimeSeconds(mtime);
            }
            if (parts.Length > 2 && TryParseOctal(parts[2], out long m))
            {
                mode = (int)(m & 0xFFFF);
            }
            if (parts.Length > 4 && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fr))
            {
                filesRemaining = fr;
            }
        }

        return new TransferFileMetadata
        {
            FileName = fileName,
            Size = size,
            ModifiedUtc = modified,
            UnixMode = mode,
            FilesRemaining = filesRemaining,
            RawMetadata = rest.Length > 0 ? rest : null
        };
    }

    private static bool TryParseOctal(string value, out long result)
    {
        result = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        foreach (char c in value)
        {
            if (c is < '0' or > '7')
            {
                return false;
            }
            result = (result << 3) + (c - '0');
        }
        return true;
    }

    /// <summary>
    /// 解码文件名字节。该字段是裸字节、不带编码声明,而现代 Linux 的文件名事实上就是 UTF-8,
    /// <c>sz 中文名.txt</c> 上链的正是 UTF-8 字节 —— 一律按 Latin1 解会把它变成 "ä¸­æ..."
    /// 这样的乱码并原样落盘。故先按严格 UTF-8 试解,失败(说明对端用的是 GBK 等其它编码)
    /// 再回退 Latin1 的字节保真解码,至少不丢信息。
    /// </summary>
    private static string DecodeFileName(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
