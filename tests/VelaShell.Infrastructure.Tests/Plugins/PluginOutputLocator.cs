namespace VelaShell.Infrastructure.Tests.Plugins;

/// <summary>
/// 找到某个插件<b>自己的</b>构建输出目录。
/// <para>
/// <b>为什么不能用 <c>typeof(某插件类).Assembly.Location</c> 的目录。</b>
/// 那拿到的是**本测试项目的 bin**,而每个插件项目都会把自己的 <c>plugin.json</c>
/// 复制到输出目录**根**下。本项目引了三个插件项目(HelloWorld / Sql / Telnet),
/// 于是 <c>bin/…/plugin.json</c> 到底是谁的,由 MSBuild 的复制顺序决定 —— 是一枚硬币。
/// </para>
/// <para>
/// 这不是假想:引入数据库插件之后,那份清单变成了 <c>velashell.sql</c>,
/// Telnet 的整链路用例于是把一份 SQL 清单铺进 <c>velashell-telnet/</c> 目录,
/// 报的是 <c>Sequence contains no matching element</c> ——
/// 一条完全指不到症结的错。在那之前它一直是绿的,只是**恰好**赢了那枚硬币。
/// </para>
/// <para>
/// 按路径去各插件自己的 bin 取,硬币就不存在了;顺带更贴近真实 ——
/// 铺开的是**发布时会进安装包的那棵目录树**,而不是一个混了测试宿主依赖的 bin。
/// </para>
/// </summary>
internal static class PluginOutputLocator
{
    /// <summary>
    /// 定位一个插件项目的输出目录。
    /// <para>配置与目标框架从本程序集的路径反推,于是 Debug / Release 都对得上。</para>
    /// </summary>
    /// <param name="projectName">插件项目名,例如 <c>VelaShell.Plugin.Sql</c>。</param>
    /// <returns>插件输出目录的绝对路径。</returns>
    public static string Locate(string projectName)
    {
        string here = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string targetFramework = Path.GetFileName(here);
        string configuration = Path.GetFileName(Path.GetDirectoryName(here)!);

        string? root = here;
        while (root is not null && !File.Exists(Path.Combine(root, "VelaShell.slnx")))
        {
            root = Directory.GetParent(root)?.FullName;
        }
        Assert.IsNotNull(root, "祖先目录里找不到 VelaShell.slnx。");

        string output = Path.Combine(root, "plugins", projectName, "bin", configuration, targetFramework);
        Assert.IsTrue(
            Directory.Exists(output),
            $"插件还没构建出来:{output}(本测试项目对它有构建顺序依赖,单独跑请先构建解决方案)");
        Assert.IsTrue(
            File.Exists(Path.Combine(output, "plugin.json")),
            $"插件构建产物里应有 plugin.json:{output}");
        return output;
    }

    /// <summary>
    /// 把一个插件的输出目录**整个**铺到目标目录下。
    /// <para>
    /// <b>整个复制,而不是挑几个文件名。</b> 数据库插件的驱动(SqlSugar、Npgsql、
    /// MySqlConnector、Microsoft.Data.SqlClient、Oracle.ManagedDataAccess、
    /// Microsoft.Data.Sqlite 与两个原生库)都不叫 <c>VelaShell.Plugin.Sql.*</c>,
    /// 挑文件名正好漏掉最关键的那一批 —— 测出来的会是"清单对了但一激活就炸"。
    /// </para>
    /// </summary>
    /// <param name="projectName">插件项目名。</param>
    /// <param name="target">目标目录(会被创建)。</param>
    public static void StageInto(string projectName, string target)
    {
        string source = Locate(projectName);
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}
