using System.Text;

namespace VelaShell.Terminal.Emulation;

/// <summary>
/// 增量式字节流解码器,在多次喂入之间缓冲不完整的多字节序列,并对畸形输入替换为 U+FFFD。
/// 默认为 UTF-8,但也接受任意 <see cref="Encoding" />(如 GBK、Big5),从而在保持 UTF-8 为默认的同时
/// 使终端字符集可配置。
/// </summary>
public sealed class Utf8Sink(Encoding? encoding = null)
{
    private char[] _chars = new char[1024];
    private Decoder _decoder = CreateDecoder(encoding);

    /// <summary>
    /// 切换解码所用的字符编码,并重置解码器状态(丢弃任何未完成的多字节前缀)。
    /// </summary>
    public void SetEncoding(Encoding encoding) => _decoder = CreateDecoder(encoding);

    private static Decoder CreateDecoder(Encoding? encoding)
    {
        encoding ??= new UTF8Encoding(false, false);
        Decoder decoder = encoding.GetDecoder();
        decoder.Fallback = new DecoderReplacementFallback("�");
        return decoder;
    }

    /// <summary>
    /// 增量解码一段字节:跨调用缓存未完成的多字节序列,非法字节替换为 U+FFFD。
    /// </summary>
    public string Decode(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<char> decoded = DecodeSpan(bytes);
        return decoded.IsEmpty ? string.Empty : new(decoded);
    }

    /// <summary>
    /// 增量解码到内部复用缓冲,返回其只读视图(仅在下次 Decode/DecodeSpan 前有效)。
    /// 输出热路径(每帧一次)用它避免物化中间 string。
    /// </summary>
    public ReadOnlySpan<char> DecodeSpan(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return [];
        }
        int max = _decoder.GetCharCount(bytes, false);
        if (_chars.Length < max)
        {
            _chars = new char[max];
        }

        // 即使 max == 0(整段都是某个多字节字符的前半部分)也必须调用 GetChars:
        // GetCharCount 不改变解码器状态,早退会把这些字节整段丢掉,下一段解码成 U+FFFD
        // (网络分块恰好切在 CJK 字符中间时输出变 �,cat 中文文件偶发乱码的根因)。
        int written = _decoder.GetChars(bytes, _chars, false);
        return _chars.AsSpan(0, written);
    }

    /// <summary>
    /// 重置解码器状态,丢弃已缓存的部分多字节序列(例如流中断或重连后调用)。
    /// </summary>
    public void Reset() => _decoder.Reset();
}
