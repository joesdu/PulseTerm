using System.Text;

namespace VelaShell.Services;

/// <summary>
/// 判定一份文本文件该用什么编码解码 —— 内置远程编辑器的"别把文件改坏"防线。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能只认 BOM。</b>原先的做法是:有 BOM 按 BOM,否则一律当 UTF-8。
/// 而 UTF-8 的解码器默认用**替换回退** —— 遇到非法字节序列不报错,悄悄换成 U+FFFD。
/// 于是打开一个 GBK 文件时,每一个中文字都变成 �,用户随手一存,
/// <b>原文件就此永久损坏</b>,而且整个过程连一句提示都没有。
/// </para>
/// <para>
/// 现在改为:BOM 优先 → 否则用**严格** UTF-8 试解(非法序列直接抛)→ 失败就回落到
/// 会话编码(用户在设置/连接里选的那个,GBK / Big5 / Shift_JIS…)。
/// 判定不出来时宁可回落到会话编码,也不要用会静默毁字的解码器。
/// </para>
/// </remarks>
public static class EditorEncodingDetector
{
    /// <summary>判定结果。</summary>
    /// <param name="Encoding">应当使用的编码。</param>
    /// <param name="PreambleLength">要跳过的 BOM 字节数。</param>
    /// <param name="FellBackToSessionEncoding">是否因为 UTF-8 严格解码失败而回落到了会话编码。</param>
    public readonly record struct Result(Encoding Encoding, int PreambleLength, bool FellBackToSessionEncoding);

    /// <summary>
    /// 判定给定字节该用什么编码解码。
    /// </summary>
    /// <param name="bytes">文件内容。</param>
    /// <param name="sessionEncoding">UTF-8 解不通时的回落编码(当前会话的终端编码);null 视为 UTF-8。</param>
    /// <returns>判定结果。</returns>
    public static Result Detect(byte[] bytes, Encoding? sessionEncoding)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        // ① BOM 是明确声明,优先级最高。
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return new(new UTF8Encoding(true), 3, false);
        }
        if (bytes is [0xFF, 0xFE, ..])
        {
            return new(Encoding.Unicode, 2, false);
        }
        if (bytes is [0xFE, 0xFF, ..])
        {
            return new(Encoding.BigEndianUnicode, 2, false);
        }

        // ② 没有 BOM:用严格 UTF-8 试一次。合法就是 UTF-8 —— 这个判定几乎不会误判,
        //    因为多字节的 GBK / Big5 / Shift_JIS 文本撞上合法 UTF-8 序列的概率极低。
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        try
        {
            strict.GetString(bytes);
            return new(new UTF8Encoding(false), 0, false);
        }
        catch (DecoderFallbackException)
        {
            // ③ 不是合法 UTF-8:按会话编码解。这正是 GBK / Big5 文件走的路。
            return new(sessionEncoding ?? new UTF8Encoding(false), 0, sessionEncoding is not null);
        }
    }

    /// <summary>按判定结果把字节解成文本(跳过 BOM)。</summary>
    /// <param name="bytes">文件内容。</param>
    /// <param name="result">判定结果。</param>
    /// <returns>解码后的文本。</returns>
    public static string Decode(byte[] bytes, Result result)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        int skip = Math.Min(result.PreambleLength, bytes.Length);
        return result.Encoding.GetString(bytes, skip, bytes.Length - skip);
    }
}
