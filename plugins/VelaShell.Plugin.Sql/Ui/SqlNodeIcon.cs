using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VelaShell.Plugin.Sql.Ui;

/// <summary>
/// 把 <see cref="SqlNodeKind" /> 换成对象树上那个图标的路径数据。
/// <para>
/// <b>为什么是转换器而不是节点上的一个 <c>Geometry</c> 属性</b>:图标是**宿主主题字典**里的
/// 资源(<c>Icon.*</c>,与 Redis 插件同一套 lucide 图集),取它要碰
/// <c>Application.Current.Resources</c> —— 那是界面层的东西。让 <see cref="SqlTreeNode" />
/// 去碰它,节点就再也不能在没有 <c>Application</c> 的纯离线单测里构造了,
/// 而树的判定用例(双击、右键、系统分组)恰恰全是那一类。
/// </para>
/// <para>
/// 取不到资源时返回 <see langword="null" />:<c>Path</c> 的 <c>Data</c> 为空就是不画,
/// 树照常可用。**图标缺失不该是一条异常** —— 宿主换一版图集不该让插件的树打不开。
/// </para>
/// </summary>
public sealed class SqlNodeIconConverter : IValueConverter
{
    /// <summary>共用一份实例(AXAML 里 <c>StaticResource</c> 引它)。</summary>
    public static SqlNodeIconConverter Instance { get; } = new();

    /// <summary>
    /// "当前库 / 当前 schema" → 字重。<see langword="true" /> 加粗,其余常规。
    /// <para>
    /// 挂在这个类上而不是另开一个文件:它只服务对象树这一处,
    /// 而"节点长什么样"这件事本来就聚在这里。
    /// </para>
    /// </summary>
    public static IValueConverter Weight { get; } = new FuncValueConverter<bool, FontWeight>(
        current => current ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>
    /// 每种节点用哪个 lucide 图标。
    /// <para>
    /// 挑选原则是**同级之间必须一眼分得开**,而不是"像不像":
    /// 表与视图并排出现,所以一个用网格、一个用眼睛;物化视图是"存下来的视图",用叠层;
    /// 存储过程与函数并排,所以一个用终端、一个用闪电。
    /// </para>
    /// </summary>
    private static readonly Dictionary<SqlNodeKind, string> Icons = new()
    {
        [SqlNodeKind.Database] = "hard-drive",
        [SqlNodeKind.Schema] = "folder",
        [SqlNodeKind.Category] = "folder-open",
        [SqlNodeKind.SystemGroup] = "settings",
        [SqlNodeKind.Table] = "grid-3x3",
        [SqlNodeKind.View] = "eye",
        [SqlNodeKind.MaterializedView] = "layers",
        [SqlNodeKind.Procedure] = "terminal",
        [SqlNodeKind.Function] = "zap",
        [SqlNodeKind.Sequence] = "list-ordered",
        [SqlNodeKind.Column] = "columns-2",
        [SqlNodeKind.Error] = "circle-alert"
    };

    /// <summary>某种节点对应的资源键;没有图标时为 <see langword="null" />。</summary>
    /// <param name="kind">节点类别。</param>
    /// <returns>资源键(如 <c>Icon.grid-3x3</c>)。</returns>
    public static string? ResourceKeyOf(SqlNodeKind kind) =>
        Icons.TryGetValue(kind, out string? name) ? $"Icon.{name}" : null;

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not SqlNodeKind kind || ResourceKeyOf(kind) is not { } key)
        {
            return null;
        }
        if (Application.Current is not { } app)
        {
            return null;
        }
        return app.TryFindResource(key, out object? resource) ? resource as Geometry : null;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("图标是只读的。");
}
