namespace VelaShell.Terminal.Emulation;

/// <summary>
/// <see cref="VtParser" /> 所产生的事件的接收端(sink)。<see cref="TerminalEmulator" />
/// 实现此接口,把解析出的转义序列转化为对屏幕缓冲区的修改。
/// </summary>
public interface IVtActions
{
    /// <summary>应在光标处写入一个可打印的 Unicode 标量值。</summary>
    void Print(int rune);

    /// <summary>
    /// 应在光标处连续写入一整段可打印 ASCII(<c>0x20</c>–<c>0x7E</c>)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 语义必须与逐个调用 <see cref="Print" /> 完全一致 —— 它存在的唯一理由是省掉那趟
    /// 逐字符分发:纯文本洪流(编译日志、<c>cat</c> 大文件)里每个字符都要走一遍
    /// 状态机分发 + 字符集映射 + 宽度判定 + 待换行判定。xterm.js 与 Windows Terminal
    /// 都有这条快路径。
    /// </para>
    /// <para>
    /// <b>刻意不写成默认接口方法。</b>默认实现"就是循环调 <see cref="Print" />"看着很顺手,
    /// 但默认接口方法会被 NSubstitute/Castle 的代理拦下来返回 <c>default</c> ——
    /// 不会落到默认实现上。那样任何用替身实现本接口的测试都会静默丢字,
    /// 而且丢得毫无征兆(本仓在 <c>ISettingsService</c> 上已经踩过一次)。
    /// 实现方只有两个,各写一遍不亏。
    /// </para>
    /// </remarks>
    /// <param name="text">一段可打印 ASCII;不含控制字符、代理对与组合标记。</param>
    void PrintRun(ReadOnlySpan<char> text);

    /// <summary>应执行一个 C0 控制字符(0x00-0x1F)或 DEL。</summary>
    void Execute(char control);

    /// <summary>一个 <c>ESC</c> 序列:<c>ESC {中间字节} {终结字节}</c>(如 ESC ( B)。</summary>
    void EscDispatch(string intermediates, char final);

    /// <summary>
    /// 一个 CSI 序列:<c>CSI {前缀} {参数} {中间字节} {终结字节}</c>。
    /// <paramref name="prefix" /> 是私有标记(如 <c>?</c>、<c>&gt;</c>)或 '\0'。
    /// </summary>
    void CsiDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final);

    /// <summary>一个 OSC 序列。<paramref name="parameters" /> 为按 ';' 拆分后的载荷。</summary>
    void OscDispatch(IReadOnlyList<string> parameters);

    /// <summary>一个带有已收集字符串载荷的 DCS 序列。</summary>
    void DcsDispatch(char prefix, IReadOnlyList<int> parameters, string intermediates, char final, string data);
}
