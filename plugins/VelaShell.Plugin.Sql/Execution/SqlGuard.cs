using System.Text;

namespace VelaShell.Plugin.Sql.Execution;

/// <summary>一条语句的危险等级(设计文档 §7.6)。</summary>
internal enum SqlRisk
{
    /// <summary>只读:<c>SELECT</c> / <c>EXPLAIN</c> / <c>SHOW</c> / <c>DESCRIBE</c>。</summary>
    Green,

    /// <summary>有界的写:带 <c>WHERE</c> 的 <c>UPDATE</c>/<c>DELETE</c>、<c>INSERT</c>。</summary>
    Yellow,

    /// <summary>无界或不可逆:无 <c>WHERE</c> 的 <c>UPDATE</c>/<c>DELETE</c>、<c>DROP</c>、<c>TRUNCATE</c>、<c>ALTER</c>、<c>GRANT</c>。</summary>
    Red
}

/// <summary>护栏对一条语句的判决。</summary>
/// <param name="Risk">危险等级。</param>
/// <param name="RequiresConfirmation">要不要弹确认框。</param>
/// <param name="RequiresTypedName">要不要让用户手打对象名(最高一档)。</param>
/// <param name="BlockedByReadOnly">是否被只读连接在**发出之前**拒掉。</param>
/// <param name="Verb">识别出的首动词(用于文案)。</param>
/// <param name="TargetObject">识别出的目标对象名(用于"键入对象名"那一档);拿不到时为空。</param>
internal sealed record SqlVerdict(
    SqlRisk Risk,
    bool RequiresConfirmation,
    bool RequiresTypedName,
    bool BlockedByReadOnly,
    string Verb,
    string TargetObject)
{
    /// <summary>能不能直接发出去(不需要任何用户交互)。</summary>
    public bool CanRunSilently => !RequiresConfirmation && !BlockedByReadOnly;
}

/// <summary>
/// 三档护栏。
/// <para>
/// 两条设计取舍值得记下来:
/// ① <b>护栏跟着连接走,不是一个全局开关</b> —— 一个开关只有两种状态,而用户同时开着开发库和生产库;
/// ② <b>只读是在发出之前拒</b>,不靠数据库权限 —— 用户可能就是用 root 连的。
/// </para>
/// </summary>
internal static class SqlGuard
{
    /// <summary>判一条语句。</summary>
    /// <param name="sql">语句原文。</param>
    /// <param name="environment">连接的环境标记。</param>
    /// <param name="readOnly">连接是不是只读。</param>
    /// <param name="dialect">方言。</param>
    /// <returns>判决。</returns>
    public static SqlVerdict Judge(string sql, SqlEnvironment environment, bool readOnly, SqlDialect dialect)
    {
        string stripped = StripLiteralsAndComments(sql ?? "", dialect);
        // **按括号深度分词**。两处非它不可:
        //  ① WITH 的 CTE 体里有 SELECT,只找"第一个 DML 词"会把 `with x as (select…) delete from t`
        //     判成查询 —— 那是一条能删光整张表的语句;
        //  ② 子查询里的 WHERE 不是本语句的 WHERE。`update t set a = (select max(x) from u where y=1)`
        //     是**无界 UPDATE**,不按深度过滤就会被判成有界,直接放行。
        (string Word, int Depth)[] tokens = Tokenize(stripped);
        string[] top = [.. tokens.Where(t => t.Depth == 0).Select(t => t.Word)];
        if (tokens.Length == 0)
        {
            return new(SqlRisk.Green, false, false, false, "", "");
        }

        string verb = tokens[0].Word;
        // WITH ... SELECT / WITH ... DELETE:真正的动词是 CTE 收尾之后、**深度 0** 上的那个。
        if (verb is "WITH")
        {
            string tail = top.Skip(1).FirstOrDefault(w => w is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE")
                          ?? "SELECT";
            // **但收尾动词不够。** PostgreSQL 的「数据修改型 CTE」把写动词藏在括号里:
            //
            //     with d as (delete from orders returning id) select count(*) from d
            //
            // 深度 0 上收尾的是 SELECT,而这条语句会**删光整张表**。只看深度 0 的话它拿绿档,
            // 于是既绕过只读连接(下面那句 blocked),又会被当成绿档送去跑 EXPLAIN ANALYZE ——
            // 「只想看看计划」也真删。所以:**任何深度**上出现写动词,它就是这条语句的真动词。
            //
            // 代价是偏严:CTE 里带 WHERE 的删除也会判红(WHERE 在深度 1,Classify 只看深度 0),
            // 方向与本文件既有的取舍一致 —— 这道闸宁可多拦。
            verb = FirstWriteVerb(tokens) ?? tail;
        }

        // EXPLAIN 默认只出计划、不执行,所以它是绿档。**但 ANALYZE 会真跑一遍** ——
        // PG 与 MySQL 都是,对 DELETE 就是真删。`IDialectPack.ExplainSql` 上写着
        // 「绿档之外的语句一律不给 analyze」,而那条纪律只有在这里把真动词挖出来之后才成立;
        // 否则 `explain analyze delete from orders` 自己就是绿档,闸门等于没有。
        if (verb is "EXPLAIN" && Array.Exists(tokens, t => t.Word is "ANALYZE"))
        {
            // 选项可以写成 `EXPLAIN (ANALYZE, BUFFERS) …` —— 那时 ANALYZE 在深度 1,
            // 而真动词仍在深度 0。认不出内层动词就落到空串,由 Classify 的 default 兜成黄档。
            verb = top.Skip(1).FirstOrDefault(w => !IsExplainOption(w)) ?? "";
        }

        SqlRisk risk = Classify(verb, top, out bool unbounded);
        string target = TargetOf(verb, top);

        // 只读连接:黄红两档在发出之前被拒。
        bool blocked = readOnly && risk != SqlRisk.Green;

        bool confirm = risk switch
        {
            SqlRisk.Green => false,
            // 黄档只在生产上拦 —— 在开发库上给每条 UPDATE 弹框,用户三分钟就会开始无脑点确定,
            // 那时护栏已经失效了,只是没人发现。
            SqlRisk.Yellow => environment == SqlEnvironment.Production,
            _ => true
        };
        bool typed = risk == SqlRisk.Red && environment == SqlEnvironment.Production;

        return new(risk, confirm, typed, blocked, verb, target)
        {
            // unbounded 只影响文案(§7.6 要求红档确认框里显示预估影响行数),
            // 分级本身已经把它算进 Classify 了。
        };

        // 找出**任何深度**上的第一个写动词;没有就返回 null。
        // 存在的理由只有一个:PG 的数据修改型 CTE 会把 DELETE / UPDATE / INSERT 藏在括号里。
        // (局部函数上不能挂 XML 文档注释 —— CS1587。)
        static string? FirstWriteVerb((string Word, int Depth)[] words)
        {
            foreach ((string word, _) in words)
            {
                if (word is "INSERT" or "UPDATE" or "DELETE" or "MERGE")
                {
                    return word;
                }
            }
            return null;
        }

        // EXPLAIN 后面那些**修饰词**(不是被解释的那条语句的动词)。
        static bool IsExplainOption(string word) =>
            word is "ANALYZE" or "ANALYSE" or "VERBOSE" or "COSTS" or "BUFFERS" or "TIMING"
                or "SUMMARY" or "SETTINGS" or "WAL" or "GENERIC_PLAN" or "FORMAT"
                or "TEXT" or "JSON" or "XML" or "YAML" or "TRUE" or "FALSE" or "ON" or "OFF"
                or "PLAN" or "FOR" or "EXTENDED" or "PARTITIONS";

        static SqlRisk Classify(string verb, string[] words, out bool unbounded)
        {
            unbounded = false;
            switch (verb)
            {
                case "SELECT":
                    // `SELECT … INTO t`(SQL Server / PG 上是**建表**)与
                    // `SELECT … INTO OUTFILE '…'`(MySQL 上是**往服务端磁盘写文件**)
                    // 都是写操作,而首词仍然是 SELECT。放过去的话,一条"只读"连接
                    // 就能在服务端建表、落文件 —— 而用户以为自己开的是只读。
                    return words.Contains("INTO") ? SqlRisk.Yellow : SqlRisk.Green;

                case "EXPLAIN" or "SHOW" or "DESCRIBE" or "DESC" or "PRAGMA" or "ANALYZE":
                    return SqlRisk.Green;

                case "UPDATE" or "DELETE":
                    // **无 WHERE 的 UPDATE/DELETE 是红档**。这是本护栏最重要的一条:
                    // 它与 DROP 的区别只是"看起来无害",后果是一样的。
                    unbounded = !words.Contains("WHERE");
                    return unbounded ? SqlRisk.Red : SqlRisk.Yellow;

                case "INSERT" or "REPLACE" or "MERGE" or "UPSERT" or "COPY":
                    return SqlRisk.Yellow;

                case "DROP" or "TRUNCATE" or "ALTER" or "GRANT" or "REVOKE" or "RENAME":
                    return SqlRisk.Red;

                case "CREATE":
                    // CREATE 本身不毁数据,但 CREATE OR REPLACE 会覆盖既有对象。
                    return words.Length > 2 && words[1] is "OR" && words[2] is "REPLACE" ? SqlRisk.Red : SqlRisk.Yellow;

                case "CALL" or "EXEC" or "EXECUTE" or "DO":
                    // 存储过程里什么都可能发生,我们看不见 —— 按写操作对待。
                    return SqlRisk.Yellow;

                case "SET" or "USE" or "BEGIN" or "COMMIT" or "ROLLBACK" or "START" or "SAVEPOINT":
                    return SqlRisk.Green;

                default:
                    // 认不出的一律按黄档:宁可多问一次,也不要把一条我们看不懂的语句静默发到生产库上。
                    return SqlRisk.Yellow;
            }
        }

        static string TargetOf(string verb, string[] words)
        {
            int at = verb switch
            {
                "DELETE" => Array.IndexOf(words, "FROM") + 1,
                "INSERT" or "REPLACE" => Array.IndexOf(words, "INTO") + 1,
                "UPDATE" or "TRUNCATE" or "CALL" or "EXEC" or "EXECUTE" => 1,
                "DROP" or "ALTER" or "RENAME" => 2,
                _ => -1
            };
            return at > 0 && at < words.Length ? words[at] : "";
        }
    }

    /// <summary>
    /// 判一整批(编辑器里一次执行多条)。取最高危的那一档。
    /// <para>
    /// 多语句在 PG 上是隐式事务(第 2 条失败会把第 1 条一起回滚),在 MSSQL 上不是 ——
    /// 所以确认框上除了危险等级,还得说清"这批在本方言下是否原子"(§5.3)。
    /// </para>
    /// </summary>
    /// <param name="statements">语句。</param>
    /// <param name="environment">环境。</param>
    /// <param name="readOnly">是否只读。</param>
    /// <param name="dialect">方言。</param>
    /// <returns>逐条判决与整体判决。</returns>
    public static (IReadOnlyList<SqlVerdict> PerStatement, SqlVerdict Overall) JudgeBatch(
        IReadOnlyList<SqlStatement> statements,
        SqlEnvironment environment,
        bool readOnly,
        SqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(statements);
        List<SqlVerdict> verdicts = [.. statements.Select(s => Judge(s.Text, environment, readOnly, dialect))];
        if (verdicts.Count == 0)
        {
            return ([], new(SqlRisk.Green, false, false, false, "", ""));
        }
        SqlVerdict worst = verdicts.OrderByDescending(v => (int)v.Risk).ThenByDescending(v => v.RequiresTypedName).First();
        return (verdicts, worst with
        {
            RequiresConfirmation = verdicts.Any(v => v.RequiresConfirmation),
            RequiresTypedName = verdicts.Any(v => v.RequiresTypedName),
            BlockedByReadOnly = verdicts.Any(v => v.BlockedByReadOnly)
        });
    }

    /// <summary>
    /// 把字符串字面量与注释挖空,只留结构。
    /// <para>
    /// 不做这一步的后果很具体:<c>DELETE FROM t WHERE note = 'no where clause'</c>
    /// 会因为字面量里有 <c>where</c> 而被判成有界(反过来也一样),护栏就废了。
    /// </para>
    /// </summary>
    private static string StripLiteralsAndComments(string sql, SqlDialect dialect)
    {
        var sb = new StringBuilder(sql.Length);
        for (int i = 0; i < sql.Length;)
        {
            char c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i = SkipToLineEnd(sql, i);
                sb.Append(' ');
                continue;
            }
            if (c == '#' && dialect == SqlDialect.MySql)
            {
                i = SkipToLineEnd(sql, i);
                sb.Append(' ');
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                sb.Append(' ');
                continue;
            }
            if (c is '\'' or '"' or '`')
            {
                // 标识符也一并挖空:一张叫 `where` 的表不该把护栏带跑。
                i = SkipQuoted(sql, i, c);
                sb.Append(" x ");
                continue;
            }
            if (c == '[' && dialect == SqlDialect.SqlServer)
            {
                int end = sql.IndexOf(']', i + 1);
                i = end < 0 ? sql.Length : end + 1;
                sb.Append(" x ");
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static int SkipToLineEnd(string sql, int i)
    {
        int n = sql.IndexOf('\n', i);
        return n < 0 ? sql.Length : n;
    }

    private static int SkipQuoted(string sql, int i, char quote)
    {
        int j = i + 1;
        while (j < sql.Length)
        {
            if (sql[j] == '\\' && quote == '\'' && j + 1 < sql.Length)
            {
                j += 2;
                continue;
            }
            if (sql[j] == quote)
            {
                if (j + 1 < sql.Length && sql[j + 1] == quote)
                {
                    j += 2;
                    continue;
                }
                return j + 1;
            }
            j++;
        }
        return sql.Length;
    }

    /// <summary>分词并记下每个词所在的括号深度(深度 0 = 本语句自己的成分)。</summary>
    private static (string Word, int Depth)[] Tokenize(string stripped)
    {
        List<(string, int)> tokens = [];
        var word = new StringBuilder();
        int depth = 0;
        foreach (char c in stripped)
        {
            if (c is '(' or ')')
            {
                Flush(tokens, word, depth);
                depth += c == '(' ? 1 : -1;
                depth = Math.Max(depth, 0);
                continue;
            }
            if (char.IsWhiteSpace(c) || c is ',' or ';')
            {
                Flush(tokens, word, depth);
                continue;
            }
            word.Append(char.ToUpperInvariant(c));
        }
        Flush(tokens, word, depth);
        return [.. tokens];

        static void Flush(List<(string, int)> into, StringBuilder buffer, int depth)
        {
            if (buffer.Length == 0)
            {
                return;
            }
            into.Add((buffer.ToString(), depth));
            buffer.Clear();
        }
    }
}
