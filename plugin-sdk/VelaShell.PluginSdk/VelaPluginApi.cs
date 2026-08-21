namespace VelaShell.PluginSdk;

/// <summary>
/// 插件 API 的版本常量。apiLevel 是整数代际:宿主对同一 apiLevel 承诺只增不改不删
/// (接口方法、DTO 字段、清单 schema);破坏性变更才会提升 apiLevel。
/// 插件在 <c>plugin.json</c> 的 <c>apiLevel</c> 字段声明其编译目标代际,
/// 宿主拒绝加载高于自身代际的插件。
/// </summary>
public static class VelaPluginApi
{
    /// <summary>当前 SDK 的 apiLevel 代际。</summary>
    public const int Level = 1;

    /// <summary>
    /// 当前 SDK 的语义版本(<c>主.次.修订</c>)。
    /// <para>
    /// apiLevel 只在**破坏性**变更时才动,所以它管不住"只增不改"的那一半:
    /// SDK 1.1 给 <c>ExecResult</c> 加了标准错误与退出码、给远程执行加了流式形态,
    /// apiLevel 仍然是 1。一个用了这些新面的插件装到只带 1.0 的老宿主上,清单校验会放行,
    /// 然后在**运行期**炸出一个 <see cref="MissingMethodException" /> —— 那正是 apiLevel
    /// 当初要消灭的那种"看不懂的绑定异常",只是它太粗,拦不住这一档。
    /// </para>
    /// <para>
    /// 所以清单上多了一个 <c>minSdkVersion</c>:用到新面的插件声明它,老宿主在**发现期**
    /// 就干净地标 Incompatible 并说清该升级什么。
    /// </para>
    /// </summary>
    public const string SdkVersion = "1.1.0";
}
