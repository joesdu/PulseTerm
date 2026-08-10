namespace VelaShell.PluginSdk.Terminal;

/// <summary>终端缓冲区中的一处搜索命中。</summary>
/// <param name="Line">缓冲区绝对行号(0 = 最旧的滚回行)。</param>
/// <param name="Text">该行完整文本。</param>
public sealed record TerminalMatch(int Line, string Text);

/// <summary>
/// 终端能力:读取/搜索会话的终端输出,以及(经用户授权)向终端回写输入。
/// 读取是**快照**(滚回 + 当前屏幕的行文本,不含颜色属性);
/// 回写走宿主既有的输入串行化队列,"如同用户键入" —— 绝不直写 SSH 流。
/// </summary>
public interface ITerminalApi
{
    /// <summary>读取终端缓冲区末尾至多 <paramref name="maxLines" /> 行(含滚回),按行拼接。</summary>
    Task<string> GetOutputAsync(string sessionId, int maxLines = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// 在终端缓冲区中搜索。<paramref name="isRegex" /> 为 false 时做大小写不敏感的
    /// 子串匹配;为 true 时按 .NET 正则(带 1s 超时,非法表达式抛 <see cref="ArgumentException" />)。
    /// </summary>
    Task<IReadOnlyList<TerminalMatch>> SearchOutputAsync(string sessionId, string pattern, bool isRegex = false,
        int maxMatches = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// 向终端回写输入(如同用户键入,单次 ≤4KB;换行才会执行命令)。
    /// **需要用户授权**:宿主弹窗给出 仅本次 / 本次运行期间 / 始终允许 / 拒绝 四种选择,
    /// "始终允许"按插件持久化;被拒绝抛 <see cref="PluginPermissionDeniedException" />。
    /// </summary>
    Task WriteAsync(string sessionId, string input, CancellationToken cancellationToken = default);
}
