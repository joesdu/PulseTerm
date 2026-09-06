namespace VelaShell.Core.Models;

/// <summary>
/// 终端支持的文本编码清单 —— 设置页的下拉与状态栏的热切菜单共用同一张表。
/// </summary>
/// <remarks>
/// 两处各抄一份的话,状态栏迟早会少掉设置页新加的那一个,而"设置里选得到、状态栏里切不了"
/// 是很难被发现的不一致。
/// <para>
/// 这些编码里除 UTF-8 与 ISO-8859-1 之外都在旧代码页里,需要
/// <c>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)</c> 之后才取得到
/// (<c>Program.Main</c> 已经注册)。
/// </para>
/// </remarks>
public static class TerminalEncodings
{
    /// <summary>可选编码,按常用度排序。</summary>
    public static string[] All { get; } =
        ["UTF-8", "GBK", "GB18030", "Big5", "Shift_JIS", "EUC-KR", "ISO-8859-1"];
}
