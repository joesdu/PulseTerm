using LiveMarkdown.Avalonia;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>
/// LiveMarkdown 三个可选节点扩展的一次性注册(Mermaid 图 / LaTeX 公式 / SVG 解码)。
/// <para>
/// 三处都是进程级静态状态,且 <see cref="MarkdownRenderer.ConfigurePipeline" /> 必须在
/// 第一个 <see cref="MarkdownRenderer" /> 实例化之前设好 —— 每个渲染器在构造时各自建一份
/// pipeline,晚注册的扩展对已建好的那份无效。故由 <see cref="ChatPanelView" /> 在
/// <c>InitializeComponent()</c> 之前调一次。
/// </para>
/// <para>
/// 顺带的必要副作用:这次调用把三个扩展程序集拉进插件 ALC。XAML 里的
/// <c>avares://LiveMarkdown.Avalonia.Mermaid/Styles.axaml</c> 靠"在已加载程序集里按名查找"
/// 定位,插件依赖不在装载方的探测路径上,没被引用过就找不到 —— 所以顺序不能反。
/// </para>
/// </summary>
internal static class MarkdownSetup
{
    private static bool _registered;

    /// <summary>幂等注册;只在 UI 线程调用(面板可能被开多次)。</summary>
    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }
        _registered = true;

        // Markdig 侧:识别 ```mermaid 围栏块;数学除 $..$ / $$..$$ 外补 \(..\) / \[..\]
        // —— 后者才是多数模型实际输出的定界符,标准 UseMathematics() 认不出来。
        MarkdownRenderer.ConfigurePipeline += pipeline => pipeline
            .UseMermaid()
            .UseExtendedMathematics();

        // 渲染侧:把解析出的块/内联映射成控件
        MarkdownNode.Edit(builder => builder
            .Register<MathInlineNode>()
            .Register<MathBlockNode>()
            .Register<MermaidBlockNode>());

        // 图片解码:SVG 优先,非 SVG 回落位图解码器
        AsyncImageLoader.DefaultDecoders = [SvgImageDecoder.Shared, DefaultBitmapDecoder.Shared];
    }
}
