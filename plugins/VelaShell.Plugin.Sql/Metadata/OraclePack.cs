using System.Data.Common;
using System.Reflection;
using System.Globalization;
using System.Text;

namespace VelaShell.Plugin.Sql.Metadata;

/// <summary>
/// Oracle Database 的方言包。**全部元数据直查 <c>ALL_*</c> 数据字典视图,一个 <c>IDbMaintenance</c> 方法都不调。**
/// <para>
/// <b>⚠ 本包是五份方言包里唯一一份没有真机的。</b> 设计文档附录 B.1 已经把这条写在明面上:
/// 本轮尝试拉 <c>container-registry.oracle.com/database/free</c>,实测 5 MB/min、剩余 2 GB 需要 6.7 小时,放弃。
/// 所以<b>这份实现完全按 Oracle 官方文档写成,一条 SQL 都没有在真服务器上跑过</b>。
/// 文件里每一处"文档这么说、但我没验过"的地方都用 <c>【未验证】</c> 标出来了 ——
/// 读到那个记号就意味着:接上真机的第一件事是先验它,而不是先信它。
/// </para>
/// <para>
/// <b>为什么仍然整块自己查</b>:理由与其它四个方言完全一样(§2.3)——
/// <c>IsIdentity</c> 恒 <see langword="true" />、视图的列返回 0 且不抛异常、自定义 schema 静默不可达、
/// 索引丢唯一性与列、生成列根本没有这个概念。这些是在 PG / SQL Server / MySQL / SQLite 上
/// <b>逐条量出来</b>的结构性缺陷,不是某个方言的偶发 bug,没有任何理由指望 Oracle 那一支是例外。
/// 一个会说谎的元数据源比没有更坏 —— 界面会如实地把假数据画出来。
/// </para>
/// <para>
/// <b>为什么用 <c>ALL_*</c> 而不是 <c>USER_*</c> 或 <c>DBA_*</c></b>:
/// <c>USER_*</c> 只看得见连接用户自己那一个 schema,而"开着 A 用户的连接去看 B schema 的表"是 Oracle 上的常态
/// (§7.2 要求 schema 一级必须真实可达);<c>DBA_*</c> 要 <c>SELECT_CATALOG_ROLE</c> 或 DBA 权限,
/// 普通业务账号一律 ORA-00942,一上来就把整棵对象树打死。<c>ALL_*</c> 是"当前用户有权限看见的全部",
/// 正好是对象树该画的那一份 —— 看不见的东西不画,比画一个点不开的节点诚实。
/// </para>
/// <para>
/// <b>标识符纪律</b>:比对数据字典一律走绑定变量,用户给的标识符<b>永不拼进 SQL</b>;
/// 确实要拼的地方(<see cref="EstimateRowCountSql" /> / <see cref="ShowCreateSql" /> 这类接口不给参数通道的)
/// 走 <see cref="Literal" />(字符串字面量)或 <see cref="DialectPackBase.QuoteIdentifier" />(标识符)。
/// </para>
/// </summary>
internal sealed class OraclePack : DialectPackBase
{
    /// <summary>
    /// 属主参数。<c>:p0</c> 传空(<see langword="null" />)时回落到连接的当前 schema。
    /// <para>
    /// <b>为什么由服务端回答"当前 schema"</b>:客户端记的那个名字会被用户的
    /// <c>ALTER SESSION SET CURRENT_SCHEMA = ...</c> 改掉,而这条语句在 Oracle 上很常用。
    /// 与 MySQL 包的 <c>COALESCE(NULLIF(@p0,''), DATABASE())</c> 是同一条思路。
    /// </para>
    /// <para>
    /// <b>⚠ 这里不能写 <c>NULLIF(:p0, '')</c></b>:Oracle 把空串当 <see langword="null" /> 存,
    /// <c>NULLIF(x, '')</c> 的第二个参数就成了字面 <c>NULL</c>,而 Oracle 明确不接受
    /// <c>NULLIF(expr, NULL)</c>(ORA-00932)。所以"没有 schema"这件事在 C# 侧就折成
    /// <see langword="null" /> 传下来(见 <see cref="OwnerParameter" />),SQL 侧只用 <c>COALESCE</c>。
    /// </para>
    /// </summary>
    private const string OwnerParam = "COALESCE(:p0, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA'))";

    /// <summary>
    /// schema 列表。Oracle 的 schema 就是 user,所以数据源是 <c>ALL_USERS</c>。
    /// <para>
    /// <c>ORACLE_MAINTAINED</c>(12c 起)标出 <c>SYS</c> / <c>SYSTEM</c> / <c>XDB</c> 这一大批随库自带的账号 ——
    /// 一个空库里就有三四十个,不标记的话用户会以为自己建了 40 个 schema。
    /// <b>仍然列出来</b>(用户确实要去 <c>SYS</c> 下看数据字典),只是默认折叠 —— 不列出来是另一种撒谎。
    /// </para>
    /// </summary>
    private const string SchemasRichSql = """
        SELECT u.USERNAME, u.ORACLE_MAINTAINED
          FROM ALL_USERS u
         ORDER BY u.USERNAME
        """;

    /// <summary>
    /// <see cref="SchemasRichSql" /> 的可移植子集:<c>ORACLE_MAINTAINED</c> 是 12c 才有的列,
    /// 11g 上整条查询会以 ORA-00904 打回 —— 那不是"少显示一点信息",是对象树整棵展不开。
    /// </summary>
    private const string SchemasPortableSql = """
        SELECT u.USERNAME, 'N' AS ORACLE_MAINTAINED
          FROM ALL_USERS u
         ORDER BY u.USERNAME
        """;

    /// <summary>
    /// 一个 schema 下的表 + 视图 + 物化视图,一次查完(§7.2 的"表 (37)"计数要求)。
    /// <para>
    /// <b>为什么把属主套进 <c>WITH</c> 子句</b>:ODP.NET 默认 <c>BindByName = false</c>,
    /// 绑定变量是<b>按出现位置</b>对上参数集合的(见 <see cref="OwnerParameter" /> 上的长注)。
    /// 三段 <c>UNION ALL</c> 里各写一次 <c>:p0</c> 就成了"要三个参数",而脚手架只给两个。
    /// <c>WITH scope AS (SELECT ... FROM DUAL)</c> 让它<b>只出现一次</b>,三段都 join 这一行。
    /// </para>
    /// <para>
    /// 四条过滤都不是洁癖,是"别把实现细节当用户对象画出来":
    /// <c>DROPPED='NO'</c> 剔回收站里的 <c>BIN$...</c>;<c>NESTED='NO'</c> 剔嵌套表的存储表;
    /// <c>SECONDARY='N'</c> 剔域索引(Text / Spatial)自建的辅助表;
    /// <c>IOT_TYPE</c> 只留 <c>NULL</c>(普通堆表)与 <c>'IOT'</c>(索引组织表本体),
    /// 把 <c>IOT_OVERFLOW</c> / <c>IOT_MAPPING</c> 这些同一张表的附属段挡掉。
    /// <c>NOT EXISTS (ALL_MVIEWS)</c> 则是因为<b>物化视图的容器表也在 <c>ALL_TABLES</c> 里</b> ——
    /// 不剔就会同一个对象在树上出现两次(一次是表、一次是物化视图)。【未验证】
    /// </para>
    /// </summary>
    private const string RelationsSql = $"""
        WITH scope AS (SELECT {OwnerParam} AS owner_name FROM DUAL)
        SELECT 1 AS obj_kind, t.OWNER, t.TABLE_NAME, tc.COMMENTS, t.NUM_ROWS
          FROM scope s
          JOIN ALL_TABLES t ON t.OWNER = s.owner_name
          LEFT JOIN ALL_TAB_COMMENTS tc ON tc.OWNER = t.OWNER AND tc.TABLE_NAME = t.TABLE_NAME
         WHERE t.DROPPED = 'NO'
           AND t.NESTED = 'NO'
           AND t.SECONDARY = 'N'
           AND (t.IOT_TYPE IS NULL OR t.IOT_TYPE = 'IOT')
           AND NOT EXISTS (SELECT 1 FROM ALL_MVIEWS mx
                            WHERE mx.OWNER = t.OWNER AND mx.CONTAINER_NAME = t.TABLE_NAME)
        UNION ALL
        SELECT 2, v.OWNER, v.VIEW_NAME, vc.COMMENTS, NULL
          FROM scope s
          JOIN ALL_VIEWS v ON v.OWNER = s.owner_name
          LEFT JOIN ALL_TAB_COMMENTS vc ON vc.OWNER = v.OWNER AND vc.TABLE_NAME = v.VIEW_NAME
        UNION ALL
        SELECT 3, m.OWNER, m.MVIEW_NAME, mc.COMMENTS,
               (SELECT ct.NUM_ROWS FROM ALL_TABLES ct
                 WHERE ct.OWNER = m.OWNER AND ct.TABLE_NAME = m.CONTAINER_NAME)
          FROM scope s
          JOIN ALL_MVIEWS m ON m.OWNER = s.owner_name
          LEFT JOIN ALL_TAB_COMMENTS mc ON mc.OWNER = m.OWNER AND mc.TABLE_NAME = m.MVIEW_NAME
         ORDER BY 1, 3
        """;

    /// <summary>
    /// 列。<b>视图与物化视图照样拿得到列</b> —— <c>ALL_TAB_COLUMNS</c> 对表 / 视图 / 物化视图一视同仁,
    /// 而 <c>GetColumnInfosByTableName</c> 对视图返回 0 列且不抛异常(§2.3 / §7.2 第 4 条)。
    /// <para>
    /// 三处需要解释的取法:
    /// </para>
    /// <para>
    /// ① <b><c>VIRTUAL_COLUMN</c> 只在 <c>ALL_TAB_COLS</c> 上有,<c>ALL_TAB_COLUMNS</c> 没有这一列</b>
    /// (对着官方视图定义核过)。所以生成列判定必须多 join 一次 <c>ALL_TAB_COLS</c>。
    /// 反过来,主表<b>不能</b>直接换成 <c>ALL_TAB_COLS</c>:那个视图连隐藏列和系统生成列一起给,
    /// 函数索引会在表上凭空多出一列 <c>SYS_NC00007$</c>,而用户没建过它。
    /// </para>
    /// <para>
    /// ② <c>DATA_DEFAULT</c> <b>放在选择列表的最后一格</b>。它是 <c>LONG</c> 类型,两个坑叠在一起:
    /// (a) 脚手架用 <c>CommandBehavior.SequentialAccess</c> 读,<c>LONG</c> 必须在它后面的列之前读完,
    /// 排在末尾就不可能踩到;(b) <b>ODP.NET 默认只取 <c>InitialLONGFetchSize</c> 个字节,
    /// 而托管版的默认值是 0 —— 也就是说不设它,这一格读回来是空串,不是默认值</b>。
    /// 设这个值要拿到 <c>OracleCommand</c> 本身,而脚手架给的是 <c>DbCommand</c>。
    /// <b>真机证实了这条推断</b>:26ai 上不设它,<c>number(12,3) default 0</c> 的默认值读回来就是空串。
    /// 现在由 <see cref="EnableLongFetch" /> 反射设成 <c>-1</c>(取完整值)。
    /// 顺带:<c>LONG</c> 不能进 <c>SUBSTR</c> / <c>TO_CHAR</c> / <c>CAST</c>,SQL 侧没有绕法。
    /// </para>
    /// <para>
    /// ③ 列注释走 <c>ALL_COL_COMMENTS</c> 的 <c>LEFT JOIN</c>,按 属主+对象+列名 三段对齐 ——
    /// 少一段就会把同名不同表的注释串到一起。
    /// </para>
    /// </summary>
    private const string ColumnsRichSql = $"""
        SELECT c.COLUMN_ID, c.COLUMN_NAME, c.DATA_TYPE, c.DATA_TYPE_OWNER,
               c.DATA_LENGTH, c.DATA_PRECISION, c.DATA_SCALE, c.CHAR_LENGTH, c.CHAR_USED,
               c.NULLABLE, c.IDENTITY_COLUMN, x.VIRTUAL_COLUMN, m.COMMENTS,
               c.DATA_DEFAULT
          FROM ALL_TAB_COLUMNS c
          LEFT JOIN ALL_TAB_COLS x
            ON x.OWNER = c.OWNER AND x.TABLE_NAME = c.TABLE_NAME AND x.COLUMN_NAME = c.COLUMN_NAME
          LEFT JOIN ALL_COL_COMMENTS m
            ON m.OWNER = c.OWNER AND m.TABLE_NAME = c.TABLE_NAME AND m.COLUMN_NAME = c.COLUMN_NAME
         WHERE c.OWNER = {OwnerParam}
           AND c.TABLE_NAME = :p1
         ORDER BY c.COLUMN_ID
        """;

    /// <summary>
    /// <see cref="ColumnsRichSql" /> 的可移植子集。<c>IDENTITY_COLUMN</c> 是 12c 才有的列
    /// (identity 列本身也是 12c 才有的特性),11g 上整条查询会以 ORA-00904 打回。
    /// <para>
    /// 退化之后 <b>11g 上的自增列一律报 <see langword="false" /></b>,这在 11g 上是对的:
    /// 那一代的"自增"是"序列 + 触发器"的手工组合,数据字典里<b>没有任何一格</b>说得出
    /// "这一列会被服务端自动填值" —— 要认它只能去解析触发器正文,那是猜不是读。
    /// 报 <see langword="false" /> 的代价是插入时多带一列;报 <see langword="true" /> 的代价是
    /// 用户明明想自己给值却被剔掉。宁可前者。
    /// </para>
    /// </summary>
    private const string ColumnsPortableSql = $"""
        SELECT c.COLUMN_ID, c.COLUMN_NAME, c.DATA_TYPE, c.DATA_TYPE_OWNER,
               c.DATA_LENGTH, c.DATA_PRECISION, c.DATA_SCALE, c.CHAR_LENGTH, c.CHAR_USED,
               c.NULLABLE, 'NO' AS IDENTITY_COLUMN, x.VIRTUAL_COLUMN, m.COMMENTS,
               c.DATA_DEFAULT
          FROM ALL_TAB_COLUMNS c
          LEFT JOIN ALL_TAB_COLS x
            ON x.OWNER = c.OWNER AND x.TABLE_NAME = c.TABLE_NAME AND x.COLUMN_NAME = c.COLUMN_NAME
          LEFT JOIN ALL_COL_COMMENTS m
            ON m.OWNER = c.OWNER AND m.TABLE_NAME = c.TABLE_NAME AND m.COLUMN_NAME = c.COLUMN_NAME
         WHERE c.OWNER = {OwnerParam}
           AND c.TABLE_NAME = :p1
         ORDER BY c.COLUMN_ID
        """;

    /// <summary>
    /// 主键列(按约束内序号)。
    /// <para>
    /// <b>为什么单独查一次,而不是从主键索引的列里取</b>:约束才是"用户声明的那个主键"。
    /// Oracle 允许主键约束挂在一个<b>已存在的非唯一索引</b>上,那个索引的列可以比主键多、
    /// 顺序也可以不同;拿索引的列当主键,网格的 <c>UPDATE</c> 就会带上多余的定位列(打不中行)
    /// 或者顺序错位。PG 包出于同一条理由走 <c>pg_constraint</c> 而不是 <c>pg_index</c>。
    /// </para>
    /// </summary>
    private const string PrimaryKeySql = $"""
        SELECT cc.COLUMN_NAME
          FROM ALL_CONSTRAINTS c
          JOIN ALL_CONS_COLUMNS cc
            ON cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
         WHERE c.CONSTRAINT_TYPE = 'P'
           AND c.OWNER = {OwnerParam}
           AND c.TABLE_NAME = :p1
         ORDER BY cc.POSITION
        """;

    /// <summary>
    /// 索引 + 每个索引的有序列。<c>ALL_IND_COLUMNS</c> 是<b>每列一行</b>,按索引名归并、
    /// <c>COLUMN_POSITION</c> 排序。
    /// <para>
    /// <c>UNIQUENESS = 'UNIQUE'</c> 是唯一性的唯一权威来源;
    /// <b>"是不是主键索引"靠 <c>ALL_CONSTRAINTS.CONSTRAINT_TYPE='P'</c> 关联</b> ——
    /// Oracle 的主键索引<b>没有固定名字</b>(不像 MySQL 恒为 <c>PRIMARY</c>),
    /// 系统生成的叫 <c>SYS_C0011234</c>,用户建的叫什么都行,按名字猜必错。
    /// 关联键是 <c>INDEX_OWNER + INDEX_NAME</c>(<c>ALL_CONSTRAINTS</c> 上这两列就是给这件事用的)。
    /// </para>
    /// <para>
    /// <c>ORDER BY</c> 把主键索引顶到最前:主键是用户第一眼要找的东西,按字母排会掉进中间。
    /// (<see cref="DialectPackBase.Fold" /> 走 <c>GroupBy</c>,分组顺序 = 首次出现顺序,
    /// 所以排序在 SQL 里定就够。)
    /// </para>
    /// </summary>
    private const string IndexesSql = $"""
        SELECT i.INDEX_NAME, i.UNIQUENESS, i.INDEX_TYPE, i.STATUS,
               CASE WHEN pk.CONSTRAINT_NAME IS NULL THEN 'N' ELSE 'Y' END AS IS_PRIMARY,
               ic.COLUMN_POSITION, ic.COLUMN_NAME, ic.DESCEND
          FROM ALL_INDEXES i
          JOIN ALL_IND_COLUMNS ic
            ON ic.INDEX_OWNER = i.OWNER AND ic.INDEX_NAME = i.INDEX_NAME
          LEFT JOIN ALL_CONSTRAINTS pk
            ON pk.OWNER = i.TABLE_OWNER AND pk.TABLE_NAME = i.TABLE_NAME
           AND pk.CONSTRAINT_TYPE = 'P'
           AND pk.INDEX_OWNER = i.OWNER AND pk.INDEX_NAME = i.INDEX_NAME
         WHERE i.TABLE_OWNER = {OwnerParam}
           AND i.TABLE_NAME = :p1
         ORDER BY CASE WHEN pk.CONSTRAINT_NAME IS NULL THEN 1 ELSE 0 END,
                  i.INDEX_NAME, ic.COLUMN_POSITION
        """;

    /// <summary>
    /// 外键。<c>IDbMaintenance</c> 里<b>一个外键方法都没有</b>(§2.3),这条只能自己查。
    /// <para>
    /// <c>CONSTRAINT_TYPE = 'R'</c> 是外键;<b>目标端靠 <c>R_OWNER</c> + <c>R_CONSTRAINT_NAME</c>
    /// 再查一次 <c>ALL_CONSTRAINTS</c> / <c>ALL_CONS_COLUMNS</c></b> ——
    /// Oracle 的外键指向的是"父表上的某个主键/唯一约束",不是直接指向列,
    /// 所以目标表名与目标列名都在那第二跳上。
    /// </para>
    /// <para>
    /// <b>两端按 <c>POSITION</c> 对齐,不是按行序对齐。</b> 复合外键分别取两串列再按下标配对,
    /// 只要任一端的返回顺序变一下就会错位 —— 而外键画错的后果是关系图上一条指向错列的线,
    /// 用户照着它写 JOIN(PG 包在 <c>unnest(conkey, confkey)</c> 那里踩的是同一个坑)。
    /// </para>
    /// <para>
    /// <b>Oracle 的外键没有 <c>ON UPDATE</c> 这回事</b>,数据字典里也就没有对应列 ——
    /// <c>ALL_CONSTRAINTS</c> 只有 <c>DELETE_RULE</c>。见 <see cref="NoUpdateRule" />。
    /// </para>
    /// <para>
    /// <b>⚠ 已知边界</b>:父表在另一个 schema 且当前用户对它没有任何权限时,
    /// 第二跳的两个 <c>ALL_*</c> 里看不见那条约束,于是<b>整条外键会从结果里消失</b>(内连接被打断)。
    /// 这是 <c>ALL_*</c> 的语义决定的,不是 bug;但它意味着"外键列表为空"不等于"没有外键"。【未验证】
    /// </para>
    /// </summary>
    private const string ForeignKeysSql = $"""
        SELECT c.CONSTRAINT_NAME, c.DELETE_RULE, cc.POSITION, cc.COLUMN_NAME,
               rc.OWNER, rc.TABLE_NAME, rcc.COLUMN_NAME
          FROM ALL_CONSTRAINTS c
          JOIN ALL_CONS_COLUMNS cc
            ON cc.OWNER = c.OWNER AND cc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
          JOIN ALL_CONSTRAINTS rc
            ON rc.OWNER = c.R_OWNER AND rc.CONSTRAINT_NAME = c.R_CONSTRAINT_NAME
          JOIN ALL_CONS_COLUMNS rcc
            ON rcc.OWNER = rc.OWNER AND rcc.CONSTRAINT_NAME = rc.CONSTRAINT_NAME
           AND rcc.POSITION = cc.POSITION
         WHERE c.CONSTRAINT_TYPE = 'R'
           AND c.OWNER = {OwnerParam}
           AND c.TABLE_NAME = :p1
         ORDER BY c.CONSTRAINT_NAME, cc.POSITION
        """;

    /// <summary>
    /// 外键的更新动作。**Oracle 的外键语法里根本没有 <c>ON UPDATE</c> 子句**
    /// (要级联更新只能自己写触发器),数据字典里也没有这一列。
    /// 所以这里填的是标准语义下的等价物,而不是"没查到"。
    /// </summary>
    private const string NoUpdateRule = "NO ACTION";

    /// <inheritdoc />
    public override SqlDialect Dialect => SqlDialect.Oracle;

    /// <inheritdoc />
    /// <remarks>Oracle 的 schema 就是 user,而且是实打实的一级:对象树必须画它。</remarks>
    public override bool HasSchemas => true;

    /// <inheritdoc />
    /// <remarks>
    /// <b>"库"这一级由 schema 顶替 —— 这是一个刻意的建模决定,不是没写完。</b>
    /// <para>
    /// Oracle 里与"数据库"这个词对得上的东西有两个,两个都不该出现在对象树的第一层:
    /// ① <b>database / CDB</b>:一条连接从建立到断开只属于一个库(或一个 PDB),
    /// 服务端<b>没有"换个库"这个动作</b> —— 换库等于换连接串重连。把它画成一级,
    /// 就会得到一棵永远只有一个孩子、点开还不能切的树。
    /// ② <b>PDB</b>:切 PDB 要 <c>ALTER SESSION SET CONTAINER</c>,那是 CDB 管理员的动作,
    /// 普通业务账号连 <c>V$PDBS</c> 都读不到,列出来就是一排点不开的节点。
    /// </para>
    /// <para>
    /// 而用户在 Oracle 上真正会"换来换去"的那一级,叫 schema:
    /// 一条连接照样看得见 <c>ALL_*</c> 里所有有权限的 schema,<c>APP.ORDERS</c> 这种限定名天天在写。
    /// 所以本包让 <see cref="HasSchemas" /> 为 <see langword="true" />、
    /// 这里为 <see langword="false" />,对象树是"连接 → schema → 对象类别 → 对象"。
    /// 与 MySQL 包正好相反(那边是"库"真实存在而 schema 是同一个东西的别名),两边都是照实建模。
    /// </para>
    /// </remarks>
    public override bool HasDatabases => false;

    /// <inheritdoc />
    /// <remarks>
    /// 一条连接只属于一个库/PDB,而<b>本包压根不画"库"这一级</b>(见上),
    /// 所以对象树永远不会问"另一个库里有什么" —— 这一格在 Oracle 上不成立,取默认的 true。
    /// </remarks>
    public override bool MetadataSpansCatalogs => true;

    /// <inheritdoc />
    public override bool HasRoutines => true;

    /// <inheritdoc />
    public override bool HasSequences => true;

    /// <summary>
    /// 定界符是双引号,转义是双引号加倍(基类统一处理)。
    /// <para>
    /// <b>⚠ Oracle 的大小写是本方言最大的坑,而它恰恰卡在这一格上。</b>
    /// 不加引号的标识符会被<b>折成大写</b>再存进数据字典(<c>create table orders</c> → 字典里是 <c>ORDERS</c>);
    /// 加引号的<b>原样存</b>(<c>create table "orders"</c> → 字典里就是 <c>orders</c>),
    /// 而且这两张表可以在同一个 schema 里<b>并存</b>。
    /// 于是:本包一旦把名字包进双引号,那个名字就<b>必须</b>是数据字典里的原样形态 ——
    /// 拼 <c>"orders"</c> 去查一张建成 <c>ORDERS</c> 的表,服务端报的是 ORA-00942 表不存在。
    /// </para>
    /// <para>
    /// 本包的对策是"不猜":名字一律原样透传(见
    /// <see cref="ReadColumnsAsync" /> 上关于"为什么不 <c>UPPER()</c>"的长注),
    /// 由对象树保证喂进来的名字来自数据字典本身。
    /// </para>
    /// <para>
    /// 顺带一条与转义有关的事实:<b>Oracle 明确禁止标识符里出现双引号</b>
    /// (加不加引号都不行,官方语法参考写死了这一条)。所以基类那一步"双引号加倍"
    /// 在任何<b>合法</b>的 Oracle 名字上都不会触发 —— 它防的正是那个不可能合法的名字,
    /// 也就是 §5.4.4 里实测能删表的那种注入载荷。<b>不能因为"用不上"就省掉。</b>
    /// </para>
    /// </summary>
    protected override (char Open, char Close) Delimiters => ('"', '"');

    /// <inheritdoc />
    /// <remarks>
    /// 恒空,理由见 <see cref="HasDatabases" />。<b>不要在这里把 PDB 或 schema 冒充成库返回</b> ——
    /// 前者绝大多数账号读不到,后者会让对象树画出"schema → 同名 schema → 表"的假三层。
    /// <para>方法体不碰 <paramref name="connection" />:一次多余的往返也是往返。</para>
    /// </remarks>
    public override Task<IReadOnlyList<SqlObject>> ListDatabasesAsync(
        DbConnection connection, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SqlObject>>([]);

    /// <inheritdoc />
    /// <remarks>
    /// 数据源是 <c>ALL_USERS</c> —— Oracle 里 schema 与 user 是同一个东西。
    /// <para>
    /// <b>为什么不用 <c>SELECT DISTINCT OWNER FROM ALL_OBJECTS</c> 来"只列有东西的 schema"</b>:
    /// 那要扫一遍整个对象字典(大库上是秒级),而且空 schema 也是用户建的、也该看得见。
    /// </para>
    /// </remarks>
    public override async Task<IReadOnlyList<SqlObject>> ListSchemasAsync(
        DbConnection connection, CancellationToken cancellationToken)
    {
        static SqlObject Map(DbDataReader r)
        {
            string name = Str(r, 0);
            // ORACLE_MAINTAINED 是 'Y' / 'N';Bool 已经认得 'Y'。
            return new SqlObject(SqlObjectKind.Schema, name, IsSystem: Bool(r, 1));
        }

        try
        {
            return await QueryAsync(connection, SchemasRichSql, Map, null, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // 12c 以下没有 ORACLE_MAINTAINED 这一列(ORA-00904)。多一个来回,换老服务端上不黑屏。【未验证】
            return await QueryAsync(connection, SchemasPortableSql, Map, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 表 / 视图 / 物化视图一次查完 —— 分三次查会让对象树展开时抖三下,
    /// 而"表 (37)"那个计数要等三条都回来(§7.2)。
    /// </remarks>
    public override async Task<IReadOnlyList<SqlObject>> ListRelationsAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        List<SqlObject> relations = await QueryAsync(
            connection,
            RelationsSql,
            r =>
            {
                // SequentialAccess:**必须严格按列序读**,一列只读一次。
                // SqlObject 的构造参数顺序是 (Kind, Name, Schema, …),而 SELECT 里 OWNER 在
                // TABLE_NAME 前面 —— 直接写成构造调用就会变成 0→2→1 的倒着读,运行期才炸。
                SqlObjectKind kind = KindOf(Int(r, 0));
                string owner = Str(r, 1);
                string name = Str(r, 2);
                string comment = Str(r, 3);
                // NUM_ROWS 来自统计信息,可能过时、也可能是 NULL(见 EstimateRowCountSql)。
                long? rows = LongOrNull(r, 4);
                return new SqlObject(kind, name, owner, comment, rows);
            },
            [OwnerParameter(schema)],
            cancellationToken).ConfigureAwait(false);
        return relations;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>视图照样拿得到列</b>(<c>ALL_TAB_COLUMNS</c> 对视图一视同仁),但视图<b>没有</b>索引、
    /// 主键约束与外键 —— 那三条查询对视图是白跑三个来回,直接省掉。
    /// <para>
    /// <b>物化视图不在此列</b>:它的容器表是一张真表,可以建索引、可以有主键,
    /// 所以按表的路径走完整流程。这一格是 Oracle 与 PG 的一个实质差别,别顺手一起省了。【未验证】
    /// </para>
    /// </remarks>
    public override async Task<IReadOnlyList<SqlObject>> ListRoutinesAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        // 数据源取 ALL_OBJECTS 而不是 ALL_PROCEDURES:后者把**包里的每个过程**也列成一行
        // (OBJECT_NAME=包名、PROCEDURE_NAME=成员名),于是一个 10 个方法的包在树上是 10 行同名节点。
        // 顶层的过程 / 函数 / 包正好是 ALL_OBJECTS 里的三个 OBJECT_TYPE。
        //
        // GENERATED='N' 剔掉系统为对象类型自动生成的那批;WITH scope 的理由同 RelationsSql
        // (ODP.NET 按位置绑参,:p0 只能出现一次)。
        const string Sql = $"""
            WITH scope AS (SELECT {OwnerParam} AS owner_name FROM DUAL)
            SELECT o.OBJECT_TYPE, o.OWNER, o.OBJECT_NAME
              FROM scope s
              JOIN ALL_OBJECTS o ON o.OWNER = s.owner_name
             WHERE o.OBJECT_TYPE IN ('PROCEDURE', 'FUNCTION', 'PACKAGE')
               AND o.GENERATED = 'N'
             ORDER BY o.OBJECT_TYPE, o.OBJECT_NAME
            """;
        return await QueryAsync(
            connection,
            Sql,
            static r =>
            {
                // SequentialAccess:必须按列序读。
                string type = Str(r, 0);
                string owner = Str(r, 1);
                string name = Str(r, 2);
                // 包(PACKAGE)归到"过程"一类:它是一组过程的容器,用户在这一栏里找的就是它。
                return new SqlObject(
                    type == "FUNCTION" ? SqlObjectKind.Function : SqlObjectKind.Procedure,
                    name,
                    owner);
            },
            [OwnerParameter(schema)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SqlObject>> ListSequencesAsync(
        DbConnection connection, string schema, CancellationToken cancellationToken)
    {
        // 数据源取 ALL_OBJECTS 而不是 ALL_SEQUENCES,只为一件事:**GENERATED 这一列**。
        // identity 列(GENERATED ALWAYS AS IDENTITY)背后引擎会自建一条序列,
        // 名字形如 ISEQ$$_74207 —— 真机上 velaspike 这个 schema 里三条序列有两条是这种。
        // ALL_SEQUENCES 里没有任何列能把它们与用户建的序列分开,而把它们画进树
        // 与"把物化视图的容器表当成表画出来"是同一类错:那是实现细节,不是用户的对象。
        const string Sql = $"""
            WITH scope AS (SELECT {OwnerParam} AS owner_name FROM DUAL)
            SELECT o.OWNER, o.OBJECT_NAME
              FROM scope s
              JOIN ALL_OBJECTS o ON o.OWNER = s.owner_name
             WHERE o.OBJECT_TYPE = 'SEQUENCE'
               AND o.GENERATED = 'N'
             ORDER BY o.OBJECT_NAME
            """;
        return await QueryAsync(
            connection,
            Sql,
            static r => new SqlObject(SqlObjectKind.Sequence, Str(r, 1), Str(r, 0)),
            [OwnerParameter(schema)],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<SqlTableSchema> DescribeAsync(
        DbConnection connection, SqlObject target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        object?[] parameters = [OwnerParameter(target.Schema), target.Name];
        bool relational = target.Kind != SqlObjectKind.View;

        List<SqlIndex> indexes = relational
            ? await ReadIndexesAsync(connection, target, parameters, cancellationToken).ConfigureAwait(false)
            : [];
        List<SqlForeignKey> foreignKeys = relational
            ? await ReadForeignKeysAsync(connection, parameters, cancellationToken).ConfigureAwait(false)
            : [];
        HashSet<string> primaryKey = relational
            ? await ReadPrimaryKeyAsync(connection, parameters, cancellationToken).ConfigureAwait(false)
            : [];
        IReadOnlyList<SqlColumn> columns =
            await ReadColumnsAsync(connection, parameters, primaryKey, cancellationToken).ConfigureAwait(false);

        return new(target, columns, indexes, foreignKeys);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>OFFSET … ROWS FETCH NEXT … ROWS ONLY</c> 是 SQL:2008 的行限定子句,Oracle <b>12.1 起</b>支持。
    /// <para>
    /// <b>【TODO】11g 及更早没有这个子句</b>,只能退回 <c>ROWNUM</c> 双层嵌套:
    /// <c>SELECT * FROM (SELECT a.*, ROWNUM rn FROM (原SQL) a WHERE ROWNUM &lt;= 结束行) WHERE rn &gt; 起始行</c>。
    /// <b>本版刻意不为老版本写这段代码</b> —— 它要把用户那条 SQL 塞进派生表,
    /// 而"塞进派生表"正是 §7.3 实测把 SQL Server 上带 <c>ORDER BY</c> 的查询整片打死的那条路
    /// (Msg 1033);Oracle 上的等价风险(带 <c>ORDER BY</c> / 带 <c>FOR UPDATE</c> / 列名重复的
    /// 用户 SQL 进派生表之后是否还成立)<b>没有真机可验</b>,凭想象写一段会在半数真实查询上出错的
    /// 兜底,比明说"这条路本版只支持 12c+"更坏。接上真机再补。
    /// </para>
    /// <para>
    /// 剥尾分号不是可选项:<b>Oracle 的 SQL 通道根本不接受语句末尾的分号</b>
    /// (那是 SQL*Plus 的行终止符,不是 SQL 的一部分),不剥掉会直接 ORA-00911。
    /// </para>
    /// </remarks>
    public override string ApplyPaging(string innerSql, int offset, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(innerSql);
        string body = innerSql.TrimEnd();
        while (body.EndsWith(';'))
        {
            body = body[..^1].TrimEnd();
        }
        string skip = Num(Math.Max(0, offset));
        string take = Num(Math.Max(0, limit));
        return $"{body}\nOFFSET {skip} ROWS FETCH NEXT {take} ROWS ONLY";
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>这个数是"约"不是"是",而且比其它方言更不可靠。</b>
    /// <c>ALL_TABLES.NUM_ROWS</c> 不是在线维护的计数器,它是<b>最后一次收集统计信息时的快照</b>
    /// (<c>DBMS_STATS</c> / 自动统计任务)。三种表现都要预期到:
    /// ① <b>可能为 <see langword="null" /></b> —— 表从建好起就没被统计过;
    /// ② <b>可能过时到离谱</b> —— 昨夜统计、今天灌了千万行,它还是昨夜那个数;
    /// ③ 统计任务被关掉的库上,它会永远停在某个历史值。
    /// 用途只有一个:底栏秒回"约 N 行",<b>点了才做精确 <c>count(*)</c></b>(§7.3)。
    /// 拿它做分页总数、做"是否为空"的判断,都会错。
    /// </remarks>
    public override string? EstimateRowCountSql(SqlObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind is not (SqlObjectKind.Table or SqlObjectKind.MaterializedView))
        {
            // 视图在 ALL_TABLES 里一行都没有,给一条恒空的语句不如明说拿不到。
            return null;
        }

        // 这个接口不给参数通道,所以属主与对象名要拼进 SQL —— 走 Literal(单引号加倍)
        // 变成**字符串字面量**参与值比对,而不是拼成标识符。
        string owner = string.IsNullOrEmpty(target.Schema)
            ? "SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')"
            : Literal(target.Schema);

        // 物化视图的行数在它的**容器表**上,而容器表名不保证等于物化视图名
        // (PREBUILT TABLE 建出来的物化视图尤其不等),所以多一跳去 ALL_MVIEWS 问一次。【未验证】
        string name = target.Kind == SqlObjectKind.MaterializedView
            ? $"""
               (SELECT mv.CONTAINER_NAME FROM ALL_MVIEWS mv
                 WHERE mv.OWNER = {owner} AND mv.MVIEW_NAME = {Literal(target.Name)})
               """
            : Literal(target.Name);

        return $"""
            SELECT t.NUM_ROWS
              FROM ALL_TABLES t
             WHERE t.OWNER = {owner}
               AND t.TABLE_NAME = {name}
            """;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>返回的是 <c>"SID,SERIAL#"</c> 两个数拼起来的一个串,不是单个 SID。这是一个刻意的取舍。</b>
    /// <para>
    /// 起因:Oracle 的旁路取消语句(<c>ALTER SYSTEM CANCEL SQL</c> / <c>KILL SESSION</c>)
    /// <b>都要 SID 与 SERIAL# 两个值</b>,单靠 SID 不够 —— SID 是会话槽位号,会被复用;
    /// SERIAL# 是那个槽位的第几次使用。只给 SID 的话,等目标会话结束、槽位被别人占了,
    /// 那条语句就会<b>掐掉一个无辜的会话</b>。这正是 Oracle 要求两个值的原因。
    /// </para>
    /// <para>
    /// 而契约上这一格是 <c>string?</c>(一条语句、一个值)。三条路里选了拼串:
    /// ① 改契约让它返回两列 —— 为一个方言改五个方言共用的接口,不划算;
    /// ② 如实返回 <see langword="null" /> 并放弃旁路取消 —— <b>代价太大</b>:§3.10 实测
    /// 旁路取消是"已经交给同步 API 的查询"<b>唯一</b>能被打断的手段,放弃它意味着 Oracle 上
    /// 一条跑飞的查询只能等它自己结束或者杀进程;
    /// ③ <b>本版:一次查回两个值,用逗号拼成一个串</b>,由 <see cref="CancelSessionSql" /> 拆回去。
    /// 契约形状没动,语义只在本包内部闭环,拆的那一头就在同一个文件里,不存在跨模块约定。
    /// </para>
    /// <para>
    /// <b>⚠ 两条已知边界</b>:
    /// (a) <c>V$SESSION</c> 要 <c>SELECT</c> 权限,普通业务账号默认<b>没有</b> ——
    /// 这条语句会 ORA-00942,调用方据此降级成"本会话不支持旁路取消",这是可接受的失败:
    /// 它是显式的,不是错答案。
    /// (b) RAC 上要连 <c>INST_ID</c> 一起带(<c>GV$SESSION</c>,语句形如 <c>'sid, serial, @inst'</c>),
    /// 本版没做。【未验证】【TODO】
    /// </para>
    /// </remarks>
    /// <summary>
    /// 执行计划。
    /// <para>
    /// <b>Oracle 是五个方言里唯一给不出"一条语句就出计划"的。</b> 没有 <c>EXPLAIN &lt;sql&gt;</c>
    /// 这种形态:<c>EXPLAIN PLAN</c> 是一条 <b>DDL,不返回结果集</b>,它只是把计划写进 <c>PLAN_TABLE</c>;
    /// 要看还得再 <c>SELECT</c> 一次 <c>DBMS_XPLAN.DISPLAY</c>。所以这里返回的是**两条语句**——
    /// 执行侧本来就按分号切句顺序发,最后一条的结果集就是计划。
    /// </para>
    /// <para>
    /// <b><c>analyze</c> 这一档在 Oracle 上是空的,而且这不是偷懒。</b>
    /// <c>EXPLAIN PLAN</c> **从不执行**被解释的语句(它连绑定变量都只按类型猜),
    /// 所以"真跑一次拿实际行数"在这条通道上不存在 —— 那要走
    /// <c>GATHER_PLAN_STATISTICS</c> 提示 + <c>DISPLAY_CURSOR</c>,而后者要先真把语句跑完,
    /// 还要 <c>sql_id</c>。两档返回同一条,是因为**这个方言里本就只有一档**,
    /// 与 SQLite 的 <c>EXPLAIN QUERY PLAN</c> 同理。
    /// </para>
    /// <para>
    /// <c>STATEMENT_ID</c> 固定用 <c>VELASHELL</c> 并在 <c>DISPLAY</c> 里按它过滤 ——
    /// 不加过滤会把 <c>PLAN_TABLE</c> 里别人留下的旧计划一起显示出来。
    /// </para>
    /// </summary>
    /// <param name="innerSql">要解释的语句。</param>
    /// <param name="analyze">在本方言上无效,见上。</param>
    /// <returns>两条语句;拼不出时为 <see langword="null" />。</returns>
    public override string? ExplainSql(string innerSql, bool analyze) =>
        string.IsNullOrWhiteSpace(innerSql)
            ? null
            : $"""
               EXPLAIN PLAN SET STATEMENT_ID = 'VELASHELL' FOR {innerSql.TrimEnd().TrimEnd(';')};
               SELECT PLAN_TABLE_OUTPUT FROM TABLE(DBMS_XPLAN.DISPLAY(NULL, 'VELASHELL', 'TYPICAL'))
               """;

    /// <summary>
    /// 会话列表。列序按契约固定:id / user / host / db / state / seconds / query。
    /// <para>
    /// id 拼成 <c>sid,serial#</c>,与 <see cref="SessionIdSql" /> 和
    /// <see cref="CancelSessionSql" /> 是同一种形态 —— 三者对不齐,「杀会话」就会挑错人。
    /// </para>
    /// <para>
    /// <b>只列前台会话</b>(<c>TYPE = 'USER'</c>):后台进程(<c>PMON</c>、<c>SMON</c>、
    /// 一大票 <c>Wnnn</c>)在 <c>V$SESSION</c> 里数量远超真人会话,混在一起这一页就没法看了。
    /// </para>
    /// <para>要 <c>SELECT_CATALOG_ROLE</c> 或对 <c>V$SESSION</c> 的显式授权,否则 ORA-00942。</para>
    /// </summary>
    public override string? SessionListSql =>
        """
        SELECT s.SID || ',' || s.SERIAL# AS ID,
               s.USERNAME,
               NVL(s.MACHINE, ' '),
               NVL(s.SERVICE_NAME, ' '),
               s.STATUS,
               NVL(s.LAST_CALL_ET, 0),
               NVL(q.SQL_TEXT, ' ')
        FROM V$SESSION s
        LEFT JOIN V$SQL q ON q.SQL_ID = s.SQL_ID AND q.CHILD_NUMBER = s.SQL_CHILD_NUMBER
        WHERE s.TYPE = 'USER'
        ORDER BY DECODE(s.STATUS, 'ACTIVE', 0, 1), s.LAST_CALL_ET DESC, s.SID
        """;

    /// <summary>
    /// 锁与阻塞链。列序按契约固定:blocked / blocking / object / mode / query。
    /// <para>
    /// <b>用 <c>V$SESSION.BLOCKING_SESSION</c> 而不是自己 join <c>V$LOCK</c>。</b>
    /// 手工 join <c>V$LOCK</c> 求阻塞关系是 Oracle 上流传最广的写法,但它只覆盖
    /// TX/TM 这类队列锁;<c>BLOCKING_SESSION</c> 是服务端自己算出来的,还能表达
    /// 跨实例与"阻塞方在别的节点"这些情况(<c>BLOCKING_SESSION_STATUS</c>)。
    /// </para>
    /// <para>
    /// 被锁对象取自 <c>V$LOCKED_OBJECT</c> + <c>ALL_OBJECTS</c> —— 拿不到就留空,
    /// 而不是让整页查询失败:阻塞关系本身才是这一页要回答的问题。
    /// </para>
    /// </summary>
    public override string? LockListSql =>
        """
        SELECT b.SID || ',' || b.SERIAL# AS BLOCKED_ID,
               w.SID || ',' || w.SERIAL# AS BLOCKING_ID,
               NVL(o.OBJECT_NAME, ' '),
               NVL(b.EVENT, ' '),
               NVL(q.SQL_TEXT, ' ')
        FROM V$SESSION b
        JOIN V$SESSION w ON w.SID = b.BLOCKING_SESSION
        LEFT JOIN V$LOCKED_OBJECT lo ON lo.SESSION_ID = b.SID
        LEFT JOIN ALL_OBJECTS o ON o.OBJECT_ID = lo.OBJECT_ID
        LEFT JOIN V$SQL q ON q.SQL_ID = b.SQL_ID AND q.CHILD_NUMBER = b.SQL_CHILD_NUMBER
        WHERE b.BLOCKING_SESSION IS NOT NULL
        ORDER BY b.SECONDS_IN_WAIT DESC
        """;

    /// <summary>
    /// 类型候选。
    /// <para>
    /// 全部用 Oracle 自己的词:<c>VARCHAR2</c> 不是 <c>VARCHAR</c>(后者 Oracle 保留但语义可能改),
    /// 没有 <c>INT</c> 之外的整型家族(<c>INT</c> 本身也只是 <c>NUMBER(38)</c> 的别名),
    /// 也没有 <c>BOOLEAN</c> 列类型 —— 23ai 起才有,而这份清单要对更老的版本也成立。
    /// </para>
    /// </summary>
    public override IReadOnlyList<string> CommonTypes =>
    [
        "NUMBER", "NUMBER(10)", "NUMBER(19)", "NUMBER(12,2)",
        "VARCHAR2(50)", "VARCHAR2(200)", "VARCHAR2(4000)", "NVARCHAR2(200)",
        "CHAR(1)", "CLOB", "NCLOB", "BLOB",
        "DATE", "TIMESTAMP", "TIMESTAMP WITH TIME ZONE", "INTERVAL DAY TO SECOND",
        "BINARY_FLOAT", "BINARY_DOUBLE", "RAW(16)"
    ];

    /// <summary>
    /// 加列。
    /// <para>
    /// <b>Oracle 没有 <c>ADD COLUMN</c> 这种写法</b> —— 基类的通用形态
    /// (<c>ALTER TABLE t ADD COLUMN c INT</c>)在 Oracle 上是 ORA-00904 语法错,
    /// 正确的是 <c>ALTER TABLE t ADD (c INT)</c>。这正是"通用 DDL 到 Oracle 就得改"的典型。
    /// </para>
    /// <para>
    /// 与基类一样,<b>表达不了的东西一律返回 <see langword="null" /> 而不是悄悄丢掉</b>:
    /// 生成列、主键、自增各有专门语法,拼进这条会得到一个"成功了但不是你要的那一列"。
    /// </para>
    /// </summary>
    /// <param name="target">目标表。</param>
    /// <param name="column">要加的列。</param>
    /// <returns>DDL;表达不了时为 <see langword="null" />。</returns>
    public override string? AddColumnDdl(SqlObject target, SqlColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (column.IsGenerated || column.IsPrimaryKey || column.IsAutoIncrement || column.Comment.Length > 0)
        {
            // 这几样这条语句表达不了 —— 明说,而不是发一条只做对一半的 DDL。
            return null;
        }
        // DEFAULT 必须排在 NOT NULL **之前**:Oracle 的列定义语法是
        // <名字> <类型> [DEFAULT <表达式>] [NOT NULL],顺序反了是 ORA-00907。
        string body = $"{QuoteIdentifier(column.Name)} {column.DataType}"
                      + (string.IsNullOrEmpty(column.DefaultValue) ? "" : $" DEFAULT {column.DefaultValue}")
                      + (column.IsNullable ? "" : " NOT NULL");
        return $"ALTER TABLE {QuoteQualified(target)} ADD ({body})";
    }

    public override string? SessionIdSql =>
        """
        SELECT TO_CHAR(s.SID) || ',' || TO_CHAR(s.SERIAL#)
          FROM V$SESSION s
         WHERE s.SID = TO_NUMBER(SYS_CONTEXT('USERENV', 'SID'))
        """;

    /// <inheritdoc />
    /// <remarks>
    /// 发的是 <c>ALTER SYSTEM CANCEL SQL</c>,<b>不是</b> <c>ALTER SYSTEM KILL SESSION</c>。理由:
    /// <para>
    /// 取消的是<b>一条查询</b>,不是用户的整个会话。掐掉会话会把编辑器里未提交的事务、
    /// 会话级临时表、<c>ALTER SESSION</c> 设过的一切一起送走 —— 这与 PG 包选
    /// <c>pg_cancel_backend</c> 而不是 <c>pg_terminate_backend</c>、MySQL 包选 <c>KILL QUERY</c>
    /// 而不是 <c>KILL CONNECTION</c> 是同一条纪律,三个方言必须表现一致,
    /// 否则"点一下取消"在 Oracle 上就成了一个会丢数据的按钮。
    /// </para>
    /// <para>
    /// <b>真机验过(26ai Free 23.26.2.0.0)</b>:被取消方收到
    /// <c>ORA-01013: 用户请求取消当前操作</c>,而且**连接仍然可用**——
    /// 这正是"取消查询"区别于"杀会话"的地方,后者之后连 <c>select 1 from dual</c> 都发不出去。
    /// <para>
    /// <b>⚠ 代价:<c>CANCEL SQL</c> 是 18c 才有的。</b> 12c / 11g 上这条语句会以 ORA-00933 打回,
    /// 表现为"取消不生效"(这一条**仍未验证**,手头没有老版本实例)。
    /// <b>本版刻意不为老版本回落到 <c>KILL SESSION</c></b>:
    /// 一个显式失败的取消按钮,比一个悄悄掐掉用户未提交事务的取消按钮好。
    /// 【TODO】等连接层能探到服务端版本,再给老版本挂一条<b>需要用户明确确认</b>的
    /// <c>KILL SESSION ... IMMEDIATE</c> 通道。
    /// </para>
    /// </para>
    /// <para>
    /// 另:这条语句要 <c>ALTER SYSTEM</c> 权限,普通账号同样可能 ORA-01031 —— 同上,显式失败。
    /// </para>
    /// </remarks>
    public override string? CancelSessionSql(string sessionId)
    {
        // 会话 id 是本包自己从 SessionIdSql 查回来的 "sid,serial#"。仍然逐字符校验再拼:
        // ALTER SYSTEM 不接受绑定变量,拼接是这里唯一的注入面,而"这个值一定是我自己查的"
        // 是一句靠调用方守的约定 —— 约定守不住的时候,校验是最后一道。
        if (string.IsNullOrEmpty(sessionId))
        {
            return null;
        }
        int comma = sessionId.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            // 只有 SID 没有 SERIAL# —— 见 SessionIdSql:这种输入**不能**猜着用,
            // 猜错的后果是掐掉一个无辜的会话。
            return null;
        }
        string sid = sessionId[..comma].Trim();
        string serial = sessionId[(comma + 1)..].Trim();
        if (!IsDecimal(sid) || !IsDecimal(serial))
        {
            return null;
        }
        return $"ALTER SYSTEM CANCEL SQL '{sid}, {serial}'";
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>DBMS_METADATA.GET_DDL</c> 是 Oracle 上唯一能吐出<b>建表 DDL 原文</b>的服务端设施
    /// (没有 <c>SHOW CREATE TABLE</c> 这种东西)。
    /// <para>
    /// <b>调用方要知道的三件事</b>:
    /// ① 返回值是 <b><c>CLOB</c></b>,不是 <c>VARCHAR2</c> —— 按普通字符串读会被截断,
    /// 要走 <c>GetChars</c> / <c>TextReader</c>;
    /// ② <b>权限</b>:看自己 schema 的对象没问题,看别人 schema 的对象要
    /// <c>SELECT_CATALOG_ROLE</c>(或对该对象的显式权限),否则 ORA-31603 "object not found" ——
    /// 那句报错的字面意思是"对象不存在",很容易被当成对象真的没了,错误面要认得这个组合;
    /// ③ 默认输出<b>带一大段存储子句</b>(<c>PCTFREE</c> / <c>TABLESPACE</c> / <c>SEGMENT ATTRIBUTES</c>),
    /// 想去掉要先调 <c>DBMS_METADATA.SET_TRANSFORM_PARAM</c>,那是一次独立的 PL/SQL 调用,
    /// 不在这一格能表达的范围里。【TODO】
    /// </para>
    /// <para>
    /// 对象类型串是 <c>DBMS_METADATA</c> 自己的词表:表是 <c>TABLE</c>、视图是 <c>VIEW</c>、
    /// <b>物化视图是 <c>MATERIALIZED_VIEW</c>(带下划线,不是空格)</b>。【未验证】
    /// 认不出的类别如实返回 <see langword="null" />,<b>不拼一段半成品 DDL</b> ——
    /// 一段少了约束、少了注释的 DDL,用户复制走会真的建错表。
    /// </para>
    /// </remarks>
    public override string? ShowCreateSql(SqlObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        string? objectType = target.Kind switch
        {
            SqlObjectKind.Table => "TABLE",
            SqlObjectKind.View => "VIEW",
            SqlObjectKind.MaterializedView => "MATERIALIZED_VIEW",
            _ => null
        };
        if (objectType is null)
        {
            return null;
        }
        // 名字与属主是**值**不是标识符(GET_DDL 的入参是 VARCHAR2),所以走 Literal 而不是 QuoteIdentifier。
        // 传下去的是数据字典里的原样形态 —— 不做任何大小写规范化,理由见 Delimiters 上的长注。
        string owner = string.IsNullOrEmpty(target.Schema)
            ? "SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')"
            : Literal(target.Schema);
        return $"SELECT DBMS_METADATA.GET_DDL({Literal(objectType)}, {Literal(target.Name)}, {owner}) FROM DUAL";
    }

    /// <summary>
    /// 把 <see cref="SqlObject.Schema" /> 折成绑定变量的值。
    /// <para>
    /// 空 schema 折成 <see langword="null" />(而不是空串)是必须的:Oracle 把空串<b>当作 NULL</b> 存,
    /// 于是 <c>OWNER = ''</c> 恒为 unknown、永远匹配不到任何行,而
    /// <see cref="OwnerParam" /> 的 <c>COALESCE</c> 也就永远回落不到当前 schema ——
    /// 表现是"schema 没传时对象树恒空",而且一个异常都不抛。
    /// </para>
    /// <para>
    /// <b>⚠ 顺带把本方言最大的那个坑再说一遍:这里不 <c>UPPER()</c>。</b> 理由见
    /// <see cref="ReadColumnsAsync" />。
    /// </para>
    /// </summary>
    /// <param name="schema">对象的 schema;可能为空。</param>
    /// <returns>绑定值。</returns>
    private static object? OwnerParameter(string? schema) =>
        string.IsNullOrEmpty(schema) ? null : schema;

    /// <summary>把 <see cref="RelationsSql" /> 的类别序号映射成对象类别。</summary>
    /// <param name="kind">1 = 表、2 = 视图、3 = 物化视图。</param>
    /// <returns>对象类别。</returns>
    private static SqlObjectKind KindOf(int kind) => kind switch
    {
        2 => SqlObjectKind.View,
        3 => SqlObjectKind.MaterializedView,
        _ => SqlObjectKind.Table
    };

    /// <summary>
    /// 打开 <c>LONG</c> 列的完整取值。
    /// <para>
    /// <b>ODP.NET 托管版的 <c>InitialLONGFetchSize</c> 默认是 0</b> —— 不是"取一点点",
    /// 是**一个字节都不取**,于是 <c>DATA_DEFAULT</c> 读回来是空串。设成 <c>-1</c> 表示取完整值。
    /// </para>
    /// <para>
    /// 用反射而不是引用 <c>OracleCommand</c>:方言包**不引用任何驱动类型**,
    /// 与 <c>SqlExceptionTranslator</c> 同一条纪律 —— 引用了,这份包就得跟着驱动版本走。
    /// 属性不在(换了驱动、或换了实现)就当没这回事,不抛 —— 最坏结果只是默认值那一格是空的。
    /// </para>
    /// </summary>
    /// <param name="command">要改的命令。</param>
    private static void EnableLongFetch(DbCommand command) =>
        command.GetType()
            .GetProperty("InitialLONGFetchSize", BindingFlags.Public | BindingFlags.Instance)
            ?.SetValue(command, -1);

    /// <summary>
    /// 读列。
    /// <para>
    /// <b>⚠⚠ 本方言最大的坑就在这个方法的 <c>WHERE</c> 上:这里<b>不</b>对表名做 <c>UPPER()</c>。</b>
    /// </para>
    /// <para>
    /// Oracle 的规则是:<b>不加引号的标识符被折成大写并以大写存进数据字典;加引号的原样存。</b>
    /// 两者可以并存 —— <c>create table orders(...)</c> 与 <c>create table "orders"(...)</c>
    /// 在同一个 schema 里是<b>两张不同的表</b>,字典里分别是 <c>ORDERS</c> 与 <c>orders</c>。
    /// </para>
    /// <para>
    /// 于是"顺手加个 <c>UPPER(c.TABLE_NAME) = UPPER(:p1)</c> 让用户少打几个字"这个诱人的写法,
    /// 会一次性造成三件事:
    /// ① <b>把两张真实存在的表混成一张</b> —— 两行都匹配,列表直接翻倍且分不清谁是谁;
    /// ② <b>让小写表永远打不开</b> —— 用户点 <c>orders</c>,拿回来的是 <c>ORDERS</c> 的结构,
    /// 拼出的 <c>SELECT * FROM "ORDERS"</c> 查的是另一张表(这正是 §5.4.5 在 PG 上真机坐实的
    /// "树上画得出来、一点开就报表不存在"的 Oracle 版本);
    /// ③ 让索引的函数化(<c>UPPER(TABLE_NAME)</c>)废掉字典视图上的索引,大库上明显变慢。
    /// </para>
    /// <para>
    /// 所以本包的纪律是<b>原样匹配</b>:喂进来的名字来自 <see cref="ListRelationsAsync" />,
    /// 而那份名字直接读自数据字典,天然就是存储形态,不需要也不允许再规范化。
    /// 用户手打的名字(搜索框、SQL 里的表名)由上层负责按 Oracle 规则解析成存储形态,
    /// <b>那件事不该在这一层猜。</b>
    /// </para>
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="parameters">属主、对象名。</param>
    /// <param name="primaryKey">主键列集合(来自 <see cref="ReadPrimaryKeyAsync" />)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>列。</returns>

    private static async Task<IReadOnlyList<SqlColumn>> ReadColumnsAsync(
        DbConnection connection,
        object?[] parameters,
        HashSet<string> primaryKey,
        CancellationToken cancellationToken)
    {
        SqlColumn Map(DbDataReader r)
        {
            // SequentialAccess:严格按列序读,一列只读一次。DATA_DEFAULT(LONG)在最后一格。
            int ordinal = Int(r, 0);
            string name = Str(r, 1);
            string type = Str(r, 2);
            string typeOwner = Str(r, 3);
            long length = LongOrNull(r, 4) ?? 0;
            long? precision = LongOrNull(r, 5);
            long? scale = LongOrNull(r, 6);
            long charLength = LongOrNull(r, 7) ?? 0;
            string charUsed = Str(r, 8);
            bool nullable = Bool(r, 9);                 // NULLABLE 是 'Y' / 'N'
            bool identity = Bool(r, 10);                // IDENTITY_COLUMN 是 'YES' / 'NO'
            bool generated = Bool(r, 11);               // VIRTUAL_COLUMN 是 'YES' / 'NO'
            string comment = Str(r, 12);
            string? rawDefault = StrOrNull(r, 13);

            // DATA_DEFAULT 存的是用户写的原文,Oracle 常在末尾留一个换行。
            // 修剪之后为空 = 没有默认值(Oracle 里"默认值是空串"这件事不存在:空串就是 NULL)。
            string? defaultValue = rawDefault?.Trim();
            if (string.IsNullOrEmpty(defaultValue))
            {
                defaultValue = null;
            }

            // **自增列与生成列的 DATA_DEFAULT 必须清掉。**
            // identity 列那一格装的是 `"APP"."ISEQ$$_78901".nextval`(系统序列,名字每次建表都不同);
            // 虚拟列那一格装的是**生成表达式**,不是默认值。
            // 两者原样交给表设计器,生成的 DDL 都会建出一张跟原表不一样的表 ——
            // PG 包在 attgenerated 那一格做的是同一件事。
            if (identity || generated)
            {
                defaultValue = null;
            }

            return new SqlColumn(
                name,
                ordinal,
                ComposeType(type, typeOwner, length, precision, scale, charLength, charUsed),
                nullable,
                primaryKey.Contains(name),
                identity,
                generated,
                defaultValue,
                defaultValue is not null && !IsLiteralDefault(defaultValue),
                comment);
        }

        try
        {
            return await QueryAsync(connection, ColumnsRichSql, Map, parameters, cancellationToken, EnableLongFetch).ConfigureAwait(false);
        }
        catch (DbException)
        {
            // 12c 以下没有 IDENTITY_COLUMN 这一列(ORA-00904)。见 ColumnsPortableSql。【未验证】
            return await QueryAsync(connection, ColumnsPortableSql, Map, parameters, cancellationToken, EnableLongFetch).ConfigureAwait(false);
        }
    }

    /// <summary>读主键列。见 <see cref="PrimaryKeySql" /> 上关于"为什么不从索引里取"的说明。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="parameters">属主、对象名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>主键列名集合(<b>序数比较</b>:Oracle 上大小写不同的两个列名是两个列)。</returns>
    private static async Task<HashSet<string>> ReadPrimaryKeyAsync(
        DbConnection connection, object?[] parameters, CancellationToken cancellationToken)
    {
        List<string> names = await QueryAsync(
            connection,
            PrimaryKeySql,
            r => Str(r, 0),
            parameters,
            cancellationToken).ConfigureAwait(false);
        return new(names, StringComparer.Ordinal);
    }

    /// <summary>
    /// 读索引。
    /// <para>
    /// <b>函数索引一律报 0 列,而不是把隐藏列名冒充成列名。</b>
    /// Oracle 实现函数索引的办法是在表上偷偷加一个隐藏虚拟列 <c>SYS_NC00007$</c>,
    /// <c>ALL_IND_COLUMNS</c> 里露出来的就是这个名字。
    /// <see cref="SqlTableSchema.TryGetRowKey" /> 在没有主键时会拿"第一个有列的唯一索引"当行定位键 ——
    /// 把 <c>SYS_NC00007$</c> 交上去,网格就会拼出 <c>WHERE "SYS_NC00007$" = ?</c> 这种
    /// 打不中行(或者打中一片)的 <c>UPDATE</c>。列数为 0 的索引会被 <c>TryGetRowKey</c> 跳过,
    /// 而 <see cref="SqlIndex.Definition" /> 照样把隐藏列名写出来 ——
    /// 用户看得见"这里有个函数索引",回写用不上,这才是对的组合(MySQL 包做的是同一件事)。
    /// </para>
    /// <para>
    /// <b>【TODO】表达式原文在 <c>ALL_IND_EXPRESSIONS.COLUMN_EXPRESSION</c> 里,本版没取</b>:
    /// 那一列也是 <c>LONG</c>,和 <c>DATA_DEFAULT</c> 撞同一个 <c>InitialLONGFetchSize</c> 的坑,
    /// 在契约能表达"读 LONG"之前取回来大概率是空串。
    /// </para>
    /// <para>
    /// <b>⚠ 降序索引</b>:Oracle 把 <c>(col DESC)</c> 也实现成函数索引(<c>INDEX_TYPE</c> 会写
    /// <c>FUNCTION-BASED NORMAL</c>),但 <c>ALL_IND_COLUMNS</c> 上这一列露的是真列名还是隐藏列名,
    /// <b>没有真机可验</b>。所以判据故意<b>不看 <c>INDEX_TYPE</c></b>,只看列名长不长得像隐藏列 ——
    /// 这样两种情况都是对的:真列名就当列用,隐藏列名就当表达式。【未验证】
    /// </para>
    /// </summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="target">宿主对象(定义原文里要写出限定名)。</param>
    /// <param name="parameters">属主、对象名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>索引,主键索引排在最前。</returns>
    private static async Task<List<SqlIndex>> ReadIndexesAsync(
        DbConnection connection,
        SqlObject target,
        object?[] parameters,
        CancellationToken cancellationToken)
    {
        List<IndexRow> rows = await QueryAsync(
            connection,
            IndexesSql,
            r => new IndexRow(
                Str(r, 0),
                string.Equals(Str(r, 1), "UNIQUE", StringComparison.Ordinal),
                Str(r, 2),
                Str(r, 3),
                Bool(r, 4),
                Int(r, 5),
                Str(r, 6),
                Str(r, 7)),
            parameters,
            cancellationToken).ConfigureAwait(false);

        return Fold(
            rows,
            row => row.Name,
            (name, rawParts) =>
            {
                // SQL 里已经 ORDER BY COLUMN_POSITION 了,这里再排一次是因为复合索引的列序
                // **就是索引的语义**(前缀匹配只认最左几列),而"排序在别处"是一条守不住的约定。
                IndexRow[] parts = [.. rawParts.OrderBy(p => p.Position)];
                IndexRow head = parts[0];
                bool functional = parts.Any(p => IsHiddenExpressionColumn(p.Column));
                // Kind 用逗号拼一串**机器可读**的标记而不是自然语言:界面要按标记上色 / 加图标,
                // 而文案要过 Loc —— 把中文烧进数据层,五种语言就只剩一种(PG 包同规则)。
                string kind = head.IndexType;
                if (functional)
                {
                    kind += ",expression";
                }
                if (string.Equals(head.Status, "UNUSABLE", StringComparison.OrdinalIgnoreCase))
                {
                    // 不可用索引优化器根本不碰。不标出来的话,用户会盯着一个"存在但从不生效"的索引查半天慢查询。
                    kind += ",unusable";
                }
                return new SqlIndex(
                    name,
                    functional ? [] : [.. parts.Select(p => p.Column)],
                    head.IsUnique,
                    head.IsPrimary,
                    kind,
                    Definition(target, name, head, parts));
            });
    }

    /// <summary>读外键。见 <see cref="ForeignKeysSql" /> 上的两跳说明与已知边界。</summary>
    /// <param name="connection">已打开的连接。</param>
    /// <param name="parameters">属主、对象名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>外键。</returns>
    private static async Task<List<SqlForeignKey>> ReadForeignKeysAsync(
        DbConnection connection, object?[] parameters, CancellationToken cancellationToken)
    {
        List<ForeignKeyRow> rows = await QueryAsync(
            connection,
            ForeignKeysSql,
            r => new ForeignKeyRow(
                Str(r, 0), Str(r, 1), Int(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), Str(r, 6)),
            parameters,
            cancellationToken).ConfigureAwait(false);

        return Fold(
            rows,
            row => row.Name,
            (name, rawParts) =>
            {
                // 两端**同序**是外键正确性的全部:本表第 n 列对目标第 n 列。
                // SQL 已经按 POSITION 排过,这里再固化一次(PG 包在 unnest 那里守的是同一条)。
                ForeignKeyRow[] parts = [.. rawParts.OrderBy(p => p.Position)];
                return new SqlForeignKey(
                    name,
                    [.. parts.Select(p => p.Column)],
                    parts[0].ReferencedSchema,
                    parts[0].ReferencedTable,
                    [.. parts.Select(p => p.ReferencedColumn)],
                    // DELETE_RULE 已经是 SQL 关键字形态('CASCADE' / 'SET NULL' / 'NO ACTION'),原样透传。
                    parts[0].DeleteRule,
                    NoUpdateRule);
            });
    }

    /// <summary>
    /// 拼一列的<b>完整原生形态</b>(<c>VARCHAR2(50 CHAR)</c>、<c>NUMBER(12,3)</c>)。
    /// <para>
    /// 这一格是本模型刻意与 <c>DbMaintenance</c> 划清界限的地方:把长度 / 精度 / 标度拆成三个字段单存,
    /// 结果就是 §3.7 那一串真机事故(<c>text</c> 长度恒 0、<c>datetime(3)</c> 的 3 被当成长度)。
    /// 用户要看的就是"这一列声明成什么样",那是<b>一个字符串</b>。
    /// </para>
    /// <para>
    /// 逐类说明(全部对着 Oracle 官方类型语法写,<b>【未验证】</b>):
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>CHAR</c> / <c>VARCHAR2</c> / <c>VARCHAR</c>:<b>必须带 <c>BYTE</c> / <c>CHAR</c> 限定词</b>。
    /// <c>CHAR_USED='C'</c> 时长度在 <c>CHAR_LENGTH</c>、单位是字符;<c>'B'</c> 时在 <c>DATA_LENGTH</c>、
    /// 单位是字节。这不是修饰:AL32UTF8 下 <c>VARCHAR2(50 BYTE)</c> 只装得下 16 个汉字,
    /// 而 <c>VARCHAR2(50 CHAR)</c> 装得下 50 个 —— 少写这个词,用户就会按错的容量设计表。
    /// </description></item>
    /// <item><description>
    /// <c>NCHAR</c> / <c>NVARCHAR2</c>:恒是字符语义,<b>不写限定词</b>(写了反而不是合法 DDL),
    /// 长度取 <c>CHAR_LENGTH</c>(<c>DATA_LENGTH</c> 是字节数,AL16UTF16 下是它的两倍)。
    /// </description></item>
    /// <item><description>
    /// <c>NUMBER</c>:四种形态要分开 —— 光秃秃的 <c>NUMBER</c>(精度标度都是 NULL,浮动小数)、
    /// <c>NUMBER(*,0)</c>(<c>INTEGER</c> 的存储形态:精度 NULL 而标度 0)、
    /// <c>NUMBER(p)</c>(标度 0)、<c>NUMBER(p,s)</c>。标度可以是<b>负数</b>(<c>NUMBER(12,-2)</c>),
    /// 所以判据是"标度非 0",不是"标度大于 0"。
    /// </description></item>
    /// <item><description>
    /// <c>FLOAT</c>:括号里是<b>二进制</b>精度(<c>FLOAT(126)</c> = <c>DOUBLE PRECISION</c>),在 <c>DATA_PRECISION</c>。
    /// </description></item>
    /// <item><description>
    /// <c>TIMESTAMP</c> 系与 <c>INTERVAL</c> 系:精度<b>已经烧在 <c>DATA_TYPE</c> 文本里</b>
    /// (字典里存的就是 <c>TIMESTAMP(6) WITH LOCAL TIME ZONE</c> / <c>INTERVAL DAY(2) TO SECOND(6)</c>)。
    /// 再按 <c>DATA_PRECISION</c> 拼一次就会拼出 <c>TIMESTAMP(6)(6)</c> ——
    /// 所以判据是"类型名里已经有括号就原样返回",一条规则盖住整个家族。
    /// </description></item>
    /// <item><description>
    /// <c>DATA_TYPE_OWNER</c> 非空 = 用户自定义类型(对象类型 / 集合类型),
    /// 要带上属主(<c>APP.ADDRESS_T</c>),否则在别的 schema 里是另一个类型。
    /// </description></item>
    /// </list>
    /// </summary>
    /// <param name="type">DATA_TYPE。</param>
    /// <param name="typeOwner">DATA_TYPE_OWNER;内建类型为空。</param>
    /// <param name="length">DATA_LENGTH(字节)。</param>
    /// <param name="precision">DATA_PRECISION。</param>
    /// <param name="scale">DATA_SCALE。</param>
    /// <param name="charLength">CHAR_LENGTH(字符)。</param>
    /// <param name="charUsed">CHAR_USED('B' / 'C' / 空)。</param>
    /// <returns>完整原生形态。</returns>
    private static string ComposeType(
        string type,
        string typeOwner,
        long length,
        long? precision,
        long? scale,
        long charLength,
        string charUsed)
    {
        if (!string.IsNullOrEmpty(typeOwner))
        {
            return $"{typeOwner}.{type}";
        }
        if (type.Contains('(', StringComparison.Ordinal))
        {
            // TIMESTAMP(6) WITH TIME ZONE / INTERVAL DAY(2) TO SECOND(6) —— 字典里已经是完整形态。
            return type;
        }

        return type switch
        {
            "CHAR" or "VARCHAR2" or "VARCHAR" =>
                string.Equals(charUsed, "C", StringComparison.Ordinal)
                    ? $"{type}({Num(charLength)} CHAR)"
                    : $"{type}({Num(length)} BYTE)",
            "NCHAR" or "NVARCHAR2" => $"{type}({Num(charLength)})",
            "RAW" or "UROWID" => $"{type}({Num(length)})",
            "NUMBER" => ComposeNumber(precision, scale),
            "FLOAT" => precision is null ? type : $"FLOAT({Num(precision.Value)})",
            // DATE / CLOB / NCLOB / BLOB / BFILE / LONG / LONG RAW / ROWID / XMLTYPE /
            // BINARY_FLOAT / BINARY_DOUBLE / JSON / BOOLEAN…:声明里就没有修饰,原样即完整形态。
            _ => type
        };
    }

    /// <summary><c>NUMBER</c> 的四种形态。见 <see cref="ComposeType" /> 上的逐类说明。</summary>
    /// <param name="precision">DATA_PRECISION。</param>
    /// <param name="scale">DATA_SCALE。</param>
    /// <returns>完整原生形态。</returns>
    private static string ComposeNumber(long? precision, long? scale)
    {
        if (precision is null)
        {
            // 两个都 NULL 才是浮动小数的 NUMBER;精度 NULL 而标度有值,是 INTEGER / NUMBER(*,0) 的存储形态。
            return scale is null ? "NUMBER" : $"NUMBER(*,{Num(scale.Value)})";
        }
        return scale is null or 0
            ? $"NUMBER({Num(precision.Value)})"
            : $"NUMBER({Num(precision.Value)},{Num(scale.Value)})";
    }

    /// <summary>
    /// 判一段 <c>DATA_DEFAULT</c> 原文是不是<b>纯字面量</b>。
    /// <para>
    /// 这一格的全部意义在于把 <c>SYSDATE</c>(每行求值一次)与字符串 <c>'SYSDATE'</c>
    /// (一个碰巧长这样的常量)分开 —— 表设计器要靠它决定生成 DDL 时加不加引号,
    /// 加错一边就是"默认值变成了固定的那一秒"或者"建表直接语法错"。
    /// </para>
    /// <para>
    /// Oracle 这一格比 PG 好判:它存的是<b>用户写的原文</b>,没有 PG 那种
    /// <c>'new'::character varying</c> 的规范化尾巴,所以只要认三种形态 ——
    /// 单引号串(含双写转义)、数字、<c>NULL</c> 关键字。
    /// <c>q'[...]'</c> 这种替代引号语法本版不认,会被判成表达式;
    /// <b>判错成表达式只是少加一对引号,判错成字面量会把一段可执行的东西当常量写回去</b>,
    /// 所以拿不准的时候一律倒向"表达式"。
    /// </para>
    /// </summary>
    /// <param name="source">默认值原文(已修剪)。</param>
    /// <returns>是不是纯字面量。</returns>
    private static bool IsLiteralDefault(string source)
    {
        if (source.Length == 0)
        {
            return false;
        }
        if (source[0] == '\'')
        {
            int i = 1;
            while (i < source.Length)
            {
                if (source[i] != '\'')
                {
                    i++;
                }
                else if (i + 1 < source.Length && source[i + 1] == '\'')
                {
                    i += 2;   // 连续两个单引号是被转义的一个,不是收尾。
                }
                else
                {
                    break;
                }
            }
            // 闭合引号必须正好是最后一个字符;后面还有东西(拼接、函数调用)就是表达式。
            return i == source.Length - 1 && source[i] == '\'';
        }
        if (source.Equals("NULL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 数字字面量:可选正负号 + 数字 + 至多一个小数点 + 至多一段指数。
        // 写得比"允许出现这几个字符"严,是因为后者会把 `1+1`、`1-SYSDATE` 这种表达式误判成常量。
        int index = source[0] is '+' or '-' ? 1 : 0;
        bool digits = false;
        bool dot = false;
        bool exponent = false;
        for (; index < source.Length; index++)
        {
            char c = source[index];
            if (char.IsAsciiDigit(c))
            {
                digits = true;
                continue;
            }
            if (c == '.' && !dot && !exponent)
            {
                dot = true;
                continue;
            }
            if (c is 'e' or 'E' && digits && !exponent)
            {
                exponent = true;
                if (index + 1 < source.Length && source[index + 1] is '+' or '-')
                {
                    index++;
                }
                continue;
            }
            return false;
        }
        return digits;
    }

    /// <summary>
    /// 这个列名是不是 Oracle 给函数索引偷加的隐藏虚拟列(<c>SYS_NC00007$</c>)。
    /// <para>
    /// 判据只看形态不看 <c>INDEX_TYPE</c>,理由见 <see cref="ReadIndexesAsync" />。
    /// <c>SYS_NC</c> 前缀 + <c>$</c> 结尾是 Oracle 保留的系统命名,用户建不出同名列
    /// (<c>$</c> 虽然合法,但 <c>SYS_</c> 开头的名字 Oracle 自己占着),所以这条过滤不会误伤。【未验证】
    /// </para>
    /// </summary>
    /// <param name="columnName">ALL_IND_COLUMNS.COLUMN_NAME。</param>
    /// <returns>是隐藏表达式列则 <see langword="true" />。</returns>
    private static bool IsHiddenExpressionColumn(string columnName) =>
        columnName.StartsWith("SYS_NC", StringComparison.Ordinal) && columnName.EndsWith('$');

    /// <summary>
    /// 索引定义原文。<b>升降序必须体现出来</b> —— <c>(a ASC, b DESC)</c> 与 <c>(a, b)</c>
    /// 是两个不同的索引(前者才能免排序地服务 <c>ORDER BY a, b DESC</c>),而列名清单里它们长得一模一样。
    /// 唯一性、不可用状态同理:这些差别只有原文说得清。
    /// </summary>
    /// <param name="target">宿主对象。</param>
    /// <param name="name">索引名。</param>
    /// <param name="head">该索引的第一行(取索引级属性)。</param>
    /// <param name="parts">按序的列。</param>
    /// <returns>贴近 <c>CREATE INDEX</c> 写法的定义原文。</returns>
    private static string Definition(SqlObject target, string name, IndexRow head, IReadOnlyList<IndexRow> parts)
    {
        var text = new StringBuilder("CREATE ");
        if (head.IsUnique)
        {
            _ = text.Append("UNIQUE ");
        }
        _ = text.Append("INDEX ").Append(Quote(name)).Append(" ON ");
        _ = text
            .Append(string.IsNullOrEmpty(target.Schema) ? "" : $"{Quote(target.Schema)}.")
            .Append(Quote(target.Name))
            .Append(" (");
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                _ = text.Append(", ");
            }
            IndexRow part = parts[i];
            _ = text.Append(Quote(part.Column));
            // DESCEND 是 'ASC' / 'DESC';两个都写出来,免得读的人要去猜"没写是不是就是升序"。
            _ = text.Append(' ').Append(string.Equals(part.Descend, "DESC", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC");
        }
        _ = text.Append(')');

        List<string> notes = [];
        if (head.IsPrimary)
        {
            notes.Add("PRIMARY KEY");
        }
        if (!string.IsNullOrEmpty(head.IndexType) && !string.Equals(head.IndexType, "NORMAL", StringComparison.Ordinal))
        {
            notes.Add(head.IndexType);
        }
        if (string.Equals(head.Status, "UNUSABLE", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("UNUSABLE");
        }
        if (notes.Count > 0)
        {
            _ = text.Append(" /* ").Append(string.Join(", ", notes)).Append(" */");
        }
        return text.ToString();
    }

    /// <summary>双引号包一层(供定义原文用;与 <see cref="DialectPackBase.QuoteIdentifier" /> 同规则)。</summary>
    /// <param name="identifier">标识符。</param>
    /// <returns>转义后的形态。</returns>
    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>
    /// 把一段文本包成 SQL <b>字符串字面量</b>(不是标识符!)。
    /// <para>
    /// 只给 <see cref="EstimateRowCountSql" /> / <see cref="ShowCreateSql" /> 这类
    /// "接口不给参数通道"的地方用。Oracle 的字符串字面量里<b>反斜杠不是转义符</b>
    /// (与 MySQL 的默认 <c>sql_mode</c> 正好相反,那边要绕到十六进制去),
    /// 唯一需要转义的就是单引号,加倍即可 —— 而且这条规则不随任何服务端参数变。
    /// </para>
    /// </summary>
    /// <param name="value">原文。</param>
    /// <returns>SQL 字面量。</returns>
    private static string Literal(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>整串都是十进制数字(且非空)。</summary>
    /// <param name="value">文本。</param>
    /// <returns>是则 <see langword="true" />。</returns>
    private static bool IsDecimal(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }
        foreach (char c in value)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>数字转文本(恒用不变文化,避免某些区域设置给整数加千位分隔符)。</summary>
    /// <param name="value">数值。</param>
    /// <returns>文本。</returns>
    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>索引查询的一行(每个索引每一列一行)。</summary>
    /// <param name="Name">索引名。</param>
    /// <param name="IsUnique">UNIQUENESS = 'UNIQUE'。</param>
    /// <param name="IndexType">INDEX_TYPE(NORMAL / BITMAP / FUNCTION-BASED NORMAL / DOMAIN…)。</param>
    /// <param name="Status">STATUS(VALID / UNUSABLE / N/A)。</param>
    /// <param name="IsPrimary">是否由主键约束支撑。</param>
    /// <param name="Position">COLUMN_POSITION(1 起)。</param>
    /// <param name="Column">COLUMN_NAME。</param>
    /// <param name="Descend">DESCEND('ASC' / 'DESC')。</param>
    private sealed record IndexRow(
        string Name,
        bool IsUnique,
        string IndexType,
        string Status,
        bool IsPrimary,
        int Position,
        string Column,
        string Descend);

    /// <summary>外键查询的一行(每条外键每一列一行)。</summary>
    /// <param name="Name">约束名。</param>
    /// <param name="DeleteRule">DELETE_RULE。</param>
    /// <param name="Position">列在约束里的序号。</param>
    /// <param name="Column">本表列。</param>
    /// <param name="ReferencedSchema">目标 schema。</param>
    /// <param name="ReferencedTable">目标表。</param>
    /// <param name="ReferencedColumn">目标列。</param>
    private sealed record ForeignKeyRow(
        string Name,
        string DeleteRule,
        int Position,
        string Column,
        string ReferencedSchema,
        string ReferencedTable,
        string ReferencedColumn);
}
