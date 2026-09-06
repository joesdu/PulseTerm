namespace VelaShell.Services;

/// <summary>
/// 重名让路:<c>file.txt</c> → <c>file (1).txt</c> → <c>file (2).txt</c> …
/// </summary>
/// <remarks>
/// <para>
/// 从 <c>FileBrowserViewModel</c> 拆出来的一簇(Q-01)。本地下载与远端上传各写过一遍
/// 同样的拆名与递增,一处用 <see cref="Path" /> 一处手撸 —— 两边对边缘输入的判断本来
/// 就不一定一致,而"下载下来的文件名跟服务器上不一样"是一类很难被想到去查的现象。
/// </para>
/// <para>
/// 只出候选名,<b>不判断存不存在</b>:本地要查文件系统、远端要查预列举的目录名单
/// 或发一次 <c>ExistsAsync</c>,那是调用方的事。
/// </para>
/// </remarks>
public static class UniqueNames
{
    /// <summary>候选序号的上限;到顶仍未找到空位就放弃让路。</summary>
    /// <remarks>
    /// 一万个同名文件时继续找下去只是把界面卡住;那种目录本身已经出了别的问题。
    /// </remarks>
    public const int MaxAttempts = 10_000;

    /// <summary>
    /// 把文件名拆成主干与扩展名。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 取<b>最后一个</b>点:<c>archive.tar.gz</c> 拆成 <c>archive.tar</c> + <c>.gz</c>,
    /// 让路之后是 <c>archive.tar (1).gz</c> —— 与用户在资源管理器里看到的习惯一致。
    /// </para>
    /// <para>
    /// <b>点在开头不算扩展名</b>(<c>dot &gt; 0</c>):<c>.bashrc</c> 整个是主干,
    /// 让路之后是 <c>.bashrc (1)</c>,而不是 <c>(1).bashrc</c>。
    /// </para>
    /// <para>
    /// 边缘情形 <c>file.</c> 会拆成 <c>file</c> + <c>.</c>(尾点归给扩展名)。
    /// 这与 <see cref="Path.GetExtension(string)" /> 的结果不同,采的是原先远端那一侧的口径 ——
    /// Windows 本身会把尾点吃掉,这种名字只可能来自远端,按远端口径处理更一致。
    /// </para>
    /// </remarks>
    /// <param name="name">文件名(不含目录)。</param>
    /// <returns>主干与扩展名(扩展名含前导点,没有则为空串)。</returns>
    public static (string Stem, string Extension) SplitExtension(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int dot = name.LastIndexOf('.');
        return dot > 0 ? (name[..dot], name[dot..]) : (name, string.Empty);
    }

    /// <summary>
    /// 依次产出带序号的候选名(不含目录)。
    /// </summary>
    /// <param name="name">原文件名。</param>
    /// <returns>候选名序列,最多 <see cref="MaxAttempts" /> 个。</returns>
    public static IEnumerable<string> Candidates(string name)
    {
        (string stem, string extension) = SplitExtension(name);
        for (int i = 1; i < MaxAttempts; i++)
        {
            yield return $"{stem} ({i}){extension}";
        }
    }
}
