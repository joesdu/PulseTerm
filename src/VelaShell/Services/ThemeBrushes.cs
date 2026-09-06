using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace VelaShell.Services;

/// <summary>
/// 从应用资源里按令牌名取画刷,给那些**不是控件**、拿不到 <c>this.TryFindResource</c>
/// 的地方用(静态配色服务、值转换器)。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由:DESIGN.md 定的规则是"颜色一律由种子色派生的令牌给",而 C# 里几处
/// 静态配色服务原先写死了 Dracula 色值 —— 切到 Sakura / GitHub Light 这类亮色主题后,
/// 树上的状态圆点、标签强调条、同步通道徽章仍是暗色系配色,而且完全绕过了
/// <c>UiThemeCatalogTests</c> 的对比度尺子。
/// </para>
/// <para>
/// <b>每次调用现取,不缓存。</b>令牌值会随主题切换整体替换(见 <c>ThemeTokenApplier</c>),
/// 缓存住就等于把第一个主题的颜色钉死。取一次资源是字典查找,开销可以忽略。
/// </para>
/// <para>
/// 拿不到应用实例(无头单元测试、设计器)时回落到传入的默认色,并且回落用的是
/// <see cref="ImmutableSolidColorBrush" /> —— 可变的 <see cref="SolidColorBrush" />
/// 是 <c>AvaloniaObject</c>,带线程亲和性,谁先碰归谁,跨线程渲染会在合成阶段直接抛。
/// </para>
/// </remarks>
public static class ThemeBrushes
{
    /// <summary>按令牌名取画刷;取不到时用 <paramref name="fallback" /> 造一个不可变画刷。</summary>
    /// <param name="key">令牌名(如 <c>VelaStatusConnected</c>)。</param>
    /// <param name="fallback">取不到令牌时的兜底颜色。</param>
    /// <returns>画刷,永不为 null。</returns>
    public static IBrush Resolve(string key, Color fallback)
    {
        if (Application.Current is { } app
            && app.Resources.TryGetResource(key, app.ActualThemeVariant, out object? value)
            && value is IBrush brush)
        {
            return brush;
        }
        return new ImmutableSolidColorBrush(fallback);
    }
}
