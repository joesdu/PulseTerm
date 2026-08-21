using VelaShell.Plugin.Sql;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>
/// 「哪一档有端点」这条声明本身。
/// <para>
/// 背景是一次实拍的缺陷:选了 SQLite 之后,主机那一栏已经改标成"数据库文件"、
/// 用户名与口令两栏也按 <see cref="WorkspaceFeatures.NoCredentials" /> 收起来了,
/// <b>唯独端口框还在,里面躺着上一个方言留下的 55432</b>。SQLite 是磁盘上的一个文件,
/// 拼连接串时压根不看端口 —— 那一栏留着只会让用户以为它有意义。
/// </para>
/// <para>
/// 这条守的是插件这一侧的**声明**;宿主是否真的把那一栏收起来由
/// <c>ConnectionProfileViewModelTests</c> 守。两层都要验:只验一层的话,
/// 另一层漏了照样是"对着一个填了没用的框发呆"。
/// </para>
/// </summary>
[TestClass]
public sealed class SqlNoPortVariantTests
{
    /// <summary>
    /// 文件型方言声明 <see cref="WorkspaceFeatures.NoEndpoint" />,其余四种一个都不许声明。
    /// <para>
    /// 反面那半条是关键:少了它,把这一位无脑加到所有变体上("一律收起端口")
    /// 也能让正面那半条通过,而那会让 MySQL 用户没地方改 13306 这类容器映射端口。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 只有文件型方言声明没有端点()
    {
        WorkspaceDescriptor descriptor = SqlPlugin.Describe(new("zh-Hans"));

        foreach (SqlDialectInfo info in SqlDialects.All)
        {
            WorkspaceVariant variant = descriptor.Variants.Single(v => v.Value == SqlDialects.VariantValue(info));
            Assert.AreEqual(
                info.IsFileBased,
                variant.Features!.Value.HasFlag(WorkspaceFeatures.NoEndpoint),
                $"{info.DisplayName} 这一档的「有没有端点」判错了。");
        }
    }

    /// <summary>
    /// 收起端口那一栏,不等于端口的**取值**可以不合法。
    /// <para>
    /// 宿主的保存/连接按钮有一条"端口在 1–65535 内"的判定,它不看这一位。
    /// 文件型方言因此仍要给出占位端口(<see cref="SqlDialectInfo.FilePlaceholderPort" />)——
    /// 给 0 的话,用户选完 SQLite 会发现三个按钮一起灰死,而界面上连端口框都看不见,
    /// 根本无从下手。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 没有端点的那一档仍给出合法的占位端口()
    {
        WorkspaceDescriptor descriptor = SqlPlugin.Describe(new("en"));

        foreach (WorkspaceVariant variant in descriptor.Variants)
        {
            Assert.IsTrue(
                variant.DefaultPort is >= 1 and <= 65535,
                $"变体 {variant.Value} 的默认端口 {variant.DefaultPort} 出了宿主允许的区间。");
        }
    }

    /// <summary>
    /// <b>没有端点 ≠ 没有主机那一栏。</b>
    /// <para>
    /// 文件路径正是填在"主机"那一格里(标签由变体改写成"数据库文件")。
    /// 哪天有人顺手让这一位把两栏一起收掉,用户就没有地方填文件了 —— 这条替那种改法守着。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 没有端点的那一档主机栏还在且改了标签()
    {
        WorkspaceVariant variant = SqlPlugin.Describe(new("zh-Hans"))
            .Variants
            .Single(v => v.Value == SqlDialects.VariantValue(SqlDialects.Of(SqlDialect.Sqlite)));

        Assert.IsTrue(variant.Features!.Value.HasFlag(WorkspaceFeatures.NoEndpoint));
        Assert.IsFalse(
            string.IsNullOrEmpty(variant.HostLabel),
            "文件路径填在\"主机\"那一格里,标签必须改写 —— 这一栏不能跟着端口一起收。");
    }
}
