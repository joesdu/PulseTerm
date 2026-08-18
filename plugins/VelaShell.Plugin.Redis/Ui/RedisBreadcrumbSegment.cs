namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// 面包屑上的一段。点它 = 把过滤条设成 <see cref="Prefix" /> 并重扫。
/// <para>
/// 之所以让下钻走过滤条而不是另存一份"当前路径":两套状态一定会打架,而过滤条
/// 底下那行"真正要发的命令"回显只认得其中一个 —— 用户就会看到列表和回显各说各话。
/// </para>
/// </summary>
/// <param name="Label">这一段的文本(如 <c>user</c>)。</param>
/// <param name="Prefix">从根到这一段的完整前缀,含结尾分隔符(如 <c>demo:user:</c>)。</param>
public sealed record RedisBreadcrumbSegment(string Label, string Prefix);
