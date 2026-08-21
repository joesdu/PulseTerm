using VelaShell.Plugin.Sql;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using static VelaShell.Plugin.Sql.SqlExceptionTranslator;

namespace VelaShell.Plugin.Sql.Tests;

/// <summary>方言登记表、文案表、连接表单声明这三件"数据"的完整性。</summary>
[TestClass]
public sealed class SqlContractTests
{
    /// <summary>
    /// 文案表:两种语言的键集必须齐平。
    /// <para>
    /// 字典初始化器里出现重复键会在**静态构造时**抛 —— 编译期看不出来,
    /// 而它一炸就是整个插件不可用。访问一次 <see cref="Loc.AllKeys" /> 就是这条的检查入口。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 文案表_两种语言键集一致且无重复键()
    {
        IReadOnlyCollection<string> all = Loc.AllKeys;
        IReadOnlyCollection<string> english = Loc.KeysOf(chinese: false);
        IReadOnlyCollection<string> chinese = Loc.KeysOf(chinese: true);

        Assert.AreNotEqual(0, all.Count);
        CollectionAssert.AreEquivalent(
            english.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            chinese.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            "中英文表的键集必须一一对应,否则某种语言下会显示键名。");
    }

    /// <summary>
    /// <b>源码里取过的每一个键,文案表里都得有。</b>
    /// <para>
    /// 这条是被真事故逼出来的:<see cref="Loc" /> 对不认识的键**原样返回键名**
    /// (这是刻意的——比抛异常温和),于是漏一个键的表现是<b>界面上出现一行
    /// <c>Sql_NoOpsForDialect</c></b>,而编译、单元测试、键集一致性检查**全都照样绿**:
    /// 中英两张表同时缺同一个键,它们仍然"一一对应"。
    /// </para>
    /// <para>
    /// 本轮这个坑踩了两次,一共漏过 4 个键(其中 <c>Sql_CommitLabel</c> / <c>Sql_CommitTooltip</c> /
    /// <c>Sql_RevertLabel</c> 已经绑在结果网格的提交/撤销按钮上——也就是说那两个按钮
    /// 当时显示的就是键名)。所以这条不查"键集一致",而是<b>直接扫源码</b>。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 文案表_源码里取过的键一个都不能少()
    {
        string root = FindSolutionRoot();
        string pluginDirectory = Path.Combine(root, "plugins", "VelaShell.Plugin.Sql");
        Assert.IsTrue(Directory.Exists(pluginDirectory), $"找不到插件源码目录:{pluginDirectory}");

        // _loc["X"] / loc["X"] / Format("X", ...) —— 三种取法覆盖了全部静态用法。
        // 动态拼的键(editability.ReasonKey、$"Sql_Env{枚举}")由别的用例守。
        var pattern = new System.Text.RegularExpressions.Regex(
            @"(?:_loc|loc|Localization)\[\s*""(Sql_\w+)""\s*\]|\.Format\(\s*""(Sql_\w+)""",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        HashSet<string> known = [.. Loc.AllKeys];
        SortedSet<string> missing = [];
        int scanned = 0;
        foreach (string file in Directory.EnumerateFiles(pluginDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "Loc.cs" || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            scanned++;
            foreach (System.Text.RegularExpressions.Match match in pattern.Matches(File.ReadAllText(file)))
            {
                string key = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (!known.Contains(key))
                {
                    missing.Add($"{key}({Path.GetFileName(file)})");
                }
            }
        }

        Assert.IsTrue(scanned > 10, $"只扫到 {scanned} 个源文件,路径多半找错了。");
        Assert.AreEqual(
            0, missing.Count,
            $"这些键在源码里取过但文案表里没有,界面上会直接显示键名:{string.Join(", ", missing)}");
    }

    private static string FindSolutionRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "VelaShell.slnx")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("找不到解决方案根目录(祖先目录里没有 VelaShell.slnx)。");
    }

    /// <summary>连接表单与面板用到的每一个键都必须收录 —— 漏一个就是界面上出现一个键名。</summary>
    [TestMethod]
    public void 文案表_覆盖全部方言的表单与面板用键()
    {
        var loc = new Loc("zh-Hans");
        HashSet<string> known = [.. Loc.AllKeys];

        foreach (ProtocolSettingField field in SqlSettings.Declare(loc))
        {
            AssertLocalized(known, field.Label);
            AssertLocalized(known, field.Hint);
            AssertLocalized(known, field.Placeholder);
            foreach (ProtocolSettingChoice choice in field.Choices)
            {
                AssertLocalized(known, choice.Label);
            }
        }
        // 连接类型名与变体里的标签改写也走文案表。
        WorkspaceDescriptor descriptor = SqlPlugin.Describe(loc);
        AssertLocalized(known, descriptor.DisplayName);
        foreach (WorkspaceVariant variant in descriptor.Variants)
        {
            AssertLocalized(known, variant.HostLabel);
            AssertLocalized(known, variant.HostPlaceholder);
        }

        // 面板与状态栏按 $"Sql_Env{枚举}" 拼键 —— 拼出来的键也得在表里。
        foreach (SqlEnvironment environment in Enum.GetValues<SqlEnvironment>())
        {
            Assert.IsTrue(known.Contains($"Sql_Env{environment}"), $"缺文案键 Sql_Env{environment}。");
        }
    }

    /// <summary>
    /// 一个没被翻译的键会原样返回自己 —— 于是"文案 == 键名"就是漏收录的信号。
    /// 只对看起来像键名的值(<c>Sql_</c> 前缀)判定,产品名之类的字面量不算。
    /// </summary>
    private static void AssertLocalized(HashSet<string> known, string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.StartsWith("Sql_", StringComparison.Ordinal))
        {
            return;
        }
        Assert.IsTrue(known.Contains(text), $"文案键 {text} 没有收录,界面上会直接显示这个键名。");
    }

    /// <summary>
    /// 工作台 id 与方言取值都落进用户的会话配置,**发布后不可更改**。
    /// 这条测试就是那句承诺的执行者:改动这几个字符串会让它立刻变红。
    /// <para>
    /// 五个方言现在共用一个 id(一个「数据库」页签),方言本身成了一个**设置值**。
    /// 于是"不可更名"这条约束从 id 转移到了那五个取值上 —— 它们仍然是原先 id 的后缀,
    /// 正因为那串字符本来就是为"落进配置且不再变"挑的。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 工作台id与方言取值_稳定不可更名()
    {
        // 先用局部变量接一手再断言。这三个都是 const,直接把常量喂给 Assert.AreEqual
        // 会被编译期折叠,分析器判成"恒真断言"(MSTEST0032)而报警告。
        // 但这条用例的价值恰恰在于把常量的**当前取值**钉死:谁改了名字,这里就得红一条 ——
        // 为了消警告把断言删掉,等于把这道闸门拆了。
        string workspaceId = SqlDialects.WorkspaceId;
        string pluginId = SqlDialects.PluginId;
        string dialectKey = SqlDialects.DialectKey;

        Assert.AreEqual("velashell.sql", workspaceId, "工作台 id 已落进用户配置,不能改。");
        // 宿主强制:连接类型 id 必须等于插件 id 或以 "<插件id>." 为前缀(防插件间冒名)。
        Assert.AreEqual(pluginId, workspaceId);
        Assert.AreEqual("dialect", dialectKey, "方言键已落进用户配置,不能改。");

        string[] expected = ["mysql", "postgresql", "sqlserver", "oracle", "sqlite"];
        string[] actual = [.. SqlDialects.All.Select(SqlDialects.VariantValue)];
        CollectionAssert.AreEqual(expected, actual, "方言取值已落进用户配置,不能改。");

        foreach (string value in actual)
        {
            Assert.IsNotNull(SqlDialects.ByVariantValue(value), "反查必须认得出自己发出去的取值。");
        }
        // 认不出的取值要回落而不是抛 —— 用户配置可能是手改的、或来自别的版本。
        Assert.IsNull(SqlDialects.ByVariantValue("mariadb"));
    }

    /// <summary>
    /// <b>一个页签,五个变体。</b>
    /// <para>
    /// 每个方言的默认端口必须由变体给出 —— 少一条,选到那个方言时端口就停在别人的默认值上
    /// (选了 PostgreSQL 而端口框里写着 MySQL 的 3306)。
    /// </para>
    /// </summary>
    [TestMethod]
    public void 连接类型_一个页签五个变体()
    {
        WorkspaceDescriptor descriptor = SqlPlugin.Describe(new("zh-Hans"));

        Assert.AreEqual(SqlDialects.WorkspaceId, descriptor.Id);
        Assert.AreEqual(SqlDialects.DialectKey, descriptor.VariantKey);
        Assert.AreEqual(SqlDialects.All.Count, descriptor.Variants.Count);

        foreach (SqlDialectInfo info in SqlDialects.All)
        {
            WorkspaceVariant variant = descriptor.Variants
                .Single(v => v.Value == SqlDialects.VariantValue(info));
            Assert.AreEqual(info.DefaultPort, variant.DefaultPort, $"{info.DisplayName} 的默认端口没跟着变体走。");
        }

        // 变体挑选走的是描述符自己的解析,不是测试里另写一份。
        Assert.AreEqual(
            "postgresql",
            descriptor.ResolveVariant(key => key == SqlDialects.DialectKey ? "postgresql" : null)?.Value);
        Assert.IsNull(descriptor.ResolveVariant(_ => "mariadb"), "认不出的取值不该硬套一条变体。");
    }

    /// <summary>默认端口要与 plugin.json 的贡献声明对得上,否则用户新建配置时预填的端口是错的。</summary>
    [TestMethod]
    public void 默认端口_与清单一致()
    {
        Assert.AreEqual(3306, SqlDialects.Of(SqlDialect.MySql).DefaultPort);
        Assert.AreEqual(5432, SqlDialects.Of(SqlDialect.PostgreSql).DefaultPort);
        Assert.AreEqual(1433, SqlDialects.Of(SqlDialect.SqlServer).DefaultPort);
        Assert.AreEqual(1521, SqlDialects.Of(SqlDialect.Oracle).DefaultPort);
        Assert.IsTrue(SqlDialects.Of(SqlDialect.Sqlite).IsFileBased);
    }

    /// <summary>
    /// §5.1:MySQL 的 <c>charset</c> 下拉是**摆设** —— 实测六种取值(含乱填的)全部被驱动忽略,
    /// 会话字符集恒为 utf8mb4。摆一个不起作用的下拉是骗人,所以表单里不能有它。
    /// </summary>
    [TestMethod]
    public void MySQL表单_没有无效的字符集下拉()
    {
        IReadOnlyList<ProtocolSettingField> fields = SqlSettings.Declare(new("en"));

        Assert.IsFalse(
            fields.Any(f => f.Key.Contains("charset", StringComparison.OrdinalIgnoreCase)),
            "MySqlConnector 已把 CharacterSet 标为 Obsolete 并完全忽略 —— 表单上不该出现它。");
    }

    /// <summary>
    /// 文件型方言没有网络端点,不该出现隧道一节,也不该要凭据。
    /// <para>
    /// 五个方言合并成一个页签之后,这件事由两层共同保证:字段那一层靠
    /// <see cref="ProtocolSettingField.VisibleWhen" />(隧道那一栏在 SQLite 下消失),
    /// 连接框那一层靠<b>变体</b>(<c>NoCredentials</c> 让宿主收起用户名与口令两栏)。
    /// <b>两层都要验</b> —— 只验一层的话,另一层漏了照样是"对着两个填了没用的框发呆"。
    /// </para>
    /// </summary>
    [TestMethod]
    public void SQLite_没有隧道也不要凭据()
    {
        const string Sqlite = "sqlite";
        IReadOnlyList<ProtocolSettingField> fields = SqlSettings.Declare(new("en"));

        Assert.IsFalse(
            fields.Where(f => VisibleFor(f, Sqlite)).Any(f => f.Kind == ProtocolSettingKind.SshSession),
            "SQLite 上不该出现跳板会话那一栏。");

        WorkspaceVariant variant = SqlPlugin.Describe(new("en")).Variants.Single(v => v.Value == Sqlite);
        Assert.IsTrue(
            variant.Features!.Value.HasFlag(WorkspaceFeatures.NoCredentials),
            "SQLite 是个文件,填了用户名也没有任何地方会用到 —— 宿主该把那两栏收起来。");
        Assert.IsFalse(variant.Features.Value.HasFlag(WorkspaceFeatures.SshTunnel));
        Assert.IsFalse(variant.Features.Value.HasFlag(WorkspaceFeatures.CertificateTrust));
        Assert.IsFalse(string.IsNullOrEmpty(variant.HostLabel), "\"主机\"那一栏在 SQLite 上装的是文件路径,标签必须改写。");
    }

    /// <summary>按方言判一个字段此刻显不显示 —— 与宿主用的是同一个判据。</summary>
    private static bool VisibleFor(ProtocolSettingField field, string dialect) =>
        field.VisibleWhen is not { } condition
        || condition.IsSatisfiedBy(key => key == SqlDialects.DialectKey ? dialect : null);

    /// <summary>指纹回写字段必须存在且隐藏 —— 宿主要往里写用户确认过的证书指纹。</summary>
    [TestMethod]
    public void 网络型方言_有隐藏的指纹回写位()
    {
        ProtocolSettingField? thumbprint = SqlSettings
            .Declare(new("en"))
            .FirstOrDefault(f => f.Key == SqlSettings.TrustedThumbprintKey);

        Assert.IsNotNull(thumbprint, "缺指纹回写位。");
        Assert.IsTrue(thumbprint.IsHidden, "指纹回写位不该出现在表单上。");
    }

    /// <summary>
    /// §5.3:「库打不开」(4060) 的 <c>Errors</c> 集合里**同时含 18456**。
    /// 先判 18456 会把它误报成密码错、白弹一次登录框,而用户真正该改的是"数据库"那一栏。
    /// <b>这条顺序规则就是本测试的全部意义。</b>
    /// </summary>
    [TestMethod]
    public void SQLServer判据_库打不开要排在认证失败前面()
    {
        // 实测形态:Number=4060, Class=11, Errors={4060, 18456}
        Assert.AreEqual(
            SqlFailureKind.DatabaseMissing,
            DecideSqlServer(number: 4060, errorClass: 11, errorNumbers: [4060, 18456]));

        // 纯登录失败:Number=18456, Class=14, Errors 只有一条
        Assert.AreEqual(
            SqlFailureKind.Authentication,
            DecideSqlServer(number: 18456, errorClass: 14, errorNumbers: [18456]));

        // 传输层一律按 Class 归类,不依赖具体错误号(号随传输层变)。
        Assert.AreEqual(
            SqlFailureKind.Connection,
            DecideSqlServer(number: 258, errorClass: 20, errorNumbers: [258]));
        Assert.AreEqual(
            SqlFailureKind.Connection,
            DecideSqlServer(number: -1983577849, errorClass: 20, errorNumbers: []));

        // 语句超时(Number=-2, Class=11)不是连接失败 —— 归执行结果,别报成连接断。
        Assert.AreEqual(
            SqlFailureKind.Unknown,
            DecideSqlServer(number: -2, errorClass: 11, errorNumbers: [-2]));
    }

    /// <summary>PostgreSQL 判据:28 类是认证域;3D000 是"库打不开"而不是认证失败。</summary>
    [TestMethod]
    public void PostgreSQL判据_按SqlState分档()
    {
        Assert.AreEqual(SqlFailureKind.Authentication, DecidePostgres("28P01"));
        Assert.AreEqual(SqlFailureKind.Authentication, DecidePostgres("28000"));
        Assert.AreEqual(SqlFailureKind.DatabaseMissing, DecidePostgres("3D000"));
        // 权限不足是普通业务错误,原文透出即可 —— 报成认证失败会白弹登录框。
        Assert.AreEqual(SqlFailureKind.Unknown, DecidePostgres("42501"));
        // 用户取消 / statement_timeout 不是错误。
        Assert.AreEqual(SqlFailureKind.Unknown, DecidePostgres("57014"));
        Assert.AreEqual(SqlFailureKind.Unknown, DecidePostgres(null));
    }

    /// <summary>MySQL 判据:1045 可靠;1042 是"连不上"的大杂烩。</summary>
    [TestMethod]
    public void MySQL判据_按错误号分档()
    {
        Assert.AreEqual(SqlFailureKind.Authentication, DecideMySql(1045));
        Assert.AreEqual(SqlFailureKind.Connection, DecideMySql(1042));
        Assert.AreEqual(SqlFailureKind.Connection, DecideMySql(0));
        // 1146(表不存在)是业务错误,不该被翻成连接层的任何一档。
        Assert.AreEqual(SqlFailureKind.Unknown, DecideMySql(1146));
    }

    /// <summary>设置解析:超出范围的超时值要回落到默认,而不是把 0 或负数送进驱动。</summary>
    [TestMethod]
    public void 设置解析_超时值越界时回落默认()
    {
        SqlSettings settings = SqlSettings.From(Request(new()
        {
            ["connectTimeout"] = "0",
            ["commandTimeout"] = "-5"
        }), SqlDialect.MySql);

        Assert.AreEqual(15, settings.ConnectTimeoutSeconds);
        Assert.AreEqual(30, settings.CommandTimeoutSeconds);
    }

    /// <summary>SQLite 的文件路径走"主机"一栏(描述符已把它改标成"数据库文件")。</summary>
    [TestMethod]
    public void 设置解析_SQLite把主机当文件路径()
    {
        var request = new WorkspaceConnectRequest
        {
            SessionId = "t",
            Host = @"C:\data\app.db",
            Port = 1
        };

        SqlSettings settings = SqlSettings.From(request, SqlDialect.Sqlite);

        Assert.AreEqual(@"C:\data\app.db", settings.Database);
    }

    private static WorkspaceConnectRequest Request(Dictionary<string, string> settings) =>
        new()
        {
            SessionId = "t",
            Host = "127.0.0.1",
            Port = 3306,
            Settings = settings
        };
}
