# 数据库管理插件（SqlSugar）：调研与设计

> 编写日期：2026-08-18　真机复测：2026-08-19　基准代码：`feat/redis-workspace-plugin` @ `f2d1f48`
>
> 前置阅读：[`Redis客户端插件化调研与设计.md`](Redis客户端插件化调研与设计.md)（工作台能力域是它逼出来的，
> 本文是它的第二个使用者）、[`plugins/dev-guide.md`](plugins/dev-guide.md)（能力面与纪律）、
> [`../DESIGN.md`](../DESIGN.md)（设计令牌与组件规范）。
>
> 本文回答四个问题：**用 SqlSugar 做一个对标 Navicat/DBeaver/DataGrip 的数据库管理插件，能做到什么程度**；
> **SqlSugar 到底给了什么、又在哪里靠不住**（第三节是实测，不是读文档）；**宿主要不要再开口子**；
> **界面怎么设计才配得上"键盘优先、信息密度高"**。
>
> **第三节的每一条结论都有真机作证。** 初版（08-18）只有 SQLite 一台真机，其余全是离线反射与 SQL 字符串比对；
> 08-19 这一轮补上了四台真服务器 —— **PostgreSQL 18.1**、**SQL Server 2025 (17.0.4025.3)**、
> **MySQL 8.4.11**、SQLite —— 跑出 254 条结论，再由两个独立复核者用不同测法重测，
> **confirmed 98 / partly 17 / refuted 2**。凡本轮被推翻或修正的地方，正文用「初版说 X，真机是 Y」的写法留痕，
> 不做无声修改 —— 因为**读者需要知道哪些结论是推的、哪些是量的**。
>
> **Oracle 曾经没有真机，2026-08-20 补上了**（见 §11.4 与附录 B.1）。
> 文中仍标着「离线推断」的 Oracle 结论**多数已被真机覆盖**，但**没有逐条回改**——
> 以 §11.4 的实测清单为准；那份清单里没提到的 Oracle 结论，仍按离线推断对待。
>
> 探针源码不在仓库里（调研产物，未污染工程树），复现见附录 A。
> 若要长期保留，建议在动工时把它收编成 `tests/VelaShell.Plugin.Sql.Tests`（§十）。

---

## 一、结论先行

**能做。但初版那句「这一次宿主一行都不用改」被真机推翻了 —— 宿主至少要开一个口子。**

Redis 插件是拿命换来的第一个工作台插件——它逼出了「工作台连接类型」与「声明式 SSH 隧道」两个扩展点。
数据库插件是这两个扩展点的**第二个使用者，白得**：连接对话框、声明式表单、凭据 AES 加密落盘、
登录弹窗、证书信任、云同步、会话树、最近连接、经跳板机的本地转发——全部零改动复用。

改口径的原因只有一个，而且是实测逼出来的：**只要真连过一次数据库，插件 ALC 就再也回收不了**
（四种驱动全中，见 §3.9），后果是**插件目录里的 DLL 删不掉、卸载插件静默失败**——
真连一次 PG 是 3 个 DLL 删不掉，SQL Server 是 7 个。这不是"值得开的小口子"，是"不开就有可见 bug"。

但有一条仍然必须说在最前面：

> **SqlSugar 是驱动装配层与元数据兜底层，不是数据库管理工具的引擎。**

这不是贬低。SqlSugar 用一个 `DbType` 枚举就把 18 种方言的驱动装配、连接串解析、
方言分页与基础元数据全接管了——**光"18 种数据库开箱即连"这一件事，自己写要几个月**。
可它是 ORM，它的目标是"让业务代码不关心方言"；而数据库管理工具的价值恰恰在**暴露方言**。
两者的方向正好相反。真机把这条边界钉得比初版更死。

### 1.1 实测速览（本轮重写）

| 结论 | 初版（08-18） | 真机（08-19） |
|---|---|---|
| **`DbMaintenance` 的方言覆盖** | SQL Server 23/23、MySQL/PG 22/23、Oracle 21/23、SQLite 12/23（离线读模板属性） | **分子分母都错**：基类上是 **33** 条模板不是 23；35 个 `DbType` 里**只有 18 个能装出 provider**；扣掉 21 格"有模板但是别人方言/死代码/语法坏损"之后是 **502/594 = 84.5%**。最狠的一格：Oracle 与达梦的 `BackupDataBaseSql` 装的是 **T-SQL 的 `BACKUP DATABASE`**，而且**没人拦**，会真发到服务端（§3.3） |
| **未捆绑方言的报错**（新增） | "报错一律说 `MySqlConnectorCore`，插件要自带映射表" | 现象对、成因错，而且**后果严重得多**：`InstanceFactory` 的静态状态**只写不清**——碰一次未捆绑方言（如手滑选了 ClickHouse），**之后 SQLite/MySQL/PG/SqlServer 全部装不出 provider，报的还是 ClickHouse 的名字，重启插件才恢复**。解药是一行静态复位（§3.3） |
| **元数据会静默说谎** | SQLite 上 `GetIndexList` 返回 `"0"`；`autoincrement` 列 `IsIdentity=false`；列长度恒 0 | **四台真机全中，且更严重**：`IsIdentity(表,列)` 在 PG / SQL Server / MySQL 上**恒返回 True**，还会永久污染 `GetIsIdentities` 的缓存（§3.5–§3.7） |
| **取消完全不可用** | `Ado.CancellationToken` 被忽略；裸 `DbCommand` 也没用，跑满 139.6 秒 | **推翻一半**：令牌**只在 async 路径生效**（`GetDataTableAsync` 是唯一无效的那个，因为它是 `Task.Run` 套同步实现）；`DbCommand.Cancel()` 在 PG/MSSQL/MySQL 上**真能打断服务端**（客户端 0~39 ms、服务端 32~233 ms）；SQLite 要直调 `sqlite3_interrupt`，20 ms 打断（§3.10） |
| **「取消不成就 Dispose 连接」** | 初版的兜底护栏 | **必须删掉**：Dispose 一条有在途命令的连接会**永久挂死调用线程**（栈停在 Winsock `recv`），而且服务端照跑到底（§3.10） |
| **ALC 卸载** | MySqlConnector / SqlClient 造连接对象第 2~3 轮 GC 回收，判为 OK | **那是"没真 Open"的假象**。真 Open + 真查询后 SQLite/PG/MSSQL/MySQL **全部 40 轮 GC 仍存活**；关池 + `ClearAllPools` 12 个变体**全军覆没**（§3.9） |
| **外键、执行计划、会话/锁、权限** | `IDbMaintenance` 里一个都没有 | 不变，并新增六项：视图/物化视图的列、自定义 schema、存储过程清单、计算列/生成列、某列是否自增、可用类型清单（§2.3） |
| **谓词层（§5.4）** | 离线生成的 SQL 全对 | **骨架成立**（18 种条件模型逐条与手写 SQL 对账一致），但**五个洞只有真跑才看得见**：`In` 不参数化且不带 `N` 前缀 → SQL Server 上中文筛选静默给错答案；`AS(表名)` 完全不转义 → 实测真删光了整张表；`ConditionalTree` 子树的 `OR` 被静默吞成 `AND`；`Updateable(字典)` 漏写 `WhereColumns` 生成无 WHERE 全表 UPDATE；按低基数列排序翻页**重复 14528 行同时漏 14528 行**（§5.4） |

### 1.2 四层架构（保留，第 ④ 层的理由变了）

```
┌ 界面层（Avalonia，插件自绘 + 结果网格）──────────────────────┐
│  对象树 · SQL 编辑器 · 结果网格 · 对象设计器 · 运维面           │
├ 能力层 ───────────────────────────────────────────────────────┤
│  ① SqlSugar 装配层：驱动装配 / 连接串 / 元数据兜底 / 代码生成      │
│  ② SqlSugar 谓词层：IConditionalModel + 字典 CRUD ——            │
│     "我们替用户生成的那些 SQL"(浏览表、过滤、排序、翻页、改一格)    │
│     一律经它出方言 —— 但必须绕开 §5.4 记的那五个洞               │
│  ③ DialectPack：每方言一份 SQL 资产（DDL 脚本、外键、执行计划、     │
│     会话与锁、权限、慢查询、取消语句、会话 id 语句、静态类型表）    │
│  ④ 裸 ADO（DbCommand）：用户手敲的 SQL 的执行与取消 ——           │
│     不是因为"Ado 不认令牌"(async 路径认)，而是因为              │
│     取消要拿到那个 DbCommand，且 AOP 不覆盖裸 DbCommand          │
└───────────────────────────────────────────────────────────────┘
```

**范围**：目标是 SqlSugar 的全部 35 种 `DbType`，分四级推进（§6.2）。
一等公民（v1）：MySQL 系、PostgreSQL 系、SQL Server、Oracle、SQLite。

**工作量**：插件侧约 12–14k 行走到 M4，再加约 3–4k 行走完 T1/T2；
宿主侧**不再是 0**——口子零（驱动前缀归宿主）与口子一（卸载隔离区）合计约 150–250 行（§四）。

---

## 二、为什么是 SqlSugar

### 2.1 三条路都摆出来

| 路线 | 代价 | 收益 |
|---|---|---|
| **A. 裸 ADO.NET + 每方言手写** | 每加一种库：找驱动包、写连接串拼装、写元数据查询、写分页——18 种就是 18 遍 | 完全可控，没有中间层说谎 |
| **B. SqlSugar** | 多一个 3MB 依赖 + 一层要提防的抽象 | 驱动装配、连接串、方言分页、基础元数据、代码生成全现成 |
| **C. `DbProviderFactories` + 自己攒方言表** | 得自己维护驱动包清单与 provider 注册 | 比 A 省一点，比 B 差很多 |

选 **B**，但**只用它擅长的那一半**。三条实测出来的账：

1. **驱动装配这件事，比想象中脏得多。** 自己做要维护 18 份 provider 映射，还要处理"用户没装这个驱动包"的降级——
   这套 SqlSugar 已经写好了（虽然它的报错信息是错的，见 §3.3）。
2. **方言分页是个隐形大坑。** 一件事自己写就是 18 份分页模板。
   但真机发现它在 SQL Server 上的兜底排序键是 `GetDate()`，且用户 SQL 带 `ORDER BY` 时**直接失败**（§7.3）——
   所以这条收益要打折：**分页只在"我们自己生成的 SQL"上可用，用户手敲的 SQL 得自己拼 `OFFSET/FETCH`**。
3. **许可相容。** 仓库 `LICENSE` 是 **MIT**（NuGet 包 nuspec 的 `licenseUrl` 仍指向 Apache-2.0，
   属元数据陈旧，两者都与本仓库 AGPL-3.0 + 商业双许可相容）。与 `StackExchange.Redis`（MIT）、
   `AWSSDK.S3`（Apache-2.0）同一条口径。

### 2.2 SqlSugar 给的六样东西

| 能力 | 实测 |
|---|---|
| **驱动装配** | 18 种方言的驱动随 `SqlSugarCore` 捆绑，`new SqlSugarClient(...)` 即得连接对象 |
| **原生 SQL 全面** | 参数化 `GetDataTable`、流式 `GetDataReader`、多结果集 `GetDataSetAll`、`SqlQuery<dynamic>`、事务 |
| **方言分页** | 任意 SELECT 包一层子查询做服务端分页 + 总数（代价见 §7.3：每翻一页多一条全表 `COUNT`） |
| **运行期谓词**（§5.4） | `IConditionalModel` 家族 + 无实体入口 `Queryable(表名, 别名)`：字段名运行期才知道也能拼条件 |
| **字典驱动 CRUD**（§5.4.3） | `Insertable(dict)` / `Updateable(dict).WhereColumns(主键)` / `Storageable`——四台真机跑通并回读校验 |
| **AOP 可观测** | 12 个钩子。`OnLogExecuting` 拿得到**参数化 SQL + 参数表**；`OnExecutingChangeSql` 能在发出前改写 SQL（这是绕开写死模板的逃生门，§3.6） |

**初版措辞要改的一处**：`OnLogExecuting` 给的是「参数化 SQL + 参数数组」，**不是拼好值的最终 SQL**
（PG/MSSQL/MySQL 三台真机一致）。§8.3 的审计设计如果想存"最终 SQL"，得自己把参数回填。

**AOP 的三个盲区（真机新增）**：

- `DbMaintenance` 的 `isCache:true` 读路径**完全不过 AOP**（冷进程实测：`isCache:true` → AOP 看到 0 条 SQL，`isCache:false` → 2 条）；
- `GetPrimaries` / `GetIsIdentities` / `IsIdentity` 返回了正确结果却**一条 SQL 都不产生**；
- **裸 `DbCommand` 一条都不触发**——而 §3.10 的取消方案必须走裸 `DbCommand`。

外加 `DbFirst` 代码生成（PG 上实测生成 3611 字符实体，表注释进了 XML summary）——
**这是商业工具里没有、而 VelaShell 白得的一项**。右键一张表 → "生成 C# 实体"。
DataGrip 要装插件、Navicat 根本没有。（注意 §3.6：SQL Server 上跨 schema 同名表的列污染会一路传进生成的实体。）

### 2.3 SqlSugar 不给的东西（真机扩充）

| 管理工具必需 | `IDbMaintenance` 里有吗 | 只能怎么办 |
|---|---|---|
| 外键 / 表关系 | **没有** | 方言包 |
| 建表 DDL 原文（`SHOW CREATE TABLE`） | **没有**（只有"按列表建表"） | 方言包 |
| 执行计划 | **没有** | 方言包 |
| 会话、锁、阻塞链 | **没有** | 方言包 |
| 用户与权限 | **没有** | 方言包 |
| 索引的列与唯一性 | 只有 `GetIndexList(t)` 返回 `List<string>`。SQLite 返回 `"0"`；**PG 返回真索引名但丢掉唯一性与列**（它的 SQL 明明 `select` 了 `indexdef` 却没进返回值）；**MySQL 是 `SHOW INDEX` 的 `Key_name` 原样输出，不去重，7 个索引报成 11 项**；**SQL Server 按索引列出行不去重，4 个索引返回 7 项，且分不出聚集/唯一/筛选/INCLUDE** | 方言包 |
| 存储过程/函数**正文** | 只有名字，**而且 PG 上连名字都没有**——`GetProcList()` 发出的 SQL 把**数据库名当成 schema 名**去 `pg_namespace` 里找，恒返回空 | 方言包 |
| **某列是否自增**（新增） | **`IsIdentity(表,列)` 在 PG / SQL Server / MySQL 上恒返回 `True`**，与该列是否自增无关（MySQL 上它一条 SQL 都不发）；更坏的是它会**污染缓存**：调过之后 `GetIsIdentities(表)` 退化成"最后一次 `IsIdentity` 问过的那一列" | **只用 `GetIsIdentities`，且必须在任何 `IsIdentity` 之前调**；稳妥做法是走方言包 |
| **视图 / 物化视图的列**（新增） | **没有**：`GetColumnInfosByTableName` 对 view/matview 返回 **0 列且不抛异常**。PG 的物化视图更彻底——既不在 `GetTableInfoList` 也不在 `GetViewInfoList`，对象树上整个消失 | 方言包 |
| **自定义 schema**（新增） | **完全不可达且静默**。PG：`GetTableInfoList` 只列 `public`，传 `"app.t"` 返回 0 列 / 0 项 / False，一个异常都不抛，唯一开关是连接串的 `search_path`。SQL Server 更糟：读侧只按名字过滤、**不带 schema 也不区分表/视图**，于是 `dbo.OrderDetail`(16 列) 被 `sales.OrderDetail` 污染成 17 列 | 方言包 |
| **计算列 / 生成列**（新增） | `DbColumnInfo` 里**根本没有这个概念** | 方言包（可写列集合必须先剔除生成列） |
| **可用数据类型清单**（新增） | `GetDbTypes()` 返回的不是"这个方言支持哪些类型"，而是"**这个库当前用到了哪些类型**"，还**随着建表变多** | 方言包提供静态类型表 |
| 取消一条查询 | 令牌**只在 async 路径生效**，`GetDataTable/GetDataSet` 系列例外 | 见 §3.10 |

**细腰照旧**：凡是"每种库长得不一样、而用户就是要看那个不一样"的，一律进方言包；
凡是"方言无关、写十八遍毫无价值"的，交给 SqlSugar。
**但真机之后这条线要往方言包那边挪一大截**——上表里"新增"的六行全是从 SqlSugar 那边划过来的。

**注意这张表说的是"元数据与运维能力"，不是"SQL 生成能力"。** 后者 SqlSugar 给得相当足（§5.4）。

---
## 三、实测：把 SqlSugar 塞进插件 ALC，对着四台真服务器跑一遍

调研阶段最怕的是"文档说能，真跑不通"。所以先写探针，再写设计。
初版只有 SQLite 一台真机——**这一节的大部分修正，都是"有了真服务器"之后才浮出来的**。

### 3.1 探针与真机环境

探针目录下两个项目：

- **Probe**（`EnableDynamicLoading=true`，引 `SqlSugarCore 5.1.4.217`）——**与真插件同构**：
  生成 `deps.json`、自带依赖复制到输出目录；
- **Runner**——复刻宿主 `PluginAssemblyLoadContext`（可收集 ALC + `AssemblyDependencyResolver` +
  `Avalonia` 前缀回落），用反射调 Probe，**Runner 不引用 SqlSugar**，跑完 `Unload()` 并做 40 轮 GC 的可回收性检查。

运行时：`.NET 11.0.0-preview.7.26381.103`（SDK `11.0.100-preview.7`，与仓库 `global.json` 一致）。

本轮的真机（全部本地，无网络延迟——这一点在引用性能数字时必须一起读，见附录 B）：

| 真机 | 版本 | 怎么来的 |
|---|---|---|
| **PostgreSQL** | 18.1 | 本机 `D:\Program Files\pgsql\bin` 的二进制，在临时目录 `initdb` 了一个独立集群，跑在 `127.0.0.1:55432`。**没动**用户那个装好但停着的 `postgresql16` 服务 |
| **SQL Server** | 2025 (RTM-CU3) 17.0.4025.3 Express | LocalDB。初版记的"LocalDB 被登录触发器挡住"是**实例级**的——`sqllocaldb create VelaSpike` 新建一个实例就绕过去了，而且拿到的是 2025 不是 2019 |
| **MySQL** | 8.4.11 | podman 容器（`docker.m.daocloud.io/library/mysql:8.4`），`127.0.0.1:13306`。默认 `caching_sha2_password`，**明文 TCP 直连即可，不需要 TLS**；容器默认自带自签证书、`SslMode=Preferred` 实际协商上了 TLS 1.3 |
| **SQLite** | `Microsoft.Data.Sqlite 10.0.9` | 进程内 |
| **Oracle** | —— | **没有**。见附录 B |

### 3.2 装载与原生驱动：通过

```
- SqlSugar 程序集: SqlSugar 5.1.4.217
- 所属 ALC: plugin:velashell.sql.spike        ← 没有漏进默认 ALC
[alc] 原生库 e_sqlite3                     -> …/runtimes/win-x64/native/e_sqlite3.dll
[alc] 原生库 Microsoft.Data.SqlClient.SNI.dll -> …/runtimes/win-x64/native/Microsoft.Data.SqlClient.SNI.dll
```

数据库驱动是插件生态里**第一次出现原生依赖**（Redis 的 `StackExchange.Redis`、S3 的 AWSSDK 都是纯托管）。
`AssemblyDependencyResolver` 按 `deps.json` 的 RID 图把两个原生库都解析到了——
**宿主的插件装载机制不需要为数据库插件做任何改动**。

**而且真机新增一条好消息：原生库本身不影响 ALC 卸载。** 只把 `e_sqlite3.dll` / SNI 经插件 ALC 的
`LoadUnmanagedDll` 装进来、不建任何连接、不碰驱动任何托管类型 → **第 2 轮 GC 就回收**。
初版猜测的"原生库持有托管回调"被排除，泄漏 100% 来自托管侧的静态注册（§3.9）。

`SqlSugarCore` 捆绑的驱动，**注意包版本与程序集版本不是一回事**（写文档别混）：

| 包 | 包版本 | 程序集版本 | 覆盖 |
|---|---|---|---|
| `Microsoft.Data.SqlClient` | 5.2.2 | **5.0.0.0** | SQL Server（原生 SNI） |
| `MySqlConnector` | 2.2.5 | **2.0.0.0** | MySQL 系 |
| `Npgsql` | 5.0.18 | 5.0.18.0 | PostgreSQL 系 |
| `Oracle.ManagedDataAccess.Core` | 23.8.0 | — | Oracle（5.13MB，最大的一个） |
| `Microsoft.Data.Sqlite` | 10.0.9 | 10.0.9.0 | SQLite（原生 e_sqlite3） |
| `SqlSugarCore.Dm` / `SqlSugarCore.Kdbndp` / `Oscar.Data.SqlClient` | — | — | 达梦 / 人大金仓 / 神通 |

初版那条 ⚠ 警告（"`Npgsql 5.0.18` 是 2021 年的版本，要在 M0 验证与新版的 API 兼容性"）
**已经结案**，见 §3.8。

> **⚠ 一条 M0 动工时才发现的安全问题**：`SqlSugarCore 5.1.4.217` 经
> `Microsoft.Data.Sqlite 10.0.9` 传递依赖到 **`SQLitePCLRaw.bundle_e_sqlite3 2.1.11`**，
> 而它带的原生 `e_sqlite3` 有一个**已知高危漏洞**（`GHSA-2m69-gcr7-jv3q`，`NU1903`）。
> 这不是洁癖问题：那是要随插件分发到每一台用户机器上的**原生库**。
> 处置：插件显式引 `SQLitePCLRaw.bundle_e_sqlite3 3.0.3` 往上覆盖
> （`Microsoft.Data.Sqlite` 的约束是 `>= 2.1.11`，覆盖不冲突），
> **并由 SQLite 的真机连通性用例守着**——原生库换版本是运行期才暴露的那类改动。
> 已验证：升版后 `dotnet build` 零 `NU1903`，SQLite 连接与探活照常。

### 3.3 离线方言矩阵：分子分母都错，而且它有毒

`DbMaintenanceProvider` 把每种维护动作拆成一个 `protected abstract` 的 SQL 模板属性，
方言不支持就抛 `NotSupportedException`。初版据此反射画出"35 方言 × 23 模板"的覆盖矩阵。
本轮把这张表逐格重算，**结论是：每一格都能复现，但这张表测的根本不是它声称的那件事。**

#### 分母错了：基类上是 33 条模板，不是 23 条

反射 `DbMaintenanceProvider` 的 `DeclaredOnly` 属性得 **33 条**（32 个 `abstract` + 1 个 `virtual`）。
初版只探了 23 条，漏掉的 10 条全是真实能力信号——`CheckSystemTablePermissionsSql`、
`IsAnyTableRemarkSql` / `IsAnyColumnRemarkSql`、`DeleteTableRemarkSql` / `DeleteColumnRemarkSql`、
`CreateTableIdentity` 等。换算后 **SQLite 由 12/23 变 18/33**。

#### 分子也错了：21 格是"有模板但不能用"

按四类**机器可验证**的判据（异方言模板逐字节相同 / IL 里方法被覆写成抛 / 幻影语句 / 模板混入非 SQL 文本）重算：

| 口径 | 名义 | 实际 |
|---|---|---|
| 文档的 23 列 × 18 个真正可达的 DbType | 384/414 = 92.8% | **363/414 = 87.7%** |
| 基类真实的 33 列 × 18 个可达 DbType | 523/594 = 88.0% | **502/594 = 84.5%** |
| 33 列 × 文档口径的 35 个 DbType | — | **502/1155 = 43.5%** |

最后一行才是真正该记住的：**35 个 `DbType` 里只有 18 个能装出 provider**，另外 16 个取
`DbMaintenance` 就抛，`Custom` 在构造函数就抛。

逐方言（名义 23 → 实际 23）：SqlServer 23→23；MySQL 系 8 个 DbType 22→21；PG 系 4 个 22→21；
人大金仓 22→21；神通 22→**19**；Oracle 21→**19**；达梦 20→**18**；SQLite 12→11。

#### 六条硬证据（每条都是"有模板但假的"）

1. **`mysqldump` 不止装在 PG 上**：人大金仓（Kdbndp）与神通（Oscar）的 `BackupDataBaseSql`
   与 `MySqlDbMaintenance` **逐字节相同**（`mysqldump.exe {0} -uroot -p > {1}`）。
   三家的 `BackupDataBase()` 都被覆写成抛异常，模板永远读不到——**至少还接得住**。
2. **更坏的一档：Oracle 与达梦的 `BackupDataBaseSql` 是 T-SQL，而且没人拦。**
   两者模板都等于 SqlServer 的 `USE master;BACKUP DATABASE {0} TO disk = '{1}'`，
   而 IL 显示它俩**没有覆写** `BackupDataBase()`，走基类模板驱动实现——
   **会真的把一条 T-SQL 发到 Oracle 服务端去**。
3. **`CreateDataBaseSql` 名义 8/8，实际只有 4 家能用**：Oracle 的 IL 里是
   `"Oracle no support create database"`、达梦是 `"dm no support create database, only create schema"`、
   神通直接 `throw NotSupportedException`、SQLite 的 `CreateDatabase()` 只有路径正则
   （它建的是文件，SQLite 根本没有 `CREATE DATABASE` 语句）。
4. **神通的 `RenameTableSql` 里混进了两个汉字**：`alter table 表名 {0} to {1}`
   （码点核对 0x8868 0x540D，不是编码显示问题）。发出去必然语法错。
5. **MySQL 的"库备份"开箱不可用**：`BackupDataBase()` 的 IL 字面量是
   `"Need MySqlBackup.NET.MySqlConnector"`——**该包不在 `SqlSugarCore` 的 12 条依赖里，也不在输出目录里**。
6. **反方向的一处低估**：SQLite 其实**有**真备份实现（PRAGMA 系），而模板矩阵把它记成"缺库备份"。

初版举的两条反例仍然成立并入档：

- **假阳性**：PG 的 `BackupDataBaseSql` 非空却是 MySQL 的命令行，真调抛 `PgSql BackupDataBase NotSupported`。
- **假阴性**：矩阵说 MySQL"缺列注释"，**真机上 `AddColumnRemark` 写得进也读得回**——
  MySQL 的 provider 直接重写了方法、不走那个模板属性。

**纪律**：离线反射矩阵**既不能用来确认，也不能单独用来排除**——
"模板缺失"可能是方法被重写了（MySQL 列注释），"模板存在"可能是别人的方言（PG 库备份）。
凡是要写进产品承诺的能力，必须真机验证过。

真机抽样（14 个常用方法）：**PostgreSQL 18.1 与 SQL Server 2025 各 14/14 通过**，SQLite 13/14。
这是抽样不是全量，写文档时要说清楚。

**SQLite 是最弱的那个，偏偏又是最常被顺手打开的那个**——方言包优先级要排在前面。

#### 惰性失败的真实边界（修正初版与本轮 §9.4 的措辞）

**两种失败要分开说，它们的边界和异常类型都不同**：

| 情形 | 何时失败 | 抛什么 |
|---|---|---|
| **未捆绑方言**（缺 `SqlSugar.XxxCore` 这类 provider 扩展包，如 Access/ClickHouse/DB2） | **取 `db.DbMaintenance` 就抛**（`db.Ado` 同样） | `SqlSugar.SqlSugarException: Not Found ….dll` —— SqlSugar 把真实装载失败吞了 |
| **捆绑方言但驱动 dll 被删**（§9.4 的"可选驱动包"路线，如手删 `Oracle.ManagedDataAccess.dll`） | 构造与 `DbMaintenance` 都成功，**第一次访问 `db.Ado.Connection` 才抛** | `FileNotFoundException` |

好消息：前一种**在建连之前就能测出来**，选完方言即可试探一次，不必真去连服务器。
**但这个试探有毒，见下。**

#### ⚠ `InstanceFactory` 的静态状态会被永久污染，一次失败拖垮整个 ALC

`SqlSugar.InstanceFactory` 上有两个 **static 且只写不清** 的成员：
`_CustomDllName`（公开可读写属性 `CustomDllName`）与 `CustomDlls`（只 `Add`，从不 `Clear`）。
实测时间线：

```
（干净进程）Access → FAIL: Not Found SqlSugar.AccessCore.dll
之后：
  Tidb.DbMaintenance      → FAIL: Not Found SqlSugar.AccessCore.dll
  MySql.DbMaintenance     → FAIL: Not Found SqlSugar.AccessCore.dll
  PostgreSQL.DbMaintenance→ FAIL: Not Found SqlSugar.AccessCore.dll
  Sqlite.DbMaintenance    → FAIL: Not Found SqlSugar.AccessCore.dll
  SqlServer.DbMaintenance → FAIL: Not Found SqlSugar.AccessCore.dll
```

**翻译成用户故事**：用户在连接对话框里手滑选了一次 ClickHouse → 报错 → 他改选 SQLite 打开一个 `.db` 文件
→ **SQLite 也打不开了**，报的还是 `Not Found SqlSugar.ClickHouseCore.dll`。**重启插件才能恢复。**

这同时**推翻了初版对那句错误文案的归因**。初版写"报错一律说 `MySqlConnectorCore`"——
现象是对的，成因不是"文案写死"，而是 `GetCustomTypeByClass` 按 `CustomDlls` 的插入顺序
**死在列表头上那一个**；列表头之所以是 `SqlSugar.MySqlConnectorCore`，只因为枚举顺序里
`DbType.MySqlConnector`（一个能正常工作的方言）排在 `Access` 前面，把自己的包名先塞了进去。
干净进程里第一个就碰 `ClickHouse`，报的就是**正确**的 `ClickHouseCore`。
→ **这句报错的包名依赖调用历史，可能对、可能是别人的名字——无论如何都不能透传给用户。**

**解药已验证，一行**：

```csharp
// 每次建 SqlSugarClient 之前、以及每次捕获 SqlSugarException 之后，复位这个静态
SqlSugar.InstanceFactory.CustomDllName = "";     // public static，get/set 都是 public
```

复位之后 `Sqlite.DbFirst` / `Oracle.Ado` / `Dm.DbFirst` 立刻恢复正常。

**三条落地纪律**：① 插件**自带 `DbType → 包名` 映射表**，永远不透传 SqlSugar 的 `Not Found` 文案；
② 连接表单里**未内置的方言直接置灰**，不给"试一下"的机会；
③ 复位动作包在插件自己的 `SqlSugarClient` 工厂里，**每次建 client 前都做一次**（成本是一次静态赋值）。

### 3.4 SQLite 真机：Ado 全通，DbMaintenance 遍地洞

`Ado` 一侧全部通过（参数化 `GetDataTable`、流式 `GetDataReader`、多结果集、事务回滚）。
`DbMaintenance` 一侧：

```
[XX] GetDataBaseList():   NotSupportedException
[XX] RenameColumn:        NotSupportedException
[XX] AddColumnRemark:     NotSupportedException
[OK] GetIndexList(t):     1 项: 0            ← 建的索引叫 ix_spike_users_name，返回的是 "0"
[OK] GetIsIdentities(t):  0 项               ← id 是 autoincrement，没认出来
[OK] GetColumnInfosByTableName: id:INTEGER(0,0) null=True pk=True id=False def=[] desc=-
                                             ← 长度/小数位恒 0、默认值空、注释空
```

**`NotSupportedException` 不可怕，可怕的是 `GetIndexList` 返回 `"0"`。**
抛异常插件能接住并降级；返回一个语法上合法、语义上错误的值，界面会**如实地把假数据画出来**。

**纪律**：凡是要显示给用户看的元数据，**方言包优先，`DbMaintenance` 只做兜底**，
且兜底结果必须过一遍合理性校验。校验不过就走方言包，方言包也没有就**显示"不可用"，而不是显示一个错值**。

真机之后这条纪律要补两点：

- **合理性校验的阈值可以按方言分档**——PG 的列元数据基本可信，不必像 SQLite 那样整片降级；
- **必检的不合理特征里要加一条「所有列都是 identity」**（`IsIdentity` 恒 True 的直接后果）。

两条对 SQLite 的补充（来自 §3.3 的模板复核）：

- **`BackupDataBase: False` 不该记成"缺库备份"**：SQLite 其实**有**真备份实现（PRAGMA 系），
  是模板矩阵在这一格低估了它；
- **`GetIndexList` 返回 `"0"` 的根因**：IL 显示它走的是 `PRAGMA index_list`，
  疑似读了第一列 `seq` 而不是 `name`——**这条是 IL 层推断，还没在真机上二分验证**。

顺带两个编译期小坑：`SqlSugar.DbType` 与 `System.Data.DbType` 同名，必须写别名
`using DbType = SqlSugar.DbType;`；`DbFirst.ToClassStringList()` 返回的是
`List<KeyValuePair<类名, 代码>>`，不是 `List<string>`。

### 3.5 PostgreSQL 18.1 真机（新增）

**总评：PG 上 `DbMaintenance` 比 SQLite 好一个量级，但有 13 处"返回了值而值是错的"。**

**可信的那一半（这是本轮最正面的结论）**：`varchar`/`numeric` 的长度与小数位、常量默认值与函数默认值、
列注释、复合主键、identity 与 serial 全部正确：

```
order_no   type=varchar len=32
title      type=varchar len=50   desc=[标题(varchar50)]
amount     type=numeric len=12 dec=3  desc=[金额 numeric(12,3)]
created_at type=timestamptz null=False def=[now()]
status     def=['new'::character varying]
seq_id     id=True  desc=[GENERATED ALWAYS AS IDENTITY]
legacy_id  id=True  def=[nextval('spike_all_legacy_id_seq'::regclass)]
GetPrimaries = 2 项: tenant_id, order_no
```

对照 `psql \d+` 全部一致。**PG 的表结构只读视图可以直接吃 `DbMaintenance`。**

**说谎的那一半**：

| 方法 | 返回了什么 | 真值 |
|---|---|---|
| `IsIdentity(表,列)` | **恒 True**（8 个列全 True） | 只有 2 列是自增。而且**调过它之后 `GetIsIdentities` 被污染**，退化成"最后问过的那一列"，`isCache:false` 也刷不回来 |
| `GetProcList()` | 恒 0 项 | 库里有一个 PG 11+ 的 `PROCEDURE`。SQL 是 `... WHERE n.nspname = 'spike_pg'` —— **把库名当 schema 名** |
| `GetColumnInfosByTableName(视图)` | 0 列，不抛异常 | 视图有列。列 SQL 的数据源是 `pg_tables`，里面没有视图 |
| 物化视图 | 既不在表清单也不在视图清单 | 存在。对象树上会整个消失 |
| `GetTriggerNames` | 3 项 | 只有 1 个用户触发器，另 2 个是外键的**内部触发器**（SQL 缺 `and not tgisinternal`）——任何有外键的表都会中招 |
| `GetDbTypes()` | 40→46 项，含 `pg_node_tree`/`anyarray`/`USER-DEFINED`/`ARRAY` | 它查的是 `SELECT DISTINCT information_schema.columns.data_type`，即"本库在用的类型"，**建表之后还会变多** |
| `AddDefaultValue` | 返回 **False** | 默认值真的加上了 |
| `AddPrimaryKeys(带名字)` | 返回 True | 名字被塞进 SQL 注释 `/*{1}*/`，约束名其实是 PG 默认名 |
| `SetAutoIncrementInitialValue` | 返回 True | 什么也没做，**还往 `Console.Out` 打了一行 `no support`** |
| 自定义 schema | `GetTableInfoList` 只列 `public`；`"app.t"` → 0 列 / 0 项 / `IsAnyTable=False` | 表存在。SQL 里 schema 是写死的 `nspname='public'` |

**三条会改设计的**：

1. **元数据缓存是进程级、跨 `SqlSugarClient` 实例共享、永不失效的**，而且 `isCache:false`
   拿到的新结果**不回写缓存**。实测：client#1 读到 `id,a` → 加列 `b` → **另一个** client 仍读到 `id,a` →
   同一 client `isCache:false` 读到 `id,a,b` → 再 `isCache:true` 又变回 `id,a`。
   **对象树用 `isCache:true` 就会一直显示旧结构，点"刷新"也治不好。**
   → 硬规则：**所有元数据读取一律 `isCache:false`，缓存由插件自己按会话管**（还能顺便做失效）。
2. **schema 那一级只能靠连接串的 `search_path`**：加上 `;Search Path=app` 之后，
   除 `GetProcList` 外的元数据方法全部跟着换 schema。而**谓词层不受影响**——
   `db.Queryable("app.spike_all","a")` 正确生成 `SELECT * FROM "app"."spike_all" "a"` 并跑通。
   → §7.2 的 schema 一级要么每个 schema 一条连接，要么方言包直查 `pg_catalog`。推荐后者。
3. **捆绑的 Npgsql 5.0.18 读不了用户自定义枚举**：一张带 `enum` 列的表 `select *` 直接抛
   `NotSupportedException: The field 'c_mood' has a type currently unknown to Npgsql (OID 17271)`。
   复核者把触发条件收窄了：**Npgsql 的 PG 类型目录是按进程缓存一次的**，
   只有"在本进程第一次连接之后才新建的"类型才读不出来；由 `psql` 预先建好的 enum 读得正常。
   便宜的解药实测有效：`NpgsqlConnection.ReloadTypes()`，比升版便宜得多。

**写侧（改结构）是 PG 上最干净的一块**：22 个方法里 20 个真的生效，DDL 是正经 PG 语法（带双引号标识符），
复合主键、注释增删、改类型、改列名、备份表、建库都对得上真值。
→ PG 的**表设计器可以把 DDL 生成交给 `DbMaintenance`**（再由 AOP 出预览），
但必须遵守"返回值不可信、以复查真值为准"的纪律，且上表那三个写侧撒谎的方法要自己写。

### 3.6 SQL Server 2025 真机（新增）

**总评：23/23 是真的**（约 50 次调用零 `NotSupportedException`，DDL 系全部执行成功），
**但"支持"不等于"说真话"**。

**最致命一条（初版完全没提）**：`GetColumnInfosByTableName` 的 SQL 只按 `sysobjects.name` 过滤，
**不带 schema，也不区分表/视图**（`xtype IN ('U','V')`），于是同名对象的列被并进同一张表并按列名去重：

```
dbo.OrderDetail 真实 16 列 → SqlSugar 返回 17 列
其中 UserName 的类型/可空/注释被 sales.OrderDetail 的同名列顶掉
污染一路传到 DbFirst：生成的实体带上了根本不存在的属性
```

而且**给 schema 反而更糟**：`("dbo.OrderDetail")` 与 `("sales.OrderDetail")` 都返回 **0 列且不抛异常**；
写成 `("[sales].[OrderDetail]")` 又回到那 17 列的污染集。`GetPrimaries("sales.X")` 返回空且**一条 SQL 都不发**。
schema 支持在 SqlSugar 内部是**分裂**的：写侧（`AddColumn`）会正确拆 schema，读侧完全不拆，
`IsAnyTable` 走 `object_id` 因而把视图也认成表。

其余"返回但错"：

| 方法 | 问题 |
|---|---|
| `IsIdentity(表,列)` | 完全不看列名——凡是存在的列都返回 True，等价于 `IsAnyColumn`。复核者进一步二分出：**只要调过一次 `IsIdentity`，`GetIsIdentities` 就退化成"最后问过的那一列"**（`n=0` 全对 → `n=2` 错 → `n=5` 返回空）。**与 PG 同病，应合并成一条跨方言纪律** |
| `Length` | `nvarchar(max)` / `varchar(max)` / `varbinary(max)` / `xml` 一律 **-1**（`COLUMNPROPERTY('PRECISION')` 直传）。界面直接渲染就是 `-1`。而 `nvarchar(50)` 是 50（不是字节数 100），`datetime2(3)` → `(23,3)` |
| 计算列 | `DbColumnInfo` 里没有这个概念，持久化与非持久化计算列都被报成普通可空 `decimal(23,3)`——表设计器/回写会当成可写列 |
| `IsAnyConstraint` | 实现是 `select object_id(名字)`，对表、视图、存储过程一律返回 True |
| `IsAnyIndex` | 全库按名字 `count`，不限本表——而 SQL Server 的索引名只在表内唯一 |
| `GetTriggerNames` | 不是按宿主表查，而是拿表名去 **LIKE 触发器正文**（`syscomments.text`），必然假阳性 |
| `GetFuncList` | 漏掉多语句表值函数；`GetProcList`/`GetFuncList` 都不带 schema，导致"列得出来但查不到" |
| `GetTableInfoList` | 不输出 schema，两张同名表在清单里是两行一模一样的 `Name`，对象树无法区分 |
| `CreateIndex` | **不转义列名**——T-SQL 保留字列名直接语法错误 |
| `AddDefaultValue` | 把数字也写成字符串字面量 |

**两条初版怀疑的澄清**：

- **`AddColumnRemark` 是好的**：写得进去也读得出来。初版冒烟看到的 `desc=-` 是
  `Entry.cs` 里 `GetColumnInfosByTableName` 排在 `AddColumnRemark` **之前**造成的顺序假象。
  真正的缺陷是**不幂等、且没有 Update 入口**。
- **`master` 库淹死是真的**（`GetViewInfoList` 返回 645 个系统视图），但**定位很精确**：
  用户库是干净的。根因是 `GetTableInfoList`/`GetViewInfoList` 用 `sysobjects` 且不过滤 `is_ms_shipped`。

**一条 SQL Server 2025 特有的新雷**：`systypes` 里 `varbinary` 与新增的 `vector` 共用 `xtype=165`，
而列元数据模板按 `xtype` 内连接 `systypes`，于是**一列变两行、类型名可能被报成 `vector`**。
`GetDbTypes()` 已经能列出 `json` 与 `vector`。

**三个逃生门全部真实可用，插件不必整块绕开 `DbMaintenance`**：
`GetTableInfoList(Func)` 与 `GetColumnInfosByTableName(name, Func)` 能整条替换模板，
`Aop.OnExecutingChangeSql` 能改掉写死的 `GetIndexList`/`GetTriggerNames`。

### 3.7 MySQL 8.4.11 真机（新增）

**总评：读侧比 SQLite 好得多，但有 9 处静默说谎，其中四条是数据/结构损坏级。**

**四条损坏级（必须写进纪律）**：

1. **`DropConstraint(表名, 约束名)` 完全无视传进去的约束名，一律发 `ALTER TABLE x drop primary key;`，然后返回 `True`。**
   实测：让它删一个 CHECK 约束 `ck_spike_v`，它把**主键**删了，CHECK 还在，还告诉你成功了。
   有自增列时它会报 `there can be only one auto column and it must be defined as a key`——
   恰好是"有自增列时报错、没自增列时静默删主键"这种最坏组合。
   → **插件永不调用 `IDbMaintenance.DropConstraint`。**
2. **`UpdateColumn`（改列）只发类型，不重述其它列属性。** MySQL 的 `CHANGE COLUMN` 是整列重定义，于是：
   改前 `` `c` varchar(20) NOT NULL DEFAULT 'dv' COMMENT '原注释' ``
   → 发出 `` alter table `spike_alt` change column `c` `c` varchar(40) DEFAULT NULL ``
   → 改后 `` `c` varchar(40) DEFAULT NULL ``。**注释、默认值、NOT NULL 三样一起没了。**
   → 改列必须由方言包基于完整列快照重建整条定义。
3. **`GetPrimaries` 与 `GetIsIdentities` 共用一个「大小写不敏感、先问先得」的表名缓存。**
   在 `lower_case_table_names=0` 的 Linux MySQL 上（两张只差大小写的表可以并存），
   这会把 A 表的主键当成 B 表的主键返回：库里 `OrderDetail.PK=Id` 与 `orderdetail.PK=k` 并存时，
   先问 `orderdetail` 得 `[k]`，再问 `OrderDetail` **还是 `[k]`**（应为 `Id`）；反过来问则全是 `[Id]`。
   而 `GetColumnInfosByTableName` 却是**大小写敏感**的（全小写 → 0 列）。
   → **网格的主键来源必须走方言包直查 `information_schema.KEY_COLUMN_USAGE`**——
   拿错主键 = UPDATE 打到别的行。
4. **`IsIdentity(表,列)` 恒 True**（一条 SQL 都不发），与 PG/MSSQL 同病。

**`Length` 语义表（MySQL 特有，界面必须知道）**：它不是长度，是从 `COLUMN_TYPE` 括号里
`SUBSTRING` 出来再强转 `decimal` 的：

| 列类型 | SqlSugar 的 `Length` | 真实含义 |
|---|---|---|
| `varchar(50)` / `binary(16)` / `decimal(12,3)` | 50 / 16 / 12(,3) | 对 |
| `text` / `longtext` / `blob` / `json` / `enum` / `set` | **恒 0** | 真值分别是 65535 / 4294967295 / … |
| `datetime(3)` | **3** | 那是秒的小数位，不是长度 |
| `tinyint(1)` | **1** | 那是显示宽度 |

`enum`/`set` 的**取值列表整个丢失**（`DataType` 只剩 `enum`）；
生成列、`ON UPDATE CURRENT_TIMESTAMP` **完全不可见**（SELECT 列表里没有 `EXTRA`、没有 `GENERATION_EXPRESSION`）。
`DefaultValue` 拿得到，但拿的是 `COLUMN_DEFAULT` 原文，**表达式默认值与字符串常量默认值不可区分**——
`CURRENT_TIMESTAMP` 与字符串 `'CURRENT_TIMESTAMP'` 长得一模一样，`bit` 默认值是 `b'0'`。
→ 表设计器生成 DDL 时不能直接把 `DefaultValue` 当字面量加引号。

**MySQL 特有的两个大坑（初版 §5.4.5 只讲了 PG/Oracle 的大小写，这一层完全空缺）**：

- **`lower_case_table_names`**：Linux 默认 0（表名大小写敏感、两张只差大小写的表可并存），
  Windows 默认 1（不敏感、且强制存成小写）。实测在 lctn=0 下，"同一个表名"在插件内部能得到
  **True / 空 / 抛异常三种互相矛盾的答案**（`IsAnyTable` 不敏感、`GetColumnInfos` 敏感、`GetPrimaries` 走缓存）。
  → **同一个插件连 Linux MySQL 和 Windows MySQL 行为不同**，文档与界面都要认这一层。
  （lctn=1/2 本轮**未实测**——改它要重启实例，而容器是共用的。）
- **`sql_mode` 的 `ANSI_QUOTES`**：SqlSugar 硬编码反引号。服务端开了 `ANSI_QUOTES` 之后
  双引号才是标识符——插件应在连上之后探测 `sql_mode`。

**其余可用的**：`GetTableInfoList` 带表注释且正确排除视图；列注释读写往返正常（推翻离线矩阵的"缺列注释"）；
`GetDataBaseList` 把 4 个系统库原样倒出来（对象树要默认折叠）；视图的 `Description` 是假的。

---
### 3.8 驱动升版：两条都能升，但 PG 那条有个进程级陷阱

初版把"`Npgsql` 是否升版覆盖"列为待决事项 3，理由是"SqlSugar 是反射调用它，破坏性改名只在运行期暴露"。
本轮逐版本真机验证，**结案：升**。

#### Npgsql 5.0.18 → 10.0.3

10 个可构建版本（5.0.18 / 6.0.4 / 7.0.2 / 7.0.6 / 8.0.0 / 8.0.5 / 9.0.1 / 9.0.4 / 10.0.0 / 10.0.3）
全部编译通过，**40 项 PG 功能冒烟在每个版本上全绿**：连接、参数化 `GetDataTable`、流式 `GetDataReader`、
`GetDataSetAll`、事务、`DbMaintenance` 五件套、谓词层 + 分页、字典 CRUD、`Storageable`、`DbFirst`。

**包体代价是零**：升到 10.0.3，输出目录仍是 **86 个文件、36 个顶层 DLL，一个新文件都没多**，
总体积 58.63 MB → 59.14 MB（+0.51 MB），唯一变化是 `Npgsql.dll` 自己变大。
原因是 .NET 11 SDK 把 `Microsoft.Extensions.Logging.Abstractions` 列进了 `packagesToPrune`，由共享框架提供。
（**注意**：若哪天插件回落到 net9.0/net10.0，MELA 会重新变成一个要随包分发的文件。）

**但"什么都没变"的真相不是兼容，而是 SqlSugar 把新版锁回了旧模式**：
`new SqlSugarClient(PostgreSQL)` 的构造里就打开了两个 **进程级 `AppContext` 开关**：

```
Npgsql.EnableLegacyTimestampBehavior = true
Npgsql.DisableDateTimeInfinityConversions = true
```

这有三个后果，每一个都要写进纪律：

1. **它是进程级、不是 ALC 级，而且穿透插件隔离。** 实测：Runner（默认 ALC，不引用 SqlSugar/Npgsql）
   在跑之前读到"未设置"，插件 ALC 里的 SqlSugar 跑完之后**宿主默认 ALC 读到的就是 `True`**，
   而且**插件 ALC `Unload()` 之后依然留着**。→ §四 要记一条"插件可以污染宿主 AppContext"的纪律。
2. **开关只在 Npgsql 静态初始化那一刻被读一次。** 于是出现顺序竞争：
   只要同 ALC 里有人比 SqlSugar **先用一次 Npgsql**（哪怕只是"先 new 个 `NpgsqlConnection` 试试连通性"
   这种看着无害的写法），SqlSugar 的开关就迟到了，Npgsql 6+ 的新语义生效，
   **SqlSugar 的日期写入路径当场炸 4 项**——包括**字典 CRUD 写回，正是 §7.5 结果网格改一格要走的那条路**：
   ```
   ArgumentException: Cannot write DateTime with Kind=Unspecified to PostgreSQL type
                      'timestamp with time zone', only UTC is supported.
   ```
   → **硬纪律：插件在创建第一个 PostgreSQL 的 `SqlSugarClient` 之前，绝不允许碰 Npgsql。**
   这条要写成一个装载回归测试（§十）。
3. **`DisableDateTimeInfinityConversions=true` 对管理工具是实伤**：表里只要有一个
   `infinity`/`-infinity` 时间戳，那一格就直接抛异常读不出来，全版本一致。
   同批还测出三个与版本无关的读取硬伤：`numeric` 超 `decimal` 范围（`OverflowException`）、
   `numeric NaN`、公元前时间戳（`ArgumentOutOfRangeException`）。
   → §7.8：**一格读失败不能让整页失败**，要退到 `col::text` 或显示 `<不可映射: 原因>`。

升版的**静默行为变化只有三处**，全在 8.0 与 10.0 两个边界：

| 变化 | 5.0.18–9.0.4 | 10.0.0+ |
|---|---|---|
| `date` / `time` 的 CLR 映射 | `DateTime` / `TimeSpan` | **`DateOnly` / `TimeOnly`** |
| `cidr` | `ValueTuple` → (8.0 起) `NpgsqlCidr` | **`IPNetwork`**（BCL 类型） |
| `interval` 含月 | 5.0.18 **静默有损折算**（`1 year 2 mons 3 days` 被按 30 天/月压成 423 天） | 6.0.4 起改成明确报错 |

第三条值得单独说：**升 Npgsql 反而修掉了一个说谎点**——旧版把 14 个月悄悄折成 423 天，
新版直接拒绝。这与本文"宁可显示不可用，也不显示错值"的纪律同向。

**只能升不能降**：显式引用低于 5.0.18 的 Npgsql 被 NuGet 拦在编译期（NU1605），
所以"万一新版有问题就回退到更老版本"这条路不存在。

**推荐 Npgsql 10.0.3**（保守可选 9.0.4，只吃 `cidr` 一处变化）。
另外：**Npgsql 5 的 `SslMode` 只有 `Disable/Prefer/Require`——PG 侧根本没有证书校验档位**，
所以 §5.1 连接表单里 PG 的 `verify-ca` 档与 `ProtocolCertificateTrustException`
**在捆绑依赖下不可达**，这是升版的又一个理由。

#### MySqlConnector 2.2.5 → 2.6.2

7 个版本全部编译通过、跑通插件真会用的每条路径，**探针输出逐行相同**（仅版本号横幅不同）。
`AssemblyVersion` 恒为 `2.0.0.0`，不需要任何 binding redirect。
**不带来任何新的传递依赖，包体只涨 216 KB。** 连接串键面只多一个 `SkipCertificateRevocationCheck`。

**升版的真实收益是错误消息**，不是功能：2.2.5 在 `SslCa` 指向不存在文件时抛一句毫无关系的胡话，2.6.2 说人话。

### 3.9 ALC 卸载：初版的表整表作废

初版的卸载矩阵有一个自认的窟窿——"后四行只造出 `DbConnection` 对象、未真正 `Open`，
带连接池的驱动 `Open` 之后可能更差不会更好，需在有服务器的环境复测"。**复测完毕，结论比原来更糟。**

| 场景 | 只造对象（初版口径） | **真 Open + 真查询 + Dispose** |
|---|---|---|
| 基线（只装载程序集，不碰驱动） | 第 2 轮 GC 回收 | — |
| 只把原生库 `LoadUnmanagedDll` 进来 | — | **第 2 轮回收**（原生库本身是干净的） |
| `Microsoft.Data.Sqlite` | 40 轮仍存活 | **40 轮仍存活** |
| `Npgsql 5.0.18`（升到 10.0.3 也一样） | 40 轮仍存活 | **40 轮仍存活** |
| **`Microsoft.Data.SqlClient`** | **第 3 轮回收** | **40 轮仍存活** ← 初版那个"通过"是假象，`Open()` 是分水岭 |
| **`MySqlConnector`** | **第 2 轮回收** | **40 轮仍存活** ← 同上 |

**"关池 + 卸载前 `ClearAllPools()`"这条最被寄予厚望的解药，完全无效**：
3 驱动 × 4 变体（默认 / 关池 / 清池 / 关池+清池）共 12 个场景，全部 40 轮仍存活，一个都没救回来。
MySQL 侧另测，`Pooling=false` 与 `ClearAllPoolsAsync` 同样无效。
→ **文档里要把这条明确写成"已实测排除"，免得动工时有人再花一天去试。**

**钉子是什么（取证到委托名）**：不是连接池里的连接，而是驱动在**类型初始化那一刻**
挂到 `AppDomain` 上的进程退出钩子，外加连接池的 prune 定时器——两者都挂在默认 ALC 的静态根上，跨 ALC 强引用：

```
Npgsql.PoolManager+<>c.<.cctor>b__10_0        → AppDomain.DomainUnload
Npgsql.PoolManager+<>c.<.cctor>b__10_1        → AppDomain.ProcessExit
Microsoft.Data.Sqlite.SqliteConnectionFactory.<.ctor>b__6_0 / b__6_1
  + PruneCallback 定时器 period=30000
Microsoft.Data.SqlClient.SqlConnectionFactory.PruneConnectionPoolGroups  period=30000
Microsoft.Data.ProviderBase.DbConnectionPool.CleanupCallback             period=190000
```

**因果验证成立**：反射摘掉这两类钩子之后，**PG 与 SQLite 在真 Open + 真查询之后第 3 轮 GC 就回收了**。
所以"插件自己有解药"成立——但**解药是反射黑魔法**，依赖 .NET 内部字段名
（本轮就踩到：.NET 11 里 `TimerQueueTimer` 的取消方法已从 `Close()` 改名成 `Dispose()`），
升一个 .NET 版本就可能失效。**可行，但不推荐作为主方案。**

**SQL Server 是最硬的一个**：`new SqlConnection` 不泄漏，**第一次 `Open()` 之后永久钉住**，
且能想到的解药一条都不灵——关池、`ClearAllPools`、`Enlist=false`、`MARS=false`、摘 AppDomain 事件
（本来就没有）、关掉两个池定时器、Dispose `SqlClientEventSource`、Dispose `SqlClientDiagnosticListener`、
甚至强制走托管网络栈完全不装原生 SNI——**全部仍是 40 轮存活**。第三个钉子本轮没定位到。

**真正干净的解药：把驱动程序集划给宿主默认 ALC**（跟宿主现在处理 `Avalonia` 前缀是同一个机制）。
实测把 `SharedPrefixes` 扩成 `["Avalonia","Npgsql","Microsoft.Data.","SQLitePCL","Microsoft.Identity","System.Configuration"]`
并给默认 ALC 加 `Resolving`/`ResolvingUnmanagedDll` 之后：

```
pg-a     驱动 @ ALC=Default  →  第 2 轮 GC 后 ALC 已回收
sqlite-a 驱动 @ ALC=Default  →  第 2 轮 GC 后 ALC 已回收
mssql-a  驱动 @ ALC=Default  →  第 2 轮 GC 后 ALC 已回收
mssql-maint（14 项 DbMaintenance 全通过）→ 第 3 轮 GC 后 ALC 已回收
```

**危害量化**（这是"必须开口子"的直接证据）：ALC 收不回来 = 插件目录里的 DLL 删不掉。
在 Probe 输出目录副本上，卸载 + 40 轮 GC 之后逐个 `File.Delete`：

| 场景 | 删成功 / 删失败 | 第一个失败的 |
|---|---|---|
| 基线 | 64 / **0** | — |
| 真连过 PG | 61 / **3** | `Npgsql.dll` — Access to the path is denied |
| 真连过 SQL Server | 57 / **7** | `Microsoft.Identity.Client.dll` |

**而且"驱动归宿主 ALC"只解决一半**：插件 ALC 回收了，但只要驱动文件仍**物理留在插件目录**，
那几个 DLL 照样删不掉（宿主 ALC 永不卸载）——所以口子零必须配套"驱动包打进宿主目录"。

### 3.10 取消：初版对了一半，护栏错了

初版只在 SQLite 上测过，结论是"令牌被忽略、裸 ADO 也没用、跑满 139.6 秒"，
然后**断言** PG/MySQL/SQL Server 都能带外取消。本轮把这两半都验了。

#### 断言的那一半：成立

| 数据库 | 机制 | 客户端返回 | 服务端真停 | 判定服务端真停的证据 |
|---|---|---|---|---|
| PostgreSQL 18.1 | `NpgsqlCommand.Cancel()` | 31 ms（sleep）/ 60 ms（CPU 密集） | 78 ms / 111 ms | `pg_stat_activity` 里 active 消失，抛 57014 |
| SQL Server 2025 | `SqlCommand.Cancel()` | 11~14 ms | 47~80 ms | **`cpu_time` 冻结在 907 ms，1.5 秒内不再增长** |
| MySQL 8.4.11 | `MySqlCommand.Cancel()`（内部另开连接发 `KILL QUERY`） | 28~37 ms | 79~111 ms | 独立 root 连接查 `information_schema.processlist` 交叉验证 |

CPU 密集查询与 `sleep` 结果一致，**不是 `sleep` 被特殊对待**。

**取消延迟分布（n=7）**：PG 客户端 min 95 / 中位 104 / max 195 ms，服务端 min 129 / 中位 159 / max 233 ms；
MSSQL 客户端 min 0 / 中位 1 / max 50 ms，服务端 min 32 / 中位 49 / max 225 ms。
**两边服务端最坏都在 233 ms 以内**——这给了可辩护的超时阈值（见下面的升级阶梯）。

**取消成功之后连接可以继续用**，不必丢弃：PG 16 ms、MSSQL 20 ms 就在同一根连接上跑完了下一条语句。
→ 标签页取消一条查询后不用重连，"取消 → 改改 → 再跑"的手感会明显好。

#### 护栏的那一半：必须推翻

> 初版：「`Cancel()` 之后仍不回来的，**直接 `Dispose` 连接**——宁可丢一条连接，
> 也不能让一个标签页永远转圈」

**这条行不通，而且是三重错。** 实测 Dispose 一条有在途命令的连接：

```
PG   （CPU 密集）: conn.Dispose() 自身 10000 ms 内没有返回（调用线程被挂死）
                   客户端 43871 ms 才回来 = 查询自然跑完
                   服务端 43939 ms 停 = 自然结束，不是被取消
MSSQL（CPU 密集）: 客户端 120000 ms 内没返回，放弃等待
                   cpu_time 31703 → 31703（服务端早就跑完了）
MySQL           : Dispose 只让客户端立刻返回，服务端那条查询继续跑到自然结束（29.0/52.5/53.1 s）
```

`dotnet-dump clrstack` 抓到的主线程栈（PG，卡了 9 分钟）：

```
Interop+Winsock.<recv>...
System.Net.Sockets.NetworkStream.Read(...)
Npgsql.NpgsqlReadBuffer+<<Ensure>g__EnsureLong|40_0>d.MoveNext()
Npgsql.NpgsqlDataReader.Consume(Boolean)
Npgsql.NpgsqlConnector.CloseOngoingOperations(Boolean)
Npgsql.NpgsqlConnection.Close()
Npgsql.NpgsqlConnection.Dispose(Boolean)
```

开不开连接池不改变结论；好消息是**池子本身没被毒化**，另取一根连接照常可用（PG 104 ms / MSSQL 62 ms）。

#### `Ado.CancellationToken`：不是"不生效"，是"只在 async 路径生效"

| 入口 | PG | MSSQL |
|---|---|---|
| `GetDataTableAsync` + 预置令牌 | **29250 ms 未取消** | **29402 ms 未取消** |
| `GetScalarAsync(sql, null, token)` | 202 ms 取消 | 78 ms 取消 |
| `ExecuteCommandAsync(sql, null)` + 预置令牌 | 145 ms 取消 | 4 ms 取消 |
| `GetDataReaderAsync` + 预置令牌 | 52 ms 取消 | 7 ms 取消 |

根因在源码里（`ilspycmd` 反编译 `SqlSugar.AdoProvider`）：

```csharp
public virtual Task<DataSet> GetDataSetAllAsync(string sql, params SugarParameter[] parameters) {
    Async();
    if (!CancellationToken.HasValue) { return Task.Run(() => GetDataSetAll(sql, parameters)); }
    return Task.Run(() => GetDataSetAll(sql, parameters), CancellationToken.Value);   // ← 令牌只影响调度
}
public virtual async Task<DataTable> GetDataTableAsync(...) { DataSet ds = await GetDataSetAllAsync(...); ... }
```

`Task.Run(..., token)` 的令牌只影响**调度**，任务一旦开跑就再也管不了；
而 `ExecuteNonQueryAsync` / `ExecuteReaderAsync` / `ExecuteScalarAsync` 三条是真把令牌交给驱动的。

**还有一个 SqlSugar 自赋值 bug**（5.1.4.217）：

```csharp
public Task<int> ExecuteCommandAsync(string sql, object parameters, CancellationToken cancellationToken) {
    CancellationToken = CancellationToken;   // ← 自赋值；兄弟方法都是 = cancellationToken
    return ExecuteCommandAsync(sql, parameters);
}
```

真机验证：这个重载取消无效，跑满 29.0 s。**禁止使用带 `CancellationToken` 形参的 `ExecuteCommandAsync` 重载**，
一律用「预置 `db.Ado.CancellationToken` + 无令牌重载」。值得给 SqlSugar 提 issue。

**令牌是粘的**：设过一次就一直挂在 `IAdo` 上，取消之后紧接着的下一条查询会 0 ms 直接失败。
→ 每次执行前 `db.Ado.CancellationToken = 新 CTS.Token`，执行完 `RemoveCancellationToken()`；
否则用户取消一次之后这个标签页就废了。

#### SQLite：初版那句括号里的话要删

> 初版：「SQLite 走 `sqlite3_interrupt`」

**ADO.NET 门面上根本没有这条路**：`Microsoft.Data.Sqlite.SqliteCommand.Cancel()` 是**空方法体**，
`ExecuteReaderAsync` 是同步套壳且令牌只在开跑前检查一次。实测 `Cancel()` 客户端跑满 144845 ms、无异常。

**但取消是能做的，只是要绕过门面**：

```
raw.sqlite3_interrupt(((SqliteConnection)conn).Handle)
  → 20 ms 打断了那条跑满 150 秒的递归 CTE
  → SqliteException 9 'interrupted'
  → 打断后同一根连接立刻可复用（select 42 = 42，7 ms）
```

`SQLitePCLRaw` 已经在 `SqlSugarCore` 的依赖里，不用额外加包。

#### MySQL 特有：取消要另开一条连接，开不出来就静默失败

MySQL 没有协议级取消帧，`MySqlCommand.Cancel()` 是**另开一条连接发 `KILL QUERY <id>`**。后果：

- **连接开不出来时，`Cancel()` 27~31 ms 静默返回、不抛任何异常、令牌式 API 也不抛
  `OperationCanceledException`，查询照跑到底并返回完整结果。**
  用户看到的就是"点了取消，转圈，然后结果出来了"。
  因果被控制实验钉死：同一账号同一条 SQL，只把 `MAX_USER_CONNECTIONS` 从 1 改成 2，取消立刻恢复正常。
  → **插件必须在连接时探测"能否再开一根控制连接"**，探不到就把取消按钮降级并说明，而不是转圈骗人。
  （经 SSH 隧道时这意味着需要第二条转发通道——**本轮未实测隧道场景**，是从实测事实推的。）
- 担心的"低权限业务账号杀不掉自己的查询"**不成立**：`KILL QUERY` 自己的查询不需要 `PROCESS`
  也不需要 `CONNECTION_ADMIN`。但没有 `PROCESS` 的账号在 `processlist` 里只看得见自己那一行。
- **取消在 MySQL 客户端侧有五种不同表现**，异常翻译（§5.3）必须五种都认成"用户取消"，
  尤其那种**什么都不抛、返回正常结果**的。
- 连接被 Dispose 之后那条 `SLEEP` **在服务端还在**——MySQL 要等语句跑完准备回写结果时才发现对端没了。

#### 旁路取消：唯一能打断"已经交给同步 API 的查询"的手段

另一根连接发 `pg_cancel_backend(pid)` / `KILL spid`，客户端 PG 22 ms、MSSQL 7 ms 就回来了。
→ **方言包要新增两条资产**：取消语句、会话 id 语句（`pg_backend_pid()` / `@@SPID` / `CONNECTION_ID()`）。
注意 `KILL` 是杀会话不是取消语句，客户端会看到 kill state 错误，异常翻译要认这一条。

#### 落地：取消的升级阶梯（替换初版那三条）

1. 结果网格与执行入口一律用 **`GetDataReaderAsync` / `GetScalarAsync` / `ExecuteCommandAsync(sql, parameters)`**，
   预置 `db.Ado.CancellationToken`；**永不使用 `GetDataTableAsync`**。
2. 用户按下取消 → 先 `DbCommand.Cancel()`（SQLite 走 `raw.sqlite3_interrupt`）。界面立刻变"正在取消…"。
3. **1.5 秒**（≈最坏观测 233 ms 的 6 倍，留足网络 RTT）没回来 → **升级到旁路取消**，
   用状态探针那根连接发 `pg_cancel_backend` / `KILL`。
4. 再等 **2 秒**仍不回来 → **放弃这根连接：不再引用它，但绝不调 `Dispose`**（会挂死线程），
   让它随 GC 走，从池里另取一根继续。界面文案是「**已放弃该连接**」，不是初版的「已断开该连接」。

---
## 四、宿主要开的口子

初版这一节的标题下写着"零改动即可落地"。**真机之后这句话不成立了**：口子一从"值得开"变成"必开"，
并且新增了一个更根治的口子零。

### 4.1 零改动即可落地的部分（复核后不变）

| 需求 | 现有机制 | 结论 |
|---|---|---|
| 连接类型进配置页 | `contributes.workspaces`（数组，一个插件可贡献多个） | 一个插件贡献 MySQL / PostgreSQL / SQL Server / Oracle / SQLite **五个页签** |
| 连接表单 | `ProtocolSettingField` + `VisibleWhen` + `IsAdvanced` | 方言差异用条件可见性分叉 |
| 凭据 | 宿主 AES-256-GCM 加密落盘 + 登录弹窗 | 插件不碰 |
| 跳板机 | `WorkspaceFeatures.SshTunnel` + `ProtocolSettingKind.SshSession` | **插件一行 SSH 代码不写**——线上库几乎从不裸露公网，这条是刚需 |
| TLS 自签 | `WorkspaceFeatures.CertificateTrust` + `TrustedThumbprintSettingKey` | 与 FTPS/S3/Redis 共用同一个信任对话框（**但 PG 侧在捆绑的 Npgsql 5 下不可达，见 §3.8**） |
| 惰性激活 | `onWorkspace:<id>` | 不点数据库页签，驱动一个字节都不装载 |
| 会话文档 | `IWorkspaceDocument.CreateView()` | 插件全权渲染 |
| 一个连接开多个查询窗口 | —— | **插件内部**多标签即可 |
| 零打字建连 | `IWorkspacesApi.ProposeConnectionAsync` | 见 §八 |

### 4.2 口子零：把数据库驱动前缀划给宿主（新增，最根治）

**问题**：§3.9 实测——真连一次数据库，插件 ALC 就永久钉死；关池/清池/摘钩子都不是可靠解药。

**方案**：扩展 `PluginAssemblyLoadContext.SharedPrefixes`（现在是 `["Avalonia"]`），
加入 `Npgsql` / `Microsoft.Data.` / `SQLitePCL` / `Microsoft.Identity` / `System.Configuration`，
并把这些驱动包**引进宿主、打进宿主目录**。实测效果：驱动在默认 ALC、SqlSugar 与插件代码仍在插件 ALC，
真 Open + 真查询（SQL Server 还跑了 14 项 `DbMaintenance`）之后，**插件 ALC 第 2/3 轮 GC 回收**。

**代价，必须摆到台面上**：驱动版本一旦归宿主，**插件就不能再自带新版 Npgsql 了**——
这与 §3.8 的结论（升到 Npgsql 10.0.3）直接打架。两条路二选一：

| | 驱动归宿主（口子零） | 驱动留插件（现状） |
|---|---|---|
| ALC 可回收 | 是（第 2/3 轮） | **否**（永久钉死） |
| 驱动版本谁说了算 | 宿主，全体插件共用 | 插件自己，可升到 10.0.3 |
| 插件目录能否删干净 | 要配合"驱动文件搬进宿主目录"才行 | 否（PG 3 个 / MSSQL 7 个 DLL 删不掉） |
| 未来第二个用 Npgsql 的插件 | 自动共版本，不会互相打架 | 各带各的，`AppContext` 开关还会互相穿透（§3.8） |

**倾向：口子零 + 宿主直接引 Npgsql 10.0.3**——把"升版"这件事从插件挪到宿主，两个收益一起拿。
但这是要用户拍板的事（§十三）。

### 4.3 口子一：卸载失败改为隔离区（必开，且方案要改写）

**现状**（复核到行号，文档原引的 372 是注释行不是代码）：
`src/VelaShell.Infrastructure/Plugins/PluginManager.cs:370-396` 的 `TryDeleteDirectory`，
`:374` 起循环重试，`:387` 判 `attempt >= 3` 放弃，`:389` 只留一条 `Trace`，`:392-393` 逼 GC。
用户看到的是"点了卸载，插件还在"。而且 `UninstallAsync` 返回 `bool`，管理页把返回值直接丢了、也没有任何 Notice。

**初版的方案是"pending-delete 清单"。真机证明不需要清单**：

| 操作 | ALC 被钉住时 |
|---|---|
| `Directory.Delete` / `File.Delete`（入口 dll） | **必然失败** |
| `File.Move`（被锁的 dll） / **`Directory.Move`（整个插件目录）** | **必然成功** |
| 现状那套 4 次重试 + 3 轮阻塞 GC | 跑完 **8 ms**，结果仍是"放弃，目录还在" |
| 改名进隔离区 | **1 ms**，插件根目录下立刻看不到它 |

**所以正解是：卸载时把目录整体改名搬进隔离区，下次启动清扫。目录本身就是那条记录，不必再维护一份 JSON 清单。**
这个改动不但更可靠，还更快。

落点全部用现成常量，不发明：

- 隔离区 `<UserPluginRoot>/.trash/<guid>/`，即 `%LocalAppData%/VelaShell/plugins/.trash/`
  （路径来源 `PluginServiceCollectionExtensions.cs:35-41`）。与插件目录同卷，`Directory.Move` 是纯元数据操作；
  而且 `Discover` 只下钻一层、且要求该层目录里有 `plugin.json`，所以 `.trash` 这一层**天然被跳过**。
- 清扫点在 `StartAsync` 的 `Discover()` 之前（`PluginManager.cs:433`）。
  更值得记的是：**"启动时清扫已卸载插件的残留"这套机制在同一个文件里已经存在了一半**——
  插件的**数据目录**已经有启动期清扫，缺的只有插件**安装目录**这一半。
- **宿主里已有完整先例**：`UpdateApplier` 的类注释写着同一条 Windows 事实和同一种收尾策略，
  而且 `Program.cs` 的启动路径上已经挂了它的启动期收尾钩子。**照抄即可，不用发明。**
- 多实例并发不必上锁：单实例互斥体用的是 Local 命名空间，且守卫自身异常时兜底放行；
  GUID 命名的隔离目录 + 清扫时逐项 try/catch 忽略已经够了，额外加锁文件是过度设计。
- **改名也失败时的第三档兜底**：`plugin.json` 实测是可删可改名的（没被 ALC 锁），
  改成 `plugin.json.uninstalled` 就能让 `Discover` 永久跳过。这一档的"清扫先于发现"顺序要求是硬的。

**测试影响面极小**：整个插件测试套里只有 4 条断言碰目录存在性，改名方案下 4 条全部继续通过。

顺手把用户可见的静默补上：卸载后给一句"已卸载，残留将在下次启动时清理"。

### 4.4 口子二：结果网格用什么（仍需拍板，但估算要改）

仓库**全库没有任何 DataGrid**——这句话核实通过（代码零命中）。两条路：

| 方案 | 代价 | 真机核实 |
|---|---|---|
| **A. 宿主引入 `Avalonia.Controls.DataGrid`** | 宿主多一个包 | **版本对得上**：DataGrid 已发布 12.1.1 / 12.1.2，与宿主钉的 Avalonia 12.1.1 严丝合缝，不存在"官方 DataGrid 没跟上 12.x"的风险。**插件自己带不了**：程序集名以 `Avalonia` 开头，命中 `SharedPrefixes` 一律回落默认 ALC——宿主没有它就是 `FileNotFoundException`（AI 插件的 `AvaloniaEdit` 就是这条路，宿主为它在 `App.axaml` 加了一行 `StyleInclude`；DataGrid 只是把这行再抄一遍） |
| **B. 插件自研虚拟化网格** | 初版估 2–3k 行，**这个估算偏乐观** | `VelaShell.Controls` 全项目只有 **1435 行 / 4 个控件 / 3 份主题令牌，没有任何虚拟化基建，且没有任何插件引用它**；而 **Avalonia 12.1.1 已经删掉了 `ItemsRepeater`**，自研只能从 `VirtualizingPanel`（public abstract）起步。另有隐性代价：宿主为 Fluent 滚动条写了 363 行 `ScrollBarThemes.axaml`，自研要贴合 DESIGN 的 26px 列头 / 28px 行 |

**仍然倾向 B**（结果网格是这个插件的门面，商业工具的差距就体现在这块），但要把估算从 2–3k 上调。
**A 路线有一条没验证到的关键项**：`Avalonia.Controls.DataGrid` **是否真做列（横向）虚拟化**——
§7.3 的"100 列 × 100 万行"直接取决于它，而本轮取包体失败（nuget.org 被本机代理 302 到镜像，
取版本索引正常、取包体连续 90 s 超时）。**动工前必须联网 restore 一个最小 A 方案原型实测这一条。**

### 4.5 口子三：状态栏挂载点（可选，最便宜）

"选中 3 行 · 平均 12.4 · 求和 37.2"这类信息，商业工具都放状态栏。宿主的状态栏挂载点还没开。
**有现成的贡献点模式可以整体照抄**：`CommandRegistry` + `PluginCommandsApi` + `PluginContext.Dispose`，
蓝图 08 已定形态。新增能力应放 `IPluginContext` 新属性而非 `IUiApi` 方法
（`IUiApi` 有 5 处实现，`IPluginContext` 只有 3 处），遵守 SDK 零重依赖纪律。

工作量锚点：上一次开一个完整能力域（workspaces，commit `94b39a5`）是 41 个文件 / +3154 −89。
状态栏这个口子远小于此。

v1 先放在插件文档自己的底栏，等宿主开了再迁。

### 4.6 口子四：工作台连接类型应当允许"无端点"形态（M0 动工时撞上的）

`WorkspaceDescriptor.DefaultPort` 是**必填且必须落在 1–65535**。而 SQLite 是文件型的——
它没有主机、没有端口、也没有凭据。M0 目前的兜法是：给一个占位端口 `1`，
再用 `HostLabel` / `HostPlaceholder` 把"主机"一栏改标成"数据库文件"，
并声明 `WorkspaceFeatures.AnonymousAccess` 免掉那个没意义的登录框。

**能用，但表单上仍然摆着一个用户填不填都无所谓的"端口"输入框。** 建议宿主给
`WorkspaceDescriptor` 加一个"无端点"形态位（或让 `DefaultPort` 可空），
让文件型连接类型把端口一栏整个隐掉。这条不阻塞 v1，但**每多一个文件型数据库
（DuckDB、Access、以及将来的嵌入式库）就多踩一次**。

### 4.7 一条新纪律：插件可以污染宿主进程的 `AppContext`

§3.8 实测：插件 ALC 里的 SqlSugar 一改 `AppContext`，**宿主默认 ALC 读到的就是改后的值，
而且插件 ALC `Unload()` 之后依然留着**。这是现有插件隔离机制管不到的面。

至少要在 `docs/plugins/dev-guide.md` 记一笔"插件不得依赖 `AppContext` 开关做行为切换"；
数据库插件自己躲不掉（是 SqlSugar 干的），但要在插件文档里写明
"**本插件会把 Npgsql 的 legacy 时间模式打成进程全局**"，免得将来第二个用 Npgsql 的插件被坑。

---

## 五、连接与执行模型

### 5.1 连接表单（按方言修订）

初版给的通用字段：`database` / `charset` / `ssl` / `environment` / `readonly` / `jumpSession` /
`connectTimeout` / `commandTimeout` / `trustedThumbprint`。**真机把 MySQL 那一份验了个透，三处是硬伤**：

| 初版字段 | 真机结论 |
|---|---|
| `charset` | **彻头彻尾的摆设**。`CharSet` 传 `utf8mb4` / `utf8` / `gbk` / `latin1` / 乱填的 `nosuchcharset` / 干脆不传，**六种情况全部连上，服务端会话字符集恒为 `utf8mb4`**，中文往返码点完全一致。MySqlConnector 的 `CharacterSet` 属性被归到 `Category="Obsolete"`。→ **删掉这个下拉**，或改成只读展示服务端协商结果 |
| `commandTimeout` | **放在连接串里对 SqlSugar 完全无效**：SqlSugar 的 `Ado.CommandTimeOut` 默认 **300 秒**并盖到每一条 `DbCommand` 上，连接串的 `DefaultCommandTimeout` 被覆盖。→ 插件必须显式设 `Ado.CommandTimeOut` |
| `connectTimeout` | **这个键名驱动不认**。`Connect Timeout` / `Connection Timeout` / `ConnectionTimeout` 三种写法有效且计时精确；驼峰无空格的 `connectTimeout` 3 ms 就被拒 |
| `ssl` 四档 | MySQL 8.4 容器**默认就开着 TLS**（自签证书自动生成）。真实翻译：`disabled`→`None`、`preferred`→`Preferred`（默认值，已经在跑 TLS 1.3）、`required`→`Required`（**能连上，不是像预想那样连不上**）、`verify-ca`→`VerifyCA`（自签 CA 不在信任库则失败，喂 `SslCa=ca.pem` 后转绿）。`VerifyFull` 即使给对 CA 仍失败，因为证书 CN 是自动生成的机器名。**枚举值大小写敏感、无别名容错**：`Require`（少个 d）与 MySQL CLI 写法 `VERIFY_CA` 都直接抛 `Requested value 'X' was not found` |

**至少缺 6 个必须有的字段**（前三个不设就是明确的功能缺陷）：

| 建议字段 | 默认值 | 不设的后果（实测） |
|---|---|---|
| `TreatTinyAsBoolean` | **必须 `false`** | 驱动默认 `true`：任何 `TINYINT(1)` 列被当 bool 读出，**值 42 显示成 `True`**。对管理工具这是数据失真 |
| `AllowUserVariables` | **建议 `true`** | 默认 `false`：用户手敲的 `SET @x := 1; SELECT @x`、`SELECT @rn := @rn+1` 这类极常见 SQL 直接报错，而且报的是"参数未定义"这种把人往参数化上引的误导消息 |
| `ConvertZeroDateTime` / `AllowZeroDateTime` | 需要一个显式选项 | 老库最常见的地雷：默认配置下**只要表里有一个 `0000-00-00`，整张 `GetDataTable` 直接抛 `InvalidCastException`**——不是那一格出错，是整个结果集拿不到。`ConvertZeroDateTime=true` 能救但会把它变成 `0001-01-01`（界面会说谎）；`AllowZeroDateTime=true` 单独用更糟 |
| `AllowLoadLocalInfile` | 与导入功能绑定 | **只开这个开关不够**：MySqlConnector 额外要求 `SslMode >= VerifyCA`，或改用 `MySqlBulkLoader.SourceStream`（不要求 TLS）。三条路实测：只开开关→拒；开关+VerifyCA+SslCa→成功；`SourceStream`→成功 |
| `Pooling` / `MaximumPoolSize` | 见 §5.2 | 连接池账：同一连接串的 3 个 `SqlSugarClient` 在服务端只占 **1 根**物理连接（`IsAutoCloseConnection=true` 每次查完归还）；全部 Dispose 后 `Pooling=true` 仍留 1 根，`Pooling=false` 留 0 根 |
| `UseCompression` / `Keepalive` | 可选 | 经 SSH 隧道的长连接场景 |

**PG 侧要补两个连接串项**：

- **`Include Error Detail=true`**：默认被 Npgsql 抹掉，加上之后约束冲突才能拿到
  `Key (id)=(1) already exists.` ——这一行字是冲突提示里最有用的部分（§7.8）；
- **`Search Path=<schema>`**：这是 `DbMaintenance` 能看到自定义 schema 的**唯一**开关（§3.5）。

**`environment` 保留**（决定护栏强度，§7.6）——理由与 Redis 插件同：
替用户猜"这是生产"会让护栏在错误的地方紧或松。

### 5.2 一条会话几根连接（重写）

`SqlSugarClient` **不是线程安全的**，而数据库管理工具天然并发。真机之后连接模型要加一根：

| 连接 | 用途 | 生命期 | 真机加的约束 |
|---|---|---|---|
| **元数据连接** | 对象树、补全数据源、表结构 | 会话级，串行化 | **PG 上按 schema 分**（或干脆让方言包直查 `pg_catalog`）；元数据一律 `isCache:false` |
| **查询连接**（每个 SQL 标签一根） | 用户查询与 DML/DDL | 标签级 | 必须**显式持有**且 `IsAutoCloseConnection=false`；SQLite 要能取到 `SqliteConnection.Handle` |
| **状态探针连接** | 状态圆点、延迟 | 会话级，低频 | **新增职责：旁路取消通道**（发 `pg_cancel_backend` / `KILL`）。MySQL 上还要用它验证"能否再开一根控制连接" |
| **（MySQL）驱动内部的取消连接** | `MySqlCommand.Cancel()` 自己开 | 瞬时 | 不由插件管，但**连接数上限要给它留位置**（§3.10） |

三条硬规则，全部来自真机：

1. **连接的建立永远由插件自己 `Open()`**（`IsAutoCloseConnection=false` + 显式 `((DbConnection)db.Ado.Connection).Open()`）。
   一举三得：① 异常翻译才有真错误码可依（§5.3）；② `DbCommand.Cancel()` 才有对象可调；
   ③ 顺带解决 **`IsAutoCloseConnection=true` 会让会话级 `SET` 静默丢失**这个坑——
   实测 `set statement_timeout='1s'` 之后 `show statement_timeout` 仍是 `0`，因为每条语句开一次连接。
   `statement_timeout`、`search_path`、时区这类设置**只能落在显式持有的长连接上**。
2. **`conn.State` 在两个驱动上都是过期信息**，不能拿来点状态圆点——必须真发一条探针语句。
   （PG 被 terminate 之后 `conn.State` 仍显示 `Open`，第一条语句抛 `SocketException 10054`，第二条又自己好了。）
3. **SQL Server 的驱动默认会静默重连**被掐断的空闲连接（`ConnectRetryCount` 默认 1），
   所以"连接断了"在 SQL Server 上多数时候用户根本看不见——想让插件自己掌控重连提示，
   得显式 `ConnectRetryCount=0`。

**取消的升级阶梯见 §3.10 末尾**，这里只重复最关键的一句：
**放弃一根连接 = 不再引用它，绝不调 `Dispose`。**

### 5.3 异常翻译（重写：判据表）

与 S3/Redis 同一条纪律：**SqlSugar 与各驱动的异常类型不得越过插件边界**，出口一律翻成 SDK 的四类。
但真机发现**这条纪律的落点要分两段写**：

> **执行期：SqlSugar 完全不包异常。** `Ado.GetDataTable` / `ExecuteCommand` / `GetDataSetAll`、
> 以及谓词层的 `Insertable(字典)` / `Updateable(字典)` / `SqlQueryable` / `Queryable`，
> 抛出来的都是**驱动原生异常，整条链只有一层**。→ 直接对
> `PostgresException` / `SqlException` / `MySqlException` / `SqliteException` 做类型匹配即可。
>
> **连接期：SqlSugar 把驱动异常整个吞掉。** 抛的是 `SqlSugarException`，
> **`InnerException` 为 `null`、`Data` 为空**，错误码只剩在中文 `Message` 文本里。
> PG 侧还能从 Message 里抠出 `28P01`，**MSSQL 侧连 18456 都没了**，只剩一句本地化的
> `Login failed for user 'x'.`（服务器语言变了这句也会变）。MySQL 侧同样丢掉 `Number=1045`。
> → **禁止靠解析 `SqlSugarException.Message` 做判据。**

**绕开办法已实测可行且零成本**：让 SqlSugar 造好连接对象，由插件自己
`((DbConnection)db.Ado.Connection).Open()` —— 抛的就是原始驱动异常，`28P01` / `18456` / `1045` 原样到手。
这正是 §5.2 第 1 条硬规则的来由。

另：**`IsValidConnection()` 把认证失败吞成 `false`**（不抛异常、不带任何错误信息），
用它做"测试连接"按钮就分不出"密码错"和"连不上"——正好踩中本节要避免的那个代价。

#### PostgreSQL 判据表（按顺序匹配）

| 判据 | 翻成 |
|---|---|
| `PostgresException.SqlState == "28P01"`（含角色不存在，`Severity=FATAL`、`Routine=auth_failed`） | **ProtocolAuthenticationException** |
| `SqlState` 以 `28` 开头 | ProtocolAuthenticationException |
| `SqlState == "3D000"`（库不存在）/ `42501`（权限不足） | **不是**认证失败 → 普通业务错误（`3D000` 应引导用户改"数据库"字段） |
| `NpgsqlException` 且 inner 是 `SocketException`（10061 拒绝 / 11001 主机不存在 / 10054 被重置）或 `TimeoutException` | **ProtocolConnectionException** |
| `SqlState == "57014"` | 用户取消 / `statement_timeout`（**不是错误**，见 §7.8） |
| `SqlState == "57P01"` | 被 `pg_terminate_backend` → 连接断 |
| `SqlState == "25P02"` | 事务已废（见下面的事务态机） |

#### SQL Server 判据表（**顺序不能换**）

| 判据 | 翻成 |
|---|---|
| `Errors` 里存在 `Number == 4060` | 库打不开 → 普通错误，引导改"数据库"字段。**必须排在 18456 前面**——实测 4060 的 `Errors` 集合里**同时含 18456**，先判 18456 会误报成密码错、白弹一次登录框 |
| `Number == 18456` | **ProtocolAuthenticationException** |
| `Class == 14` 且非 18456（229 / 262 等） | 权限不足 → 普通错误，提示所需权限 |
| `Class == 20`（258 / 233 / -1983577849 等，号随传输层变） | **ProtocolConnectionException** |
| `Number == -2`（Class 11，inner Win32 258） | **语句超时** —— 归执行结果，不要报成连接断 |
| `Number == 596`（Class 21） | 会话被 `KILL`（旁路取消的正常回执） |

注：`SqlException.SqlState` 属性**恒为 `null`**，不可用。

#### MySQL 判据表

| 判据 | 翻成 |
|---|---|
| `MySqlException.Number == 1045` | **ProtocolAuthenticationException**（可靠） |
| `Number == 1042` | **大杂烩**：端口不通 / DNS 失败 / 连接超时 / 证书不受信全共用它，**必须靠 `InnerException` 与 Message 二次分流** |
| `Query execution was interrupted` | 用户取消（**五种取消表现之一**，见 §3.10） |
| 什么都不抛、返回正常结果 | **也可能是取消**（`SELECT SLEEP(n)` 被 `KILL QUERY` 打断后服务端算"正常完成"） |

#### 装配期的两种异常（容易被漏掉）

- **provider 装不出来**：抛 `SqlSugar.SqlSugarException: Not Found ….dll`——
  **不是 `FileNotFoundException`**，SqlSugar 把真实的装载失败吞掉了，
  而且**那个 dll 名可能是别人的**（§3.3 的静态污染）。翻译成 `ProtocolUnsupportedException`，
  文案走插件自带的 `DbType → 包名` 映射表，**永不透传原文**。
- **`IsAnyProcedure` 抛的是 `NotImplementedException`**，不是 `NotSupportedException`——
  按后者 catch 会漏。

#### 两条不要按类型/文案匹配的警告

- **异常类型会随驱动大版本变**：同一个错误，Npgsql 6.x/7.x 抛 `InvalidCastException`，
  8.0 起改抛 `ArgumentException`；`OverflowException` 的文案也从 Npgsql 自己的话改成了 BCL 的话。
- **文案会随服务器语言变**。
- → 按"驱动 + 场景"分类，并**给每条翻译配快照测试**。

#### 事务态机：本轮唯一一条会造成数据事故的坑

| | PostgreSQL | SQL Server |
|---|---|---|
| 语句出错后 | 进入 **25P02 僵死态**，后续语句全报错 | 普通错误不废事务（`XACT_STATE=1`，可继续）；**但 `XACT_ABORT ON` 时服务端直接回滚，`@@TRANCOUNT` 归 0** |
| 客户端看得见吗 | **看不见**：`IsAnyTran()` 仍为 True、`conn.State` 仍 Open、`NpgsqlTransaction` 上没有任何公开状态属性 | **看不见**：`IsAnyTran()` 仍返回 True |
| 后果 | 对已废事务调 `CommitTran` **不抛异常**（PG 实际执行的是 ROLLBACK） | **之后的语句变成自动提交，`RollbackTran` 也收不回来**。实测：id=60 随服务端回滚没了，id=61 **被自动提交了** |
| 多语句批的失败后果 | 整批当隐式事务，**第 2 条失败会把第 1 条一起回滚** | **不会**，前面的语句已经提交了 |

→ **硬规则**：插件必须自己维护事务健康标志。PG 上捕到 `25P02` 就把事务标记为 aborted，
界面把"提交"换成"只能回滚"，**绝不能让用户点到那个会静默变成 ROLLBACK 的提交**；
SQL Server 上**每条语句执行后要查 `XACT_STATE()`/`@@TRANCOUNT`**，不能信 `IsAnyTran()`。
§7.4 的执行完成面板要明写"前 N 条已提交 / 已随批回滚"。

---
### 5.4 谓词层：用 SqlSugar 的条件模型屏蔽方言差异

> 本节初版（08-18）**全部是离线生成 SQL 字符串**的实测——`ToSqlString()` 看着对，
> 没有一条真发到服务器上执行过。08-19 在 PG 18.1 / SQL Server 2025 / MySQL 8.4.11 三台真机上重跑，
> 结论是：**骨架成立，主路径该用；但有五个洞只有真跑才看得见，其中两个会让界面给出错误答案、
> 一个能删掉整张表。**

#### 5.4.1 为什么 lambda 不行，而条件模型行

商业工具的筛选行长这样：用户在 `status` 列下拉选"等于"、填 `paid`。这些字段名**在编译期不存在**——
所以 `Where(it => it.Status == "paid")` 这条路从根上就走不通。

SqlSugar 有第二套谓词，**不走表达式树**：

```csharp
public class ConditionalModel : IConditionalModel {
    public string FieldName;                       // 字段名(有格式校验,见 5.4.4)
    public string FieldValue;                      // 值(字符串,按 CSharpTypeName 转型后参数化)
    public string CSharpTypeName;                  // "decimal" / "datetime" / "int" …
    public ConditionalType ConditionalType;        // 18 种
}
public class ConditionalCollections : IConditionalModel { … }   // 一层 AND/OR 组(只能装叶子)
public class ConditionalTree : IConditionalModel { … }          // 递归嵌套
```

配上**无实体查询入口** `ISugarQueryable<ExpandoObject> Queryable(string tableName, string shortName)`，
"运行期才知道表名与字段名"这件事 SqlSugar 是**有正面支持的**。

**但初版对嵌套能力的描述要降级**：

| 初版 | 真机 |
|---|---|
| `ConditionalTree` 递归嵌套 → 任意括号层级 | **两层可靠，更深层要按规避写法排布**。子树节点上的 `WhereType.Or` **被静默丢弃、当成 AND 渲染** —— 连接符取自"组的第一个子元素的 `WhereType`"，挂在组本身那个 `KeyValuePair` 上的 `WhereType` 完全被忽略。「A OR (B AND C)」这个形状**根本表达不出来，而且不报错**：期望 54 行，实际返回 5 行 |
| —— | 把 `ConditionalCollections` 塞进 `ConditionalTree` 直接抛 **`NullReferenceException`**（生成阶段就炸）。**嵌套只用 `ConditionalTree`，`ConditionalCollections` 只做最内层叶子组** |

可用的规避写法两条（真机验证正确，54 行）：① 把组放前面、标量放后面；
② 顶层 List 放两个 `ConditionalCollections`，第二组首元素给 `Or`。
→ **这条要写成插件谓词构造器的单测**：构造一组 `A OR (B AND C)` 断言行数。

**`CSharpTypeName` 在 PostgreSQL 上是必填项**（初版把它列为可选）：不填则任何非文本列的条件直接报
`42883: operator does not exist: integer = text`；SQL Server 靠隐式转换全部蒙混过关。
→ **筛选面板必须先拿到列类型元数据才能构造条件**，否则 PG 上一按筛选就报错。

**18 种 `ConditionalType` 的语义速查表（四处反直觉，离线看 SQL 看不出来）**：

| 取值 | 真实语义 | 注意 |
|---|---|---|
| `LikeLeft` | **前缀匹配**（参数 `值%`） | 与中文名直觉**相反**，不能翻译成"以…结尾" |
| `LikeRight` | **后缀匹配**（参数 `%值`） | 同上 |
| `NoEqual` | `col <> v`，**排除 NULL** | 200 行数据里 159 行 |
| `IsNot` | `( col <> v OR col is null )`，**包含 NULL** | 199 行 |
| `EqualNull` | 传 null 值时退化成 `IS NULL`；传值时就是普通 Equal | |
| `Range` / `RangeDate` | `FieldValue` 必须是 `"a,b"` 逗号格式，只给一个值抛异常 | **`RangeDate` 的右端是 `< 结束日+1天`**（传 2/1–3/1 会含整个 3 月 1 日） |
| `In` | 只有一个值时退化成 `= '值'` 而不是 `IN` | 另见 5.4.4 的严重缺陷 |
| `IsNullOrEmpty` | 展开成 `(col IS NULL) OR (col = '')` | 在 `datetime` 列上**直接抛异常**，在数值列上语义无意义 |

复核补一条：**`Range` 在 PG 的数值列上是坏的** —— `Range amount='10,20'` 生成的参数被当 `text` 发出去，
报 `42883: operator does not exist: numeric >= text`；同一条件在 MSSQL 上正常。
→ **PG 上的"区间"筛选必须自己拼两条 `GreaterThan`/`LessThan`。**

#### 5.4.2 方言化：文本统一了，语义没有

七种方言的标识符转义与分页形态（离线，仍然成立）：

| 方言 | 标识符转义 | 分页（第 2 页 ×10） |
|---|---|---|
| MySQL / SQLite | `` `orders` `` | `LIMIT 10,10` |
| PostgreSQL / 人大金仓 | `"orders"` / `"ORDERS"` | `LIMIT 10 offset 10` |
| SQL Server | `[orders]` | `ROW_NUMBER() OVER(…) … BETWEEN 11 AND 20` |
| Oracle / 达梦 | `"ORDERS"` | 同 SQL Server 形态（**离线推断，无真机**） |

> **初版：「`IsNullOrEmpty` 一条被展开成 `(memo IS NULL) OR (memo = '')`——七种方言一致，
> 这正是"空值到底算不算空"这种最容易写错的地方，它替你统一了。」**
>
> **这句话是过誉的。它统一了 SQL 文本，没有也不可能统一语义。**
> 同一张表、同样的数据、同一条生成的 SQL：
>
> | 列 | 结果 |
> |---|---|
> | PG 的 `text` 列 | **80 行**（只有 NULL 和真空串） |
> | SQL Server 的 `nvarchar` 列 | **120 行**（单空格行也算"空"——SQL Server 比较时忽略尾随空格） |
> | PG 的 `char(20)` 定长列 | 120 行（bpchar 同样忽略尾随空格） |
> | MySQL `utf8mb4_0900_ai_ci` | 因列校对的 PAD 属性不同，同一张表里 **80 vs 120** |
>
> 手写 SQL 与谓词层在每一格上都相等——差异纯粹来自方言/列类型语义。
> → 界面上"为空"这个筛选项要注明它在该方言下的确切含义，或干脆拆成「IS NULL」「= 空串」两个选项。

同类的还有一条：**`Like` 在 MySQL 默认 `ai_ci` 校对下同时是大小写不敏感和重音不敏感，而 PG 上两者都敏感**——
同一个筛选条件在两种一等公民方言上给出不同结果集。

#### 5.4.3 字典驱动的写回（结果网格要的那件事）

**主路径三台真机全通并回读校验**：`Insertable(dict).AS(表).ExecuteCommand()` 影响 1 行、回读正确；
`Insertable(List<dict>)` 批量 3 行；`Updateable(dict).WhereColumns("id")` 影响 1 行**且不误伤其他列**；
`Deleteable<object>().AS(表).Where(条件模型)`；`db.Fastest<DataTable>().AS(表).BulkCopy(dt)` PG/MSSQL 都通。
值里带单引号的三种注入 payload 全部安全回读，哨兵表存活。

**初版记的泛型推断坑复核仍在**：`db.Insertable<object>(dict)` 抛
`InvalidOperationException: Sequence contains no elements`，必须写 `db.Insertable(dict)` 让编译器推断。

**三条真机新增的护栏**：

1. **`Updateable(字典)` 忘了写 `WhereColumns` 不会报错，直接生成没有 WHERE 的全表 UPDATE**
   （而且主键也被写进 `SET`）：`UPDATE "orders" SET "amount"=5,"id"=1001`。
   → **没有主键 / 没写 `WhereColumns` 的表，网格一律只读。** 这条不可省。
2. **`Storageable` 不是 `ON CONFLICT`、也不是 `MERGE`**（初版说"不用自己拼 18 份"是对的，
   但它的实现方式必须写清楚）：它是三步——
   ① `SELECT` 探一遍（**100 行数据 = 100 个 `OR` 子句**）；② 批量 `INSERT`（**值内联，无参数**）；
   ③ `UPDATE … FROM (VALUES …)`。**不原子、有 TOCTOU 竞态、探测 SQL 随行数线性膨胀。**
   → CSV 导入（§6 能力组 6）要么限制批大小，要么改走 `Fastest`/`BulkCopy`。
   （一个小意外：MSSQL 的批量 `INSERT` **是带 `N''` 前缀的**，写入路径中文安全——和 `In` 那条路径不一致。）
3. **生成列是地雷**：字典 CRUD 一带上生成列，MySQL 就报
   `The value specified for generated column 'c_gen_v' is not allowed`。
   而 `DbMaintenance` 认不出生成列（§2.3）→ **可写列集合必须由方言包提供。**

#### 5.4.4 安全：字段名有校验，表名没有，`In` 不参数化

**好消息（真机确证）**：`FieldName` 的格式校验是真的——`id; drop table x--`、`id"; …`、`id]; …`、
`id) or 1=1--` 四种 payload 在 PG 与 MSSQL 上一律抛 `` `id; drop table x--` format error ``，哨兵表全程存活。
而且**中文列名与含空格列名是能正常用的**：`WHERE "名称" = @Condit名称0` / `WHERE [order date] = @Conditorder date0`，
真表上执行正确。（唯一破绽：参数名派生自字段名，`1=1 or id` 这种能过校验的怪名字会生成非法参数名，
报错难懂但不构成注入——§7.8 要把这类翻成"该列名暂不支持筛选"。）

**坏消息一：`AS(表名)` 这条路径没有任何格式校验、也不转义结束定界符。** SQL Server 上实测真炸：

```csharp
db.Deleteable<object>().AS("orders]; drop table victim2--").Where("1=2").ExecuteCommand()
// 生成: DELETE FROM [orders]; drop table victim2--] WHERE 1=2
// 结果: 影响 200 行，orders 200 → 0；victim2 表没了
```

PG 上同类 payload 生成的是同样破损的 SQL，但服务端以 `42601`/`42P01` 拒绝，哨兵表存活——
**是运气不是防护**（MySQL 上试的 7 种反引号载荷同样全部语法错误告终，未能构造出可用注入）。
→ **插件必须自己白名单校验表名**：只允许来自对象树的已知标识符，或按方言做定界符加倍转义。
这条要进 §7.6 的安全护栏。

**坏消息二：`In` / `NotIn` 不走参数化，且内联时不加 `N` 前缀。** 初版写的
"值全部走 `SugarParameter`"要改：

```
MSSQL(SQL_Latin1_General_CP1_CI_AS):
  生成 WHERE [region] IN ('华东','华南')  参数=(无)
  谓词层返回 0 行   手写 region in (N'华东',N'华南') = 100 行   内存模型 = 100 行
  NotIn 同一组 → 谓词层 200 行(全表)，正确答案 100 行
PG 上同一条 In：100/100/100 全对
```

**这是正确性缺陷不是注入缺陷**（单引号是做了转义的），但**性质更坏——它静默给错答案**。
DELETE 路径同样中招。
→ **硬规则：筛选行的 `In` 一律在谓词构造层展开成 `Equal` 的 `OR` 组**
（实测 `ConditionalCollections[(And,Equal 华东),(Or,Equal 华南)]` → 参数化、PG/MSSQL 都对）。

**坏消息三：`Like` 系不转义通配符。** 用户在筛选框里输入 `%` 或 `_` 会被当成通配符：
`Like` 值=`%` → 200 行（全表）；想搜字面量 `pa_d` → 命中 50 行（实际匹配的是 `paid`）。
→ 筛选行要么自己转义（加 `ESCAPE` 子句），要么把"包含"明确标成通配符搜索。

**关于 SQL 预览**：`ToSqlString()` 是给人看的内联版，`ToSql()` 才是参数化真相。
但真机发现**PG 上的预览带 `N''` 前缀**（`WHERE "UserName" = N'alice'`），
它能跑，但 `pg_typeof(N'abc')` 是 `character`——`N'abc ' = 'abc'` 为真而 `'abc ' = 'abc'` 为假。
→ §7.5 的 SQL 预览要么剥掉 `N` 前缀，要么标注"仅供阅读，勿直接执行"。

#### 5.4.5 标识符大小写（真机加强）

**默认配置下 SqlSugar 会改写你的表名与列名**，这是 ORM 的正确行为、管理工具的致命伤。真机原文：

```
lower=on（SqlSugar 默认）:
  Queryable("OrderDetail","o").ToSql()  →  SELECT * FROM "orderdetail" "o"
  真跑 → PostgresException: 42P01: relation "orderdetail" does not exist
lower=off（四开关）:
  →  SELECT * FROM "OrderDetail" "o"   真跑 → 1 行
```

修复仍是那四个开关（实测七方言全部回到原样输出）：

```csharp
MoreSettings = new ConnMoreSettings {
    PgSqlIsAutoToLower = false, PgSqlIsAutoToLowerCodeFirst = false,
    PgSqlIsAutoToLowerSchema = false, IsAutoToUpper = false
}
```

**真机比离线更险的三点**：

1. **元数据仍然读得到，只有数据读不到。** 同一份默认配置下
   `DbMaintenance.GetColumnInfosByTableName("OrderDetail")` **成功**返回三列，
   而 `ToDataTable`/`Count`/`ToPageList`/字典 UPDATE/字典 INSERT **五个入口全部 42P01**。
   → 用户看到的表现是「**对象树里有这张表和它的列，一点开就报表不存在**」。
   §7.2 与 §7.8 的文案要按这个表现写。
2. **关掉大小写规范化只解决了生成 SQL 那一半。** `DbMaintenance` 的匹配是**大小写不敏感**的
   （`upper()=upper()` / `Lower(tablename)=…`），所以 `IsAnyTable("orderdetail")` 也返回 True、
   `GetColumnInfosByTableName("orderdetail")` 会把 `"OrderDetail"` 的列拿回来——
   **而在 PG 上这两张表是可以并存的**。这一层只能由方言包用精确匹配兜。
3. **MySQL 这一层初版完全没提**：MySQL 的大小写敏感性**来自服务器**（`lower_case_table_names`，
   表名敏感、列名不敏感），与 SqlSugar 无关——装不装那四个开关，生成的 SQL 一模一样。
   但 `GetPrimaries`/`GetIsIdentities` 的大小写不敏感缓存会串表（§3.7）。

`ConnMoreSettings` 上另有几个对管理工具有用的：`EnableILike`、`DisableNvarchar`、
`IsWithNoLockQuery`、`DbMinDate`、`MaxParameterNameLength`。

#### 5.4.6 边界：谓词层管到哪儿为止

| 场景 | 走谓词层 | 走裸 ADO / 方言包 | 真机加的前提 |
|---|---|---|---|
| 点开一张表看数据、筛选、排序、翻页 | 是 | | **排序必须自动追加主键做 tie-breaker**（§7.3）；PG 上区间筛选自己拼；`In` 展开成 `OR` 组 |
| 网格里改一格 / 加一行 / 删一行 | 是（字典 CRUD） | | 必须有 `WhereColumns`；可写列要剔除生成列 |
| CSV 导入 | 是（`Storageable`/`BulkCopy`） | | `Storageable` 非原子，大批量走 `BulkCopy` |
| **用户在编辑器里手敲的 SQL** | | 裸 ADO | 要取消、要多结果集、要原样透传、**要能拼自己的分页** |
| DDL、执行计划、会话与锁、权限、外键 | | 方言包 | |
| **PG 上"浏览一张表"** | | **有例外** | 表里有 `infinity` 时间戳 / `numeric NaN` / 未知 OID 的枚举时，`select *` 整条炸（§3.8）——要按单元格容错 |

一句话：**谓词层负责"我们替用户写的 SQL"，裸 ADO 负责"用户自己写的 SQL"。**
这条线一划，"屏蔽方言差异"就落在了它真正成立的那一半上——
**而真机告诉我们，即使在那一半里，方言差异也只被屏蔽了语法，没有屏蔽语义。**

---
## 六、对标商业工具：能力清单与覆盖策略

### 6.1 十组能力，逐组点名谁提供

| # | 能力组 | 商业工具的水位 | 谁提供 | v1 |
|---|---|---|---|---|
| 1 | **连接管理** | 保存/分组/隧道/SSL/只读 | **宿主全包** | 是 |
| 2 | **对象浏览** | 库→schema→表/视图/函数/存储过程/触发器/序列 | SqlSugar 兜底 + 方言包补。**真机之后方言包要补的清单长了一大截**：外键、索引列与唯一性、视图/物化视图的列、自定义 schema、计算列、触发器（去内部触发器）、存储过程清单（PG）、可用类型表 | 是 |
| 3 | **SQL 编辑与执行** | 高亮、补全、多语句、执行选中、结果多标签 | 自研（AvaloniaEdit）+ **裸 ADO 执行与分页**（不是 SqlSugar 分页，见 §7.3） | 是 |
| 4 | **结果网格** | 百万行虚拟化、就地编辑、NULL/二进制、导出 | **自研（最大工程项）** | 是 |
| 5 | **表设计器** | 列/索引/外键/约束的可视化增删改 | 方言包生成 DDL，**先给预览再执行**。PG 的写侧可以吃 `DbMaintenance`（20/22 生效），**MySQL 的写侧不能**（`DropConstraint` 会删主键、`UpdateColumn` 抹注释，§3.7） | 是（列 + 索引；外键 M4） |
| 6 | **数据导入导出** | CSV/JSON/Excel/SQL dump | 自研；导入走 `BulkCopy`（不是 `Storageable`，§5.4.3） | 是（CSV/JSON；Excel 不做） |
| 7 | **执行计划** | 可视化计划树、代价、索引建议 | 方言包 | M4（先出原文） |
| 8 | **运维面** | 会话/锁/阻塞链/慢查询/表空间 | 方言包 | M4 |
| 9 | **结构与数据比较** | schema diff + 同步脚本 | 自研 | 不做（§十二） |
| 10 | **ER 图** | 自动布局的关系图 | 需外键元数据 + 布局引擎 | 不做 |
| ★ | **实体代码生成** | Navicat 没有，DataGrip 要插件 | **SqlSugar `DbFirst` 白得** | 是 |
| ★ | **与终端并排 / 从 SSH 零打字建连 / 操作可审计** | 三家都没有 | VelaShell 独有（§八） | 是 |

### 6.2 全方言覆盖路线

"支持 SqlSugar 的全部数据库"拆开只有三个问题：**驱动从哪来**、**方言包写几份**、**界面要不要变形**。

| 级 | 方言（`DbType`） | 驱动 | 方言包 | 界面 | 排期 |
|---|---|---|---|---|---|
| **T0** | `MySql`、`MySqlConnector` | 捆绑 | **MySQL 基准包**（新写） | 标准 | M1 |
| **T0** | `PostgreSQL` | 捆绑 | **PG 基准包**（新写） | 标准 | M1 |
| **T0** | `Sqlite` | 捆绑（原生 e_sqlite3） | **SQLite 基准包**（新写，**必须写满**——只有 12/23） | 标准（无"库"层） | M1 |
| **T0** | `SqlServer` | 捆绑（原生 SNI） | **T-SQL 基准包**（新写） | 标准（多 schema 一级） | M4 |
| **T0** | `Oracle` | 捆绑（5.13MB） | **Oracle 基准包**（新写） | 标准（"库"= schema/表空间） | M4 |
| **T1** | `Tidb`、`OceanBase`、`PolarDB`、`Doris`、`GoldenDB`、`TDSQL` | 捆绑 | 复用 MySQL 包 + 差异补丁 | 标准 | M4 |
| **T1** | `OpenGauss`、`Vastbase`、`GaussDB`、`Kdbndp`(人大金仓) | 捆绑 | 复用 PG 包 + 差异补丁 | 标准 | M4 |
| **T1** | `HG`(瀚高)、`QuestDB` | 需额外包 | 复用 PG 包 + 补丁（QuestDB 退化为只读 + 时序视角） | 标准 / 退化 | M5 |
| **T2** | `Dm`(达梦)、`Kdbndp`(人大金仓)、`Oscar`(神通) | 捆绑 | **各自独立的基准包**（SqlSugar 内部就是三个独立 provider，指望不上 Oracle/PG 复用）。**神通要额外下调预期**：它只覆写 7/81 个方法，索引/存储过程/函数/触发器全吃基类，且 `RenameTableSql` 里混了中文必然语法错 | 标准 | M5 |
| **T2** | `Xugu`、`GBase`、`HANA`、`DB2`、`GaussDBNative` | 需额外包（**四个包 nuget.org 查无此包，见下**） | 各自专有 | 标准 | M5，按需求排 |
| **T2** | `TDSQLForPGODBC`、`TDSQLForOracleODBC`、`Odbc` | 需 `System.Data.Odbc` | 走 ODBC 的 `GetSchema` | 标准但**能力大幅退化** | M5 |
| **T2** | `Access` | 需额外包（x86/x64 要与 Office 位数一致） | 专有 | 砍掉一半对象类别 | M5，低优先 |
| **T3** | `DuckDB` | 需额外包 | 专有 | 标准 | M5 |
| **T3** | `ClickHouse` | 需额外包 | 专有（引擎、分区、物化视图是一等概念；`UPDATE/DELETE` 是异步 mutation） | **对象树要加"引擎/分区"，数据编辑要禁用** | M6 |
| **T3** | `TDengine` | 需额外包 | 专有（超级表/子表/标签） | **对象树形态不同** | M6 |
| ✘ | `MongoDb` | 需 `SqlSugar.MongoDbCore` | —— | **文档库，不进本插件**（§十二） | 另开插件 |
| ✘ | `Custom` | 用户自带程序集 | —— | 不在支持承诺内 | 不做 |

#### 复用是真的，但机制和数字都要改（本轮反射验证）

初版写"真正互不相同的只有 **6 套**方言语法，其余是 MySQL/PG 的兼容实现"。
反射 `SqlSugar.dll` 之后，这句话**方向对、机制全错、数字偏低**：

**机制不是类继承，而是 `SqlSugarClient` 构造时就地改写 `config.DbType`。**
`SqlSugar` 里**一条跨方言继承都没有**——8 个 `DbMaintenance` 全是
`Xxx → DbMaintenanceProvider → object` 两层，程序集里根本没有 `TidbDbMaintenance` 这种类型。

| 传进去的 `DbType` | `new SqlSugarClient(cfg)` 之后 `cfg.DbType` 变成 |
|---|---|
| `MySqlConnector` / `Tidb` / `OceanBase` / `PolarDB` / `Doris` / `GoldenDB` / `TDSQL` | **`MySql`** |
| `OpenGauss` / `GaussDB` / `Vastbase` | **`PostgreSQL`** |

**两个后果插件必须知道**：

1. **不能靠 `config.DbType` 记住"用户选了 TiDB"**——建完 client 它就变成 `MySql` 了
   （而且是对传入对象本身的写入，不只是 `db.CurrentConnectionConfig`）。
   连接档案里要**另存一个"用户可见方言"字段**，用来选方言包和差异补丁。
2. **绕过 `SqlSugarClient` 直接调 `InstanceFactory.GetDbMaintenance(cfg)` 是不通的**：
   没有改写，`Tidb` 会当场抛。想自己接管方言装配的话，这条路封死。

**数字应该是 8 套，不是 6 套**：达梦、人大金仓、神通**各自是独立实现，不继承也不复用 Oracle/PG**：

```
DbMaintenanceProvider（抽象基类，33 条模板）
├── MySqlDbMaintenance ....... MySql, MySqlConnector, Tidb, OceanBase, PolarDB, Doris, GoldenDB, TDSQL  (8)
├── PostgreSQLDbMaintenance .. PostgreSQL, OpenGauss, GaussDB, Vastbase                                  (4)
├── SqlServerDbMaintenance ... SqlServer          ├── OracleDbMaintenance ... Oracle
├── SqliteDbMaintenance ...... Sqlite             ├── DmDbMaintenance ....... 达梦（独立）
├── KdbndpDbMaintenance ...... 人大金仓（独立）    └── OscarDbMaintenance .... 神通（独立）
无归属（取 DbMaintenance 当场抛）：Access, QuestDB, HG, ClickHouse, GBase, Odbc,
    OceanBaseForOracle, TDengine, Xugu, TDSQLForPG/OracleODBC, HANA, DB2, GaussDBNative, DuckDB, MongoDb (16)
```

- **人大金仓**不是"复用 PG 包"：它把 `pg_class`/`pg_tables`/`pg_attribute` 系统性换成 `sys_*`，是**真改写**；
- **神通**用 `sys_class + sys_description` 与 `INFO_SCHEM.ALL_TAB_COLUMNS`，与 PG 不同，
  而且**只覆写 7/81 个方法**（MySQL 覆写 24、SqlServer 25），索引/存储过程/函数/触发器全吃基类——
  **§6.2 给它的定位要下调**；
- **达梦**的模板层确实与 Oracle 大量逐字节相同（6 条），但元数据查询改用达梦专有的
  `SF_GET_SCHEMA_NAME_BY_ID(CURRENT_SCHID)`。

→ **"6 份方言包"应改成"8 份基准包 + 一批差异补丁"**；或者明确写成
"我们选择把达梦/人大金仓/神通按 Oracle/PG 包 + 补丁来做"——但不能写成"SqlSugar 复用了"，因为它没有。

三条结论：

1. **35 种听起来吓人，实际是 8 份方言包 + 一批补丁。** T1 那十种确实白得（同一个类），
   这正是选 SqlSugar 的复利所在。
2. **未捆绑驱动的那些方言，包体不能白背**，走 §九 的"可选驱动包"路线。
   **但排期前必须先确认包拿得到**：`SqlSugar.Db2Core` / `SqlSugar.GaussDBCore` /
   `SqlSugar.HANAConnector` / `SqlSugar.TDSQLForOracleODBC` 四个包在 nuget.org
   `--exact-match` **搜不到**；虚谷对口 `XuguCore` / `XuguCoreNew` / `XuguClient` 哪个也待确认；
   QuestDB 的 provider 类全在 `SqlSugar.dll` 里却被依赖检查拦下，真正要装的包名 IL 里查不到。
3. **有三种必须改界面才算真支持**：ClickHouse、TDengine、ODBC。
   **在它们身上套标准界面就是"能看不能用的假客户端"**，所以排在最后且要单独评估界面形态。

**"完整"在数据库上的正确定义**：和 Redis 那篇一样——**不是把功能清单画满**。
界面的职责是让**最高频的那 20 个动作零打字**，让**危险的那 5 个不打偏**。

---

## 七、界面设计（重点）

### 7.1 信息架构：一个停靠文档，内含三区

```
┌ 数据库文档（宿主标签页：● prod-mysql · 10.0.3.7:3306 ↝ bastion-01）────────┐
│ ┌ 对象树 220px ┐ ┌ 工作区（插件内部多标签）──────────────────────────────┐ │
│ │ ▾ 📁 shop    │ │ [查询 1] [orders 数据] [orders 结构] [+]              │ │
│ │   ▾ 表 (37)  │ │ ┌ SQL 编辑器（AvaloniaEdit，可折叠）─────────────────┐ │ │
│ │     orders   │ │ │ select * from orders where created_at > @d        │ │ │
│ │     users    │ │ └──────────────────────────────────────────────────┘ │ │
│ │   ▸ 视图 (4) │ │ ┌ 结果网格 ────────────────────────────────────────┐ │ │
│ │   ▸ 函数 (2) │ │ │ id │ user_id │ amount │ status  │ created_at     │ │ │
│ │ ▸ 📁 shop_bak│ │ │ 1  │ 1024    │ 39.90  │ paid    │ 2026-08-17 …   │ │ │
│ │              │ │ └──────────────────────────────────────────────────┘ │ │
│ │              │ │ 底栏: 1–200 / 约 12,847 行 · 18 ms · 只读列 3        │ │
│ └──────────────┘ └────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────────────────┘
```

沿用 `DESIGN.md`：列头 26px / `VelaBgSurface`，数据行 28px + 1px `VelaBorderPrimary`，
对象树行 28px，一律 `{DynamicResource Vela*}`，不写十六进制。

**为什么编辑器与网格是上下而不是左右**：SQL 是宽的，结果也是宽的。左右分栏会让两边都被压扁。

（底栏那个"约 12,847 行"的"约"字是真机逼出来的——见 §7.3 的总数策略。）

### 7.2 对象树：懒加载 + 计数 + 不撒谎

- **三层懒加载**：库 → 对象类别 → 对象。展开才查。
- **类别行带计数**（`表 (37)`）：这个数**只在展开过之后才显示**。展开前显示估算值是撒谎。
- **元数据缓存 + 显式刷新**：缓存进内存 + 插件私有时序库，F5 刷新当前节点。**永不自动轮询**。
- **搜索定位**：`Ctrl+P` 在缓存里模糊搜对象名，回车直接打开数据标签。

**真机加的四条硬规则**：

1. **所有元数据读取一律 `isCache:false`**——SqlSugar 的缓存是进程级、跨实例共享、永不失效的，
   用 `isCache:true` 就会一直显示旧结构，点"刷新"也治不好（§3.5）。缓存由插件自己按会话管。
2. **判断自增列只用 `GetIsIdentities`，且必须在任何 `IsIdentity` 之前调**（§2.3）。
   合理性校验里加一条"所有列都是 identity"的不合理特征。
3. **schema 一级由方言包直查系统目录**，不走 `DbMaintenance`——PG 上它只认 `search_path`，
   SQL Server 上它会跨 schema 串列（§3.5/§3.6）。
4. **视图/物化视图的列展开必须走方言包**，不能复用表的那条路径（返回 0 列且不抛异常）。
   PG 的物化视图还要单独查 `pg_class` 才能出现在树里。

**一条要写进文案的表现**：PG 上大小写混合的表，**对象树里画得出来、一点开就报"表不存在"**（§5.4.5）。
错误面要认得这个组合并直说原因，而不是让用户以为表被删了。

### 7.3 结果网格：这是最大的工程项（真机重写）

要求（缺一不可）：双向虚拟化、列宽自适应 + 拖拽 + 记忆、单元格/行/列三种选区、
NULL/空串/二进制/超长文本区分显示、就地编辑 + 未提交高亮、分页与"取更多"。

**本轮把数据通路量了个透。以下每条都是可以直接抄进实现的结论。**

#### 取数：流式方向对，但收尾会付一次全表排水

| 口径 | PG（50 万行 × 100 列） | MSSQL | MySQL |
|---|---|---|---|
| `GetDataReader` 首行到达 | **11 ms** | 8~19 ms | 真流式（4.4 万亿行的交叉连接首行 6.6 s 到手，内存平在 20 MB） |
| 200 行到手 | **21 ms** | 19 ms | — |
| 峰值内存增量 | 3.8~6.5 MB | 同量级 | 20 MB |
| `GetDataTable` 全量落地 | **24057 ms / 托管堆 1491.7 MB** | 18136 ms / 1492.2 MB | 2.09M 行×5 列 = 474 MB / 6.8 s |

**默认取 200 行**这条设计站得住（21 ms）。**但"读 200 行就 break"有一个隐藏代价**：

```
reader.Dispose() 会阻塞着把剩余结果集从网络全部拉完再丢掉
  PG   wide 6550 ms / tall 1378 ms
  MSSQL wide 2791 ms / tall 550 ms
  MySQL wide 6672 ms
  Pooling=false 也一样；无界结果集上直接 Dispose 会永久挂死调用线程
两条解法实测都有效：
  ① 先 cmd.Cancel() 再 Dispose  →  PG 146 ms / MSSQL 4 ms / MySQL 20 ms
  ② 根本别发全表 SQL，让服务端 LIMIT 200  →  Dispose 0 ms
```

→ **硬规则：网格取数一律走"服务端 LIMIT + `GetDataReaderAsync`"；
若确实要中途放弃结果集，必须先 `Cancel()` 再 `Dispose`，且这件事绝不能在界面线程上做。**
（好消息：break + Dispose 之后连接是干净的、服务端没有残留查询，同一个 client 再查 `select 1` 是 0 ms。）

#### 内存：分界线不是 DataTable，是"有没有全量物化"

修正一个直觉：**`DataTable` 本身不是内存罪魁**。同样 hold 住 50 万行 × 100 列——
`DataTable` 1491 MB（3111 字节/行），自己攒的 `object[][]` 反而更费 2233 MB（4681 字节/行）。
**流式读完整表但不 hold，内存恒定在 ~10 MB。**

外推文档说的"100 列 × 100 万行"：**≈ 3~6 GB，取决于列的 CLR 类型**
（复核者用全 `text` 列的表量到 5955 字节/行，接近两倍——所以不能给单一数字）。
**一屏 200 行 × 100 列只有 0.7~1.0 MB**，完全不构成压力。
单元格取值方式对耗时几乎没影响，但对分配量差 2.5 倍（1000 行×100 列：`GetValue` 装箱 5.81 MB、
强类型访问器 3.31 MB、转 string 8.17 MB）。

#### 分页：SqlSugar 的分页在这里不能用

| 问题 | 实测 |
|---|---|
| **每翻一页都多一条全表 `COUNT`** | `ToPageList(page,size,ref total)` 第二条 SQL 是 `SELECT COUNT(1) FROM (SELECT t.* FROM (原SQL) t) CountTable`，**没有任何缓存**。PG tall 带 total 175~458 ms、不带 1~272 ms；MySQL InnoDB 上 tall 385 ms / wide 638 ms |
| **SQL Server 上用户 SQL 带 `ORDER BY` 就直接失败** | Msg 1033，带不带 total 都一样——因为 SqlSugar 把原 SQL 塞进派生表。**这让"翻页用 `SqlQueryable(原SQL).ToPageList`"在 MSSQL 上对绝大多数真实查询不成立** |
| **SQL Server 的兜底排序键是 `GetDate()`** | 生成 `ROW_NUMBER() OVER( ORDER BY GetDate() )` —— 不确定函数，顺序无任何保证。插件自己拼 `OFFSET/FETCH` + 用户 `ORDER BY` 完全正常且更快 |
| **深分页崩塌** | 页大小 200：PG tall `offset` 0/19800/199800/1999800 → 0/2/24/312 ms；PG wide → 3/26/238/606 ms；MySQL 窄表到 100 万 offset 只要 177 ms，**宽表到 10 万 offset 就 850 ms**（崩塌程度取决于行宽不是行数）。**键集分页（`where id > X`）在全部深度都是 0~4 ms** |

→ **三条设计结论**：
① **用户手敲的 SQL 的分页，插件自己拼**（`OFFSET/FETCH` / `LIMIT`），不走 `ToPageList`；
② **总数默认用估算值秒回**（PG `pg_class.reltuples` 0.3~7 ms、MSSQL `sys.dm_db_partition_stats` 5~28 ms），
底栏显示"约 N 行"，**点了才做精确 `count(*)`**。
（复核修正：MSSQL 的估算优势没有 PG 那么夸张——小表上几乎没优势，大表上只快 3.5 倍。设计仍成立，但不要承诺"0~1 ms"。）
③ **提供"跳到最后一页"要谨慎**，深分页在宽表上是秒级；优先提供键集分页的"下一页"。

#### 排序：不加 tie-breaker 就会重复和丢行

**这是本轮最容易被忽略、后果却最直接的一条**：

```
PG 18.1，10 万行，每页 1000 翻完 100 页：
  无 ORDER BY          → 重复 0 / 漏 0
  ORDER BY id asc      → 0 / 0
  ORDER BY status asc（有索引）→ 0 / 0
  ORDER BY grp desc（10 个取值、无索引）→ 取回 100000、去重后 85472
                                          重复 14528 行 / 漏 14528 行
  ORDER BY status asc, id asc（tie-breaker）→ 0 / 0
```

→ **硬规则：用户点列头排序时，插件必须自动追加主键（或任一唯一键）作为 tie-breaker。**
（SQL Server 2025 在同一组用例上五种排序全 0/0——**未复现不等于安全**，`ROW_NUMBER` 遇并列同样无顺序保证。）

#### 值的显示：四类可区分，但超长文本是内存杀手

四类值在 `IDataReader` 层面天然可区分且各库一致：
`NULL` → `DBNull.Value`、空串 → `Length=0` 的 `String`、二进制 → `byte[]`、超长文本 → `string`。

**超长文本必须服务端截断**：

| 做法 | 200 行 × 1MB 文本 |
|---|---|
| `GetDataTable` | **+400 MB（PG）/ +450 MB（MSSQL）托管堆** |
| 流式逐行读整条但不 hold | 只涨 6~10 MB，但要 1.2~1.5 s（PG）/ 0.7~0.8 s（MSSQL） |
| **默认 `CommandBehavior` 下的 `GetChars`** | **救不了**——它照样先把整列缓冲 |
| `SequentialAccess` + `GetChars` | 分配从 4,196,888 → **2,808 字节**（1495 倍）。省内存但**不省带宽** |
| **服务端 `left(col,256)`** | **只要 0.2 MB，PG 794 ms、MSSQL 4~17 ms** ← 唯一有效的护栏 |

（MySQL 侧更绝：`GetChars` 分段取前 N 个字符**一分钱都省不下来，反而更贵**。）

**要认的 CLR 类型清单**（真机取得，含版本注记）：

- PG：`inet`→`IPAddress`、`interval`/`time`→`TimeSpan`、`int[]`→`Int32[]`、`jsonb`→`String`、
  `uuid`→`Guid`、`bit(4)`→`BitArray`、`money`→`Decimal`；
  **Npgsql 10 起 `date`→`DateOnly`、`time`→`TimeOnly`、`cidr`→`IPNetwork`**（§3.8）
- MSSQL：`xml`→`String`、`money`→`Decimal`、`uniqueidentifier`→`Guid`
- MySQL：**`tinyint(1)`→`Boolean`（默认，必须关掉，§5.1）**、`bit(1)`→`UInt64`、`json`/`enum`/`set`→`String`、
  `blob`/`varbinary`/`geometry`→`Byte[]`、`year`→`Int32`、`bigint unsigned`→`UInt64`
- **MySQL 的 reader 列类型名不足以还原真实 DDL 类型**：`VARBINARY(32)` 和 `BLOB` 都叫 `BLOB`，
  `LONGTEXT` 和 `VARCHAR` 都叫 `VARCHAR` → 要显示准确列类型必须另查 `information_schema`

**单元格级容错（新增硬规则）**：PG 上一格读失败**不能让整页失败**——
`infinity` 时间戳、`numeric NaN`、超 `decimal` 范围、未知 OID 的枚举都会抛（§3.8）。
要退到 `col::text` 或显示 `<不可映射: 原因>`。

**一条容易误判的性能事实**：TTFB 快只在**没有阻塞算子**时成立。同一张表
无 `ORDER BY` 时第 1 行 11 ms，改成按一个没索引的列排序后第 1 行要 **1061 ms**——服务端必须先排完全表。

### 7.4 SQL 编辑器

- **AvaloniaEdit**（宿主已引 `Avalonia.AvaloniaEdit 12.0.0`，插件只需编译期引用，
  运行时按 ALC 规则回落装载方那份——**因此本插件必须 `inProcess`**）；
- **高亮**：内置 `TSQL-Mode.xshd` 可作起点，但关键字表是 T-SQL 口径。**每方言一份 `.xshd`**（EmbeddedResource）；
- **补全**：数据源是 §7.2 的元数据缓存。**补全必须来自当前连接的真实元数据**，不是写死的关键字表；
- **执行语义**：`Ctrl+Enter` 执行光标所在语句，`Ctrl+Shift+Enter` 执行全部/选中。
  多语句按分号切分并逐条显示结果标签，任一条失败即停并高亮到那一行；
- **历史**：每次执行落插件私有时序库，`Ctrl+H` 调出，可搜索、可重放。

#### 错误定位：三步算法（真机原料）

初版承诺"定位到出错的那一行"。真机把可用原料摸清了，**能力按方言分档**：

| 场景 | PostgreSQL 18.1 | SQL Server 2025 | SQLite |
|---|---|---|---|
| 语法错 | `SqlState 42601` + **`Position=42`（1-based 字符）** + `Statement.SQL` → **行 + 列** | `Number 102`, `Class 15`, **`LineNumber=3`** → 行 | `ErrorCode 1`，**无位置** |
| 表/列不存在 | `42P01`/`42703` + Position | `208`/`207`，`LineNumber` | 无位置 |
| MySQL | —— | —— | 语法错的 Message 里带 `at line N`，可用正则解析 |

**两个坑**：

- **PG 的 `Position` 相对的是"改写后的、单条"语句**，既不是整批文本、也不是用户原文——
  直接拿去数用户输入会指错行（多语句、长参数名两种偏法都复现了）。
  **解法**：`PostgresException.Statement.SQL` 装着失败那条语句的**实际发送文本**，`Position` 相对它是精确的。
- **MSSQL 的 `LineNumber` 相对整批**（CRLF/LF 无差别），但**不保证指向出错 token 那一行**——
  同一个 207 在 `select` 列表里报 token 行、在 `where` 子句里报语句起始行。
  → 对 SQL Server 的措辞降级为「定位到出错的那**一条语句**」，不要承诺列级。

**三步算法**：
① 插件自己按分号切句并记下每条的起始行；
② 执行时数已消费的结果集，失败的是第 N+1 条（PG 与 MSSQL 都成立，编译期与运行期错误都成立）；
③ PG 用 `Position` 对 `Statement.SQL` 换算句内行列再加起始行；MSSQL 直接用 `LineNumber`
（`Ctrl+Enter` 只发单条时要把光标所在语句的起始行加回去）。
`Position` 按 **UTF-16 字符**计（`\r` 算一个），直接按 C# string 索引换算，不要做 UTF-8 字节换算。

**多语句的失败后果按方言不同**（§5.3 的事务态机）：PG 整批回滚，MSSQL 前面的已提交。
执行完成面板必须明写"前 N 条已提交 / 已随批回滚"。

### 7.5 数据编辑与回写

就地改一格之后要生成 `UPDATE`，前提是**能唯一定位这一行**：

| 情形 | 处理 |
|---|---|
| 结果集含完整主键 | 按主键生成 `UPDATE … WHERE pk = @pk`，**同时带上原值做乐观并发** |
| 无主键但有唯一索引 | 用唯一索引，并在底栏说明"按唯一索引 `ux_xxx` 定位" |
| 都没有 | **网格只读**，底栏说清原因，并给"改为按全列匹配（危险）"的显式入口 |
| 结果来自 JOIN / 聚合 | 只读 |

**提交前必须给 SQL 预览。** 这是本设计与"点了保存就发出去"的分界线。
（预览用 `ToSqlString()`，但 PG 上它带 `N''` 前缀，要么剥掉要么标注"仅供阅读"，见 §5.4.4。）

**真机加的四条**：

1. **主键来源必须走方言包**，不能用 `GetPrimaries`——MySQL 上它有大小写不敏感缓存会串表（§3.7）。
2. **可写列集合必须剔除计算列/生成列**——`DbMaintenance` 认不出它们，带上就报错（§2.3）。
3. **没有 `WhereColumns` 就是全表 UPDATE**，且 `Updateable(字典)` 不会报错（§5.4.3）。
4. **PG 侧要保证"SqlSugar 先手"**：插件在第一个 PG `SqlSugarClient` 之前碰过 Npgsql 的话，
   **字典 CRUD 写回会当场炸**（§3.8）。这条要写成装载回归测试。

### 7.6 安全护栏：三档，按 `environment` 分级

| 档 | 判据 | development | staging | production |
|---|---|---|---|---|
| **绿** | `SELECT` / `EXPLAIN` / `SHOW` | 直接执行 | 直接执行 | 直接执行 |
| **黄** | 带 `WHERE` 的 `UPDATE`/`DELETE`、`INSERT` | 直接执行 | 直接执行 | **确认框**（显示预估影响行数） |
| **红** | **无 `WHERE` 的 `UPDATE`/`DELETE`**、`DROP`、`TRUNCATE`、`ALTER`、`GRANT`、跨库操作 | 确认框 | 确认框 | **确认框 + 键入对象名** |

外加硬规则：

1. **`readonly` 连接**：黄红两档在**发出之前**被拒。不是靠数据库的权限。
2. **影响行数预估**：红档确认框里显示 `SELECT count(*)` 的结果。
3. **表名白名单校验（新增）**：`AS(表名)` 这条路径 SqlSugar 完全不转义，实测能删表（§5.4.4）——
   插件传给 `AS`/`Queryable` 的表名**只能来自对象树的已知标识符**。
4. **production + 多语句 + 含写操作**的确认框里，要带上"这批在本方言下是否原子"这句话（§5.3）。

### 7.7 键盘优先

| 键 | 动作 |
|---|---|
| `Ctrl+Enter` / `Ctrl+Shift+Enter` | 执行当前语句 / 执行全部 |
| `Ctrl+P` | 对象快速跳转 |
| `Ctrl+H` | SQL 历史 |
| `F5` | 刷新当前对象/结果 |
| `Esc` | 取消正在跑的查询 |
| `Ctrl+D` | 复制当前行 |
| `Alt+↑/↓` | 结果标签间切换 |

### 7.8 空状态、错误与降级

- **方言不支持某项**：显示"该数据库不提供执行计划"，**而不是空白**或永远转圈；
- **元数据取不到**：区分"没有权限"（给出需要的权限名）与"该方言无此概念"；
- **查询失败**：错误原文 + 按 §7.4 的三步算法定位；
- **连接断了**：网格保留最后一次结果（不清空），顶部横幅提示重连。

真机补的五条：

1. **"查询超时"与"连接断了"要分成两档**，两边都能干净区分：
   PG 超时是 `TimeoutException` 或 `57014`，连接断是 `57P01`/`SocketException 10054`；
   MSSQL 超时是 `Number=-2`，连接断是 `596`/`233`/`Class 20`。文案分别是"查询超时，已取消"与"连接已断开"。
2. **SQL Server 上"连接断了"默认用户看不见**（驱动静默重连），要显式 `ConnectRetryCount=0` 才由插件接管。
3. **单元格级容错**：一格读失败显示 `<不可映射: 原因>`，不要让整页失败（§7.3）。
4. **约束冲突要显示到底是哪个值**：PG 加 `Include Error Detail=true` 后能拿到
   `Key (id)=(1) already exists.`；MSSQL 这句在 Message 里（`The duplicate key value is (1).`）。
5. **PG 上大小写混合表的表现是"树里有、点开报不存在"**，错误文案要直接说破这一点（§5.4.5）。

---
## 八、只有 VelaShell 能做的三件事

### 8.1 从 SSH 会话零打字建连（杀手级）

命令面板 → "从 SSH 会话探测数据库"：

1. `RemoteExec` 在已连接的 SSH 会话上跑 `ss -lntp` / `netstat`，认出 3306/5432/1433/1521 端口；
2. `RemoteFs` 读 `/etc/my.cnf`、`/etc/postgresql/*/main/postgresql.conf`，拿到端口、`bind-address`、数据目录；
3. `ProposeConnectionAsync` 弹出宿主的「新建连接」对话框并预填，
   **`jumpSession` 预选这条 SSH 会话所属的配置**——隧道不用手开。

Navicat 用户的真实路径是：SSH 上去 → `cat my.cnf` → 抄端口 → 回 Navicat → 建隧道 → 填 127.0.0.1 → 试连。
六步，全是人肉状态同步。这里是**一步**。

> 与 Redis 插件同一条边界：**插件不能自己写宿主的会话库**，只能提议，由用户过一眼再保存。

### 8.2 与终端并排

`mysqldump`、`pg_restore`、`explain analyze` 之后要看的 `iostat`——这些在纯 GUI 工具里是断裂的。
在 VelaShell 里，数据库文档与 SSH 终端是**同一个 dock 里的两个标签**，可以左右分栏并排。

### 8.3 操作可审计

每条 DDL/DML 落插件私有时序库：SQL、连接、库、执行者、耗时、影响行数、是否走了确认框。
"昨天谁把那张表的索引删了"——商业工具答不上来。

**真机给这条打三个补丁**：

1. `Aop.OnLogExecuting` 给的是**参数化 SQL + 参数表**，不是拼好值的最终 SQL——想存"最终 SQL"要自己回填；
2. **裸 `DbCommand` 一条都不触发 AOP**——而用户手敲的 SQL 走的正是裸 `DbCommand`，
   所以审计必须在插件自己的执行入口上埋点，不能只靠 AOP；
3. `DbMaintenance` 的 `isCache:true` 读路径也不过 AOP（§2.2）。

---

## 九、包体与生命周期（真机重写）

初版量到"`SqlSugarCore` 输出目录 58.5MB / 86 个文件"——**复现成功**（实测 58.62 MB / 86 个文件），
但组成部分少算了一块：顶层 17.74 MB（36 个托管 dll）+ `runtimes/` 38.30 MB（27 个 RID 目录）
+ **10 个 `Microsoft.Data.SqlClient` 本地化卫星资源 dll（2.57 MB）**，39+37+10=86 正好对上。

### 9.1 现状核实

`plugins/Directory.Build.targets:49` 的真实写法比初版引用的多一层：
`Include="$(TargetDir)**\*" Exclude="$(TargetDir)**\*.pdb;$(TargetDir)**\*.xml"`（pdb/xml 已排除）。
`VelaPluginShip` 的语义是 `targets:48` 的构建条件，为 false 时整个 ItemGroup 不生成。

**仓库里没有任何 RID 裁剪逻辑，而且方向是反的**：宿主两处显式把 `RuntimeIdentifier` 摘掉，
插件永远以 RID 无关方式构建，所以 27 个 RID 的原生库必然全进包。
**这个浪费今天已经在发生**——AI 插件的 `libonigwrap` 带了 13 个 RID / 6.69 MB，
单平台包里约 6.16 MB 是死重。现有插件 payload 体积：Ai 49 文件/32.19 MB、Redis 8/2.64 MB、
S3 7/2.44 MB、Telnet 5/0.03 MB。

### 9.2 裁剪：初版的措施 1 会把 SQL Server 打死

> 初版措施 1：「只收当前 RID 的 `runtimes/<rid>/`」

**实测这个做法会把 SQL Server 打死**：只留 `runtimes/win-x64` 得到 22.51 MB/51 文件，
但一碰 SqlServer 就 `FileNotFoundException`——因为 `Microsoft.Data.SqlClient` 的托管实现在
RID 图的**父节点** `runtimes/win/lib/net8.0/` 里（7 个文件 3.65 MB），
`AssemblyDependencyResolver` 认 `deps.json` 的 `runtimeTargets`，顶层那份 890 KB 的 AnyCPU 回落根本不会被用。
要手工裁就得连 `win`（以及 linux 侧的 `linux`/`unix`）一起留，那是 26.16 MB——**比正确做法还大**。

**正确做法是给插件构建传 `RuntimeIdentifier` + `SelfContained=false`**：
`runtimes/` 目录整个消失，原生库与 RID 专属托管 dll 全部拍平到顶层，**自动绕开了 RID 图的坑**。
`EnableDynamicLoading` + RID 的组合行为：输出目录分叉到 `bin/<cfg>/net11.0/<rid>/`，
`deps.json`/`runtimeconfig.json` 照常生成，插件 ALC 能正常解析原生库（RID 无关的 Runner 装载 RID 构建的探针全绿）。

**第三条裁剪杠杆（初版没提）**：`<SatelliteResourceLanguages>en</SatelliteResourceLanguages>`
一行省 2.57 MB，代价只是 SqlServer 的**驱动层**错误消息不再本地化。

### 9.3 真实数字

| 口径 | win-x64 | linux-x64 | osx-arm64 |
|---|---|---|---|
| 原样 build（基线） | 58.62 MB / 86 文件 | — | — |
| RID 构建 + 卫星资源裁剪（**全方言驱动保留**） | **22.19 MB / 42** | 20.23 MB / 40 | 20.43 MB / 40 |
| 再剔掉 4 个大方言驱动 | **14.96 MB / 38** | 12.99 MB / 36 | 13.19 MB / 36 |

（win-arm64 14.75/38、linux-arm64 13.02/36、osx-x64 13.22/36。
**只在 win-x64 真机跑过**，其余 RID 是 `dotnet publish` 出来的真实文件统计但未跑运行验证。）

比原样 build **省 74.5%**。

### 9.4 "可选驱动包"路线可行，但 v1 不拆

**删掉用不到的方言驱动完全安全**——这是这条路线的关键证据：
35 方言离线能力矩阵在删驱动前后 **diff 为 0 行**（45 行输出逐字节相同），
PostgreSQL 18.1 只读 11 项全 OK，SQLite 真机 Entry 全量与基线逐条一致，SqlServer/MySQL 驱动照常装载。
**失败是惰性的**：`SqlSugarClient` 构造成功、`DbMaintenance` 也成功返回 `OracleDbMaintenance`，
直到第一次访问 `db.Ado.Connection` 才抛 `FileNotFoundException`。启动期一点事没有。

> **注意这里的失败模式与"未捆绑方言"不是一回事**（§3.3 末尾的对照表）：
> 这里删的是 **ADO 驱动 dll**（`Oracle.ManagedDataAccess.dll`），SqlSugar 自己的 provider 还在，
> 所以 `DbMaintenance` 拿得到，炸在 `Ado.Connection`，异常是 `FileNotFoundException`；
> 而未捆绑方言缺的是 **SqlSugar 的 provider 扩展包**，`DbMaintenance` 就抛，异常是 `SqlSugarException`。
> 两者的预检点不同：前者只能靠 `File.Exists`，后者可以直接试 `db.DbMaintenance`
> （**但试完必须复位 `InstanceFactory.CustomDllName`**，否则整个 ALC 被污染）。

四个可选驱动精确体积合计 **7.24 MB**：Oracle 5.13 / Kdbndp 0.86 / Dm 0.84 / Oscar 0.41。

**§十三 待决 2 的答案：v1 不拆，全捆绑 22.19 MB。** 理由是体积账算不过体验账——
Oracle 5.13 MB 在裁剪后的 22.19 MB 基数上只占 23%，而拆出去后用户选 Oracle 的观感是
"**方言选得上、连接表单填得完、点连接才炸 `FileNotFoundException`**"。
真要拆时必须自己在方言下拉里做 `File.Exists` 预检才能把错误提前。

### 9.5 落地时要一起修的一个坑

带 RID 的输出目录是无 RID 输出目录的**子目录**。同一台机器上两种构建都跑过之后，
无 RID 那次的 `$(TargetDir)**\*` 会把 `win-x64/` 整个再收一遍——
实测递归清单从 84 涨到 **128 files / 80.94 MB**（其中 44 files / 24.44 MB 来自嵌套子目录）。
**两处通配要加排除**：`plugins/Directory.Build.targets:35` 与 `:49`。

宿主 MSBuild 调用加 `Properties="…;VelaPluginRid=$(RuntimeIdentifier)"` 能穿过既有的 `RemoveProperties`
（它摘的是 `RuntimeIdentifier` 这个名字），插件 props 里再转回来。
（这组仿宿主实验**未在真仓库验证**，见附录 B。）

---

## 十、测试策略

对齐 Redis 插件的 179 项：

| 层 | 内容 | 不需要真库 |
|---|---|---|
| **纯逻辑单测** | 连接设置解析、SQL 语句切分、护栏分档、元数据合理性校验、文案表无重复键（`LocTests`） | 是 |
| **方言包快照测试** | 每方言的 SQL 资产对着期望字符串断言 | 是 |
| **元数据映射测试** | 把各方言 `information_schema` 的真实返回**录成夹具**，测映射逻辑 | 是 |
| **headless 面板测试** | AXAML 真装载一次 + 交互 | 是 |
| **ALC 装载测试** | 探针改造成回归测试 | 是 |
| **真库集成测试** | SQLite 随时可跑；PG/MySQL/SQL Server 按可用性跳过 | 否 |

**真机新增的必做回归项**（每一条都对应本轮一个实测坑，写成测试才不会退化）：

1. **PG 客户端装配纪律**：断言插件在第一个 PG `SqlSugarClient` 之前没有装载/使用过 Npgsql
   （§3.8 的顺序竞争会让字典 CRUD 写回当场炸）。
2. **`ConnMoreSettings` 四开关**：对着大小写混合的表断言五个入口都能通（§5.4.5）。
3. **谓词构造器**：构造 `A OR (B AND C)` 断言行数（`ConditionalTree` 的 `OR` 会被吞，§5.4.1）。
4. **`In` 展开**：断言筛选行的 `In` 被展开成 `Equal` 的 `OR` 组且走参数（§5.4.4）。
5. **表名白名单**：断言 `AS()` 只接受来自对象树的标识符（§5.4.4 能删表）。
6. **排序 tie-breaker**：断言点列头排序时 SQL 里带上了主键（§7.3 会重复+丢行）。
7. **禁用清单**：断言插件代码里不出现 `GetDataTableAsync`、
   带 `CancellationToken` 形参的 `ExecuteCommandAsync` 重载、`IDbMaintenance.DropConstraint`、
   `IsIdentity(表,列)`、`IsValidConnection()`。这是一条静态检查，最便宜也最值。
8. **元数据一律 `isCache:false`**：同上，静态检查。
9. **异常翻译快照**：每条判据配一个快照（驱动升大版本会改异常类型与文案，§5.3）。
10. **合理性校验**：喂"所有列都是 identity"、"索引名是纯数字"、"列长度恒 0"三种假数据，断言被拦下。
11. **`InstanceFactory` 复位**：先碰一次未捆绑方言（如 `ClickHouse`）让它失败，
    再断言 SQLite/MySQL/PG/SqlServer **仍然能取到 `DbMaintenance`**（§3.3 的静态污染）。
    这条不写测试，将来某次重构漏掉复位就会变成"用户选错一次方言之后插件全废"。
12. **用户可见方言不丢**：断言建完 `SqlSugarClient` 之后插件仍知道用户选的是 TiDB 而不是 MySQL
    （`config.DbType` 会被就地改写，§6.2）。

> 本机现状（08-19）：**无 Docker，但有 podman**（WSL 后端）。本轮的 MySQL 就是 podman 起的。
> PostgreSQL 用本机 18.1 二进制起了个临时集群；SQL Server 用新建的 LocalDB 实例绕开了登录触发器。
> **建议把 `docker-compose.test.yml` 扩成 podman 也能用的形式**（`podman-compose` 或直接 `podman run`），
> 并把本轮的起库脚本收编进 `tests/`。

---

## 十一、里程碑与工作量

| 里程碑 | 内容 | 规模 |
|---|---|---|
| **M0 骨架** ✅ **主体完成** | 五个连接类型注册、连接表单、连接/断开/重连/探活、**异常翻译（三份判据表）**、纪律回归测试、四台真机连通性验收。**包体裁剪只做了插件能自己做的那一条**（卫星资源），RID 裁剪要宿主侧改动（见 §11.1 的遗留） | 实际 ~1.4k 行 + 测试 ~0.6k |
| **M1 看得见** | 对象树（懒加载 + 缓存 + 搜索）、表结构只读视图、方言包接口 + MySQL/PG/SQLite 三份 | ~3.5k 行 |
| **M2 能用** | SQL 编辑器（高亮/补全/执行/历史/**错误三步定位**）+ **结果网格**（虚拟化/选区/导出/**服务端截断与分页**） | ~5k 行 |
| **M3 敢用** | 数据就地编辑与回写、三档护栏、SQL 预览、**事务态机**、审计落库 | ~2.2k 行 |
| **M4 好用** | 表设计器、执行计划、运维面、SQL Server/Oracle 方言包 + T1 十种同族方言、`DbFirst` 代码生成 | ~3k 行 |
| **M5 铺开** | T2 方言 + 可选驱动包机制 | ~2.5k 行 |
| **M6 变形** | ClickHouse / TDengine（先评估再动工） | ~1.5k 行 |

M0–M4 合计约 **13–15k 行**（比初版估的 12–14k 上调，因为方言包要补的清单变长了、
结果网格自研的估算也上调了）；M5+M6 再约 4k；另加约 400 条文案 × 5 语。

**宿主侧**：口子零（驱动前缀 + 驱动包进宿主）约 100–150 行，口子一（`.trash` 隔离区）约 50–80 行。

**关键路径仍是 M2 的结果网格**——§4.4 的口子二必须先拍板，而且拍板前要先联网验证
`Avalonia.Controls.DataGrid` 到底做不做列虚拟化。

### 11.1 M0 已落地的东西（2026-08-20）

代码在 `plugins/VelaShell.Plugin.Sql/`，测试在 `tests/VelaShell.Plugin.Sql.Tests/`。

| 文件 | 它承载的是哪条结论 |
|---|---|
| `SqlDialect.cs` | **用户可见方言**必须与 `ConnectionConfig.DbType` 分开存——后者会被 SqlSugar 就地改写（§6.2） |
| `SqlSugarGate.cs` | **本插件唯一允许 `new SqlSugarClient` 的地方**。五条纪律全在这里：`InstanceFactory` 复位（§3.3）、PG 装配顺序自检（§3.8）、`IsAutoCloseConnection=false`（§5.2）、大小写四开关（§5.4.5）、`Ado.CommandTimeOut`（§5.1） |
| `SqlConnectionString.cs` | §5.1 的逐条修正：`Connect Timeout` 的正确键名、`TreatTinyAsBoolean=false`、`AllowUserVariables`、PG 的 `Include Error Detail` 与 `Search Path`、MSSQL 的 `ConnectRetryCount=0`；**没有** `charset` 那个摆设 |
| `SqlExceptionTranslator.cs` | §5.3 的三份判据表。判决抽成纯函数，于是"4060 要排在 18456 前面"这条**没有服务器也测得住** |
| `SqlConnection.cs` | 连接由插件自己 `Open()`（§5.3）；探活不看 `conn.State`（§5.2）；`Dispose` 只对空闲连接调（§3.10） |

**验收**：37 项测试全绿、0 跳过，其中 8 项是**真机连通性**——
SQLite、PostgreSQL 18.1、MySQL 8.4.11、SQL Server 2025 LocalDB 全部真连上，
外加"密码错要翻成认证失败而不是连接失败"与"端口不通要翻成连接失败"两条翻译验收。

### 11.2 M1–M3 已落地的东西（2026-08-20）

M0 之后一口气把对象树、SQL 编辑器、结果网格与就地编辑做完了。新增的落点：

| 文件 | 它承载的是哪条结论 |
|---|---|
| `Metadata/IDialectPack.cs` + 五份方言包 | **元数据一律直查系统表，一个 `IDbMaintenance` 方法都不调**。§2.3 那张表里"它给错的"每一项——自增、生成列、视图的列、索引唯一性、跨 schema 串列——都由方言包自己查 |
| `Execution/SqlStatementSplitter.cs` | 按分号切句**并记下每条的起始行列**，这是 §7.4 错误定位算法的地基。字符串/注释/PG 美元引用里的分号一律不切 |
| `Execution/SqlGuard.cs` | §7.6 三档护栏。**按括号深度分词**——`WITH x AS (SELECT…) DELETE` 的真动词、以及 `UPDATE t SET a=(SELECT…WHERE…)` 这种**无界 UPDATE**，都只有深度感知才判得对 |
| `Execution/SqlExecutor.cs` | 走裸 `DbCommand`；**一律跳到后台线程**（见下） |
| `Execution/SqlCancellation.cs` | §3.10 的四级阶梯，含 SQLite 的 `raw.sqlite3_interrupt` 与"放弃≠Dispose" |
| `Execution/SqlErrorLocator.cs` | PG 的 `Position`（字符偏移，相对单条语句）、MSSQL 的 `LineNumber`（相对整批）、MySQL 的 `at line N` |
| `Execution/SqlResultSet.cs` | `SequentialAccess` + `GetChars` 截断超长文本；**单元格级容错**（一格读不出来不让整页失败） |
| `Execution/SqlWriteBack.cs` | §7.5 回写。**自己拼参数化 SQL 而不用 SqlSugar 的字典 CRUD**——后者漏写 `WhereColumns` 会静默生成全表 UPDATE |
| `Ui/*` | 左树 + 上编辑器 + 下网格；NULL/空串/二进制/超长文本四态可辨；提交前给 SQL 预览 |

**又逮到两个只有真跑才会暴露的东西**：

1. **`Microsoft.Data.Sqlite` 的异步是同步套壳**（调研 §3.10 记过，但当时只当成"取消的坑"）。
   后果比想象中大：`await executor.ExecuteAsync(...)` 在 UI 线程上调用会**同步跑完整条查询**，
   表现是整个窗口冻住、连取消按钮都点不到。`SqlExecutor` 因此改成**一律 `Task.Run` 跳到后台线程**。
   这条是端到端取消测试逼出来的——单元测试和"能编译"都发现不了。
2. **PG 方言包里的 `unnest(conkey, confkey) WITH ORDINALITY` 是错的**：多参数 `unnest`
   在 PG 里**只允许直接出现在 FROM 里**，配 `WITH ORDINALITY` 必须写成
   `ROWS FROM (unnest(a), unnest(b)) WITH ORDINALITY`。服务端报的是
   `42883: function pg_catalog.unnest(smallint[], smallint[]) does not exist` ——
   "函数不存在"很容易让人以为是版本问题，其实是语法位置不对。

**验收**：99 项测试全绿、0 跳过。其中真机的有：四台服务器连通性、三份方言包的元数据逐项对账
（列类型原文 / 自增 / 生成列 / 默认值表达式 / 索引唯一性 / 外键 / 视图的列 / **PG 跨 schema 同名表不串**）、
端到端全链路（对象树 → 双击表 → 网格出数）、**取消跑飞的查询**（6 ms 生效）、
**改一格并被乐观并发拦下**、面板 headless 装载、只读连接拒写。

**当时仍然欠着的**（这份清单写于 M3 收尾，**下面标了 ✅ 的已在 M4 收掉，见 §11.3**）：

- ✅ **SQL Server 方言包**正在从另一份真机验过的实现移植过来（它当时基于一套自己发明的契约）；
- ✅ **Oracle 方言包**已落地但**没有真机**，全部标注未验证 —— **2026-08-20 补上了真机，见 §11.4**；
- ✅ **表设计器、执行计划、运维面**（M4）没做；
- ✅ **CSV/JSON 导出**只做到剪贴板 TSV，没有落文件；
- ✅ **包体 94 MB** 的 RID 裁剪欠账仍在（§11.1），它要宿主侧改动 —— 发布包现为 24 MB。

**M0 当时没做的**（现已补上）：面板上只有连接概览，没有对象树也没有编辑器。

**M0 留下的一个必须尽快收掉的欠账 —— 包体 94 MB。** 实测部署到
`src/VelaShell/bin/.../plugins/velashell-sql/` 的真实形态：

| 项 | 数字 |
|---|---|
| 总计 | **94 MB / 88 个文件** |
| 顶层托管 dll | 19 MB |
| `runtimes/` 全 RID 树 | **76 MB / 36 个 RID 目录** |
| win-x64 实际用到 | `runtimes/win-x64` 2.4 MB + `runtimes/win` 3.7 MB |

比调研时量的 58.62 MB 更大——因为 M0 又加了 Npgsql 10.0.3 与 SQLitePCLRaw 3.0.3，
**各自再带一棵 RID 树**。也就是说：§九 那条"RID 裁剪"不是可选优化，
**每加一个带原生依赖的包，欠账就再翻一层**。

裁不掉的原因在 §9.1 已经写清楚：**仓库里没有任何 RID 裁剪逻辑，而且方向是反的**
（宿主两处显式把 `RuntimeIdentifier` 摘掉）。这条**插件自己修不了**：
在插件 csproj 里写死 `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` 会让输出目录分叉
并把 Linux/macOS 构建打死。正解是 §9.5 的宿主侧改动
（给插件构建传 `RuntimeIdentifier` + `SelfContained=false`，并给两处 `$(TargetDir)**\*` 通配加排除）。

**注意这不只是数据库插件的账**：AI 插件的 `libonigwrap` 今天就已经带着 13 个 RID / 6.69 MB，
单平台包里约 6.16 MB 是死重（§9.1 实测）。数据库插件只是把它放大到了不能再忽略的量级。

---

### 11.3 M4 已落地的东西（2026-08-20）

M4 收的是 §11.2 结尾那张欠账单：结构页、运维面、执行计划、表设计器、导出落文件、包体裁剪。

| 文件 | 它承载的是哪条结论 |
|---|---|
| `Ui/SqlStructureTabViewModel.cs` | 结构页 + **表设计器**。列/索引/外键/建表原文四块；加列、删列、建索引、删索引各走方言包出 DDL |
| `Ui/SqlOpsTabViewModel.cs` | 运维面：会话、锁与阻塞链、杀会话。**手动刷新，永不轮询**（§7.2） |
| `Execution/SqlExport.cs` | CSV / TSV / JSON / `INSERT` 四种落文件；`INSERT` 走方言包出标识符转义 |
| `IDialectPack` 的 M4 成员 | `ExplainSql` / `SessionListSql` / `LockListSql` / `CommonTypes` / 四个 DDL 生成器 |
| `plugins/Directory.Build.props` + `.targets`、宿主 `VelaShell.csproj` | §9.5 的 RID 裁剪，**宿主侧改动** |

**这一轮逮到的东西**：

1. **包体：发布包 94 MB → 24 MB，但"开发构建里还是 98 MB"不是没裁干净。**
   两条路径本来就不同：`AddVelaPluginsToPublish` 把宿主的 `RuntimeIdentifier` 用
   `VelaPluginRid` 这个**换了名字的属性**传给插件（宿主用 `RemoveProperties` 按名字摘 `RuntimeIdentifier`，
   换名就能穿过去）。宿主 `dotnet build` 时没有 RID，`VelaPluginRid` 是空的，插件照旧以 RID 无关方式构建 ——
   于是 `bin/` 下仍是全 RID 树。**这是对的**：F5 调试不需要裁剪，而裁剪会把跨平台构建打死。
   实测发布产物：

   | 项 | 裁剪前 | 裁剪后 |
   |---|---|---|
   | 总计 | 94 MB / 88 文件 | **24 MB / 43 文件** |
   | `runtimes/` 树 | 76 MB / 36 个 RID 目录 | **整棵消失** |
   | 原生库 | 埋在 `runtimes/win-x64/native/` | 拍平到顶层（`e_sqlite3.dll`、`Microsoft.Data.SqlClient.SNI.dll`） |

   顺带自动绕开了 §9.2 那个坑：手工只留 `runtimes/<rid>/` 会把 SQL Server 打死，
   因为 `Microsoft.Data.SqlClient` 的托管实现在 RID 图的**父节点** `runtimes/win/` 里。

2. **导出的编码不能一刀切 —— 一个 BOM 在四种格式里有两种相反的正确答案。**
   初版四种格式统一用 `Encoding.UTF8` 写盘，而 `Encoding.UTF8` 这个**静态属性是带 BOM 的**
   （`encoderShouldEmitUTF8Identifier: true`），与长得几乎一样的 `new UTF8Encoding(false)` 相反。
   后果分两个方向，都是真错：

   - **JSON 带了 BOM**：RFC 8259 §8.1 的原话是实现 *MUST NOT* 在 JSON 文本开头加 BOM，
     严格解析器会在第一个字符上报错；
   - **CSV 少了 BOM**：中文 Windows 上的 Excel 会按 GBK 解，中文列名当场乱码 ——
     而 CSV 的头号消费者恰恰是 Excel。

   现在 `SqlExport.EncodingFor` 按格式分叉：CSV/TSV 带 BOM，JSON 与 `.sql` 不带。
   **这条只有断言磁盘上的头三个字节才测得出来** —— BOM 是编码器加的，`Render` 返回的字符串里根本没有它。

3. **JSON 导出把中文全转义成了 `\uXXXX`。** `System.Text.Json` 默认的 `JavaScriptEncoder`
   出于 XSS 防御会转义所有非 ASCII，于是"北京"导出来是 `"\u5317\u4EAC"` ——
   合法 JSON、也能原样读回来，但人和 `grep` 都读不了它，而数据库导出的头号用途恰恰是拿去看、拿去 diff。
   改用 `JavaScriptEncoder.Create(UnicodeRanges.All)` 而不是 `UnsafeRelaxedJsonEscaping`：
   前者只放开非 ASCII，仍然转义 `<` `>` `&`；后者连它们一起放开，导出的文件要是被谁塞进网页就成了注入面。

4. **DataTemplate 选不中是编译期完全看不见的错。** `DataType` 写错一个字，Avalonia 不报错，
   它退回默认模板 —— 屏幕上出现的是 `VelaShell.Plugin.Sql.Ui.SqlStructureTabViewModel` 这行类名，
   而 `Tabs.Count`、单元测试、构建全都照样绿。所以这两页各补了一条 **headless 渲染**用例，
   断言的是渲染结果：该出现的文字真的落在某个控件里，且**类名没有**出现在任何控件里。
   同类的还有 `IsVisible="{Binding !IsSupported}"` 这种取反绑定 —— 写反了也不报错，只是永远不显示。

**验收（2026-08-20 收尾）**：插件自己 **216 项全绿、0 跳过**（含五个方言的真机用例：
PostgreSQL 18.1 / MySQL 8.4.11 / SQL Server 2025 LocalDB / **Oracle 26ai Free** / SQLite）；
宿主侧整链路 **4 项**；全解决方案 **2381 项通过**。
唯一的红是 `TelnetSessionTests.Write_AfterServerTakesOverEcho_DoesNotEchoLocally` ——
**与本插件无关的既有抖动**，而且它**单跑也会偶发**（连跑两次:一次绿一次红），
不是并行压力导致的：那条用例在 `WriteAsync("ls")` 与服务端回显之间赛跑，
本地回显抑制稍慢一拍，读到的第一个字符就成了 `l`。
本轮没有碰过 `plugins/VelaShell.Plugin.Telnet/` 或其测试的任何一个文件。
发布包 **24 MB / 43 文件**。

**表设计器为什么把"确认"做成硬路径**：改数据错了还有乐观并发拦一道，
`DROP COLUMN` 发出去的那一刻数据就没了，而且**多数引擎的 DDL 不参与事务回滚**
（MySQL 上它还会把你正开着的事务隐式提交掉）。所以这一页没有"直接执行"这条路径，
确认框里给的也是**真要发的那条原文**，不是另拼一份给人看的。生产环境下还要手打表名 —— 与改数据同一条护栏。

#### 这一轮最严重的一个：在 SQLite 上点"计划"会把整个进程打死

`Microsoft.Data.Sqlite` 的 `GetChars` 内部走 `GetStream`，后者要调原生的
`sqlite3_table_column_metadata` 去问"这一列属于哪张表的哪一列"——
而**表达式列没有表**：`select 1+1`、任何函数或聚合结果、
以及 `EXPLAIN QUERY PLAN` 的**每一列**都是。这一问就是一个 **`0xC0000005` 访问冲突**。

而 `SqlResultReader.ReadText` 正是用 `GetChars` 做超长文本截断的（§5 那条"分块读"的实现）。
于是这条链是完整的：**用户在 SQLite 连接上点一下「计划」→ 整个 VelaShell 没了。**

三件事让它躲过了之前所有的用例：

1. **在这之前，所有 SQLite 用例读的都是真表的列**——那条路上 `GetChars` 是安全的。
   要触发它，得走 `SqlExecutor` 真跑一次 `EXPLAIN QUERY PLAN`，而那是 M4 才有的路径。
2. **`ReadText` 里那个 `catch (InvalidCastException)` 回落对它完全无效**：
   访问冲突不是异常，`try/catch` 接不住，它直接带走进程。
3. **失败的样子不像失败**：测试宿主整个崩掉，`dotnet test` 打的是
   `测试运行已中止 / 测试总数: 未知`，而不是一条红。第一眼很容易当成基础设施抽风。

修法是按方言关掉分块读（`SqlFetchOptions.AllowChunkedReads`，SQLite 上为 `false`），
走"整取再截断"。代价可控：**SQLite 是进程内的库**，分块读本来就省不下网络传输，
省下的只是一次托管堆分配——而对面换来的是不崩。

回归用例的形态也特别：它断言的是"**进程还活着**"（`SqlExecutionLogicTests`
里那条 `表达式列_SQLite上读回来不会把进程打死`），因为这个失败没有别的可断言的东西。

#### 四条只有"换一种验法"才看得见的问题

**① 插件自己的用例全绿，不等于宿主装得起来。** 插件那 200 条用例都是**直接 new 出视图模型**跑的，
它们绕过了宿主真正走的那条路：清单发现 → `PluginAssemblyLoadContext` 装载 → 能力域注册。
这条路断掉的每一种方式，表现都是同一句"页签在，点了没反应"。
所以补了 `tests/VelaShell.Infrastructure.Tests/Plugins/SqlPluginEndToEndTests.cs`：
把**构建产物里的真插件目录整个铺开**，用真 `PluginManager` 走一遍——
五个页签在不装载程序集的前提下画得出来、点一下惰性激活、注册进**工作台表而不是协议表**
（落错表的后果是宿主开出一个空的双栏文件浏览器，不报错）、最后真开一条 SQLite 会话。
最后那一条是唯一能验到"SqlSugar 与四个驱动有没有被复制到插件目录"的——
前面几条即使驱动全漏了也照样绿，因为它们碰不到 `Microsoft.Data.Sqlite`。

**② 可视树断言与"看起来对"是两件事。** `DataTemplate` 选不中、`IsVisible` 取反绑定写反、
控件宽度塌成 0、深色主题下前景与背景同色——每一种在可视树里都完全正常。
于是补了三张 headless 真渲染截图（结构页 / 运维面 / DDL 确认框）。
**第一次截图差点造成误判**：图上除了底部一排输入框全是黑的，看着像布局炸了；
实际原因是共用的 headless 宿主**刻意一个 `Vela*` 令牌都不给**（那是用来守"宿主令牌缺席时面板照样装载"的），
所有走 `{DynamicResource VelaText*}` 的前景色都解析不到。
截图用例因此自带一份令牌——**"没有配色"与"没有渲染"在图上长得一模一样**。

**③ 截图立刻抓出了三个真问题**，全都是可视树断言看不见的：
运维面那句说明被 `DockPanel` 垂直居中，飘在半空正中间像是出错了；
方言不支持时"刷新/杀掉会话"两个按钮照旧摆着，点了没有任何反应；
以及中文文案把方言名写了两遍（"SQLite 这类嵌入式方言(SQLite)…"）。

**④ 一枚一直在那儿、被这个插件翻出来的硬币。**
`VelaShell.Infrastructure.Tests` 的整链路用例是这么找插件产物的:
`Path.GetDirectoryName(typeof(TelnetPlugin).Assembly.Location)` —— 也就是**本测试项目的 bin**。
但**每个插件项目都把自己的 `plugin.json` 复制到输出目录的根下**,
而这个测试项目引了三个插件项目(HelloWorld / Sql / Telnet)。
于是 `bin/…/plugin.json` 到底是谁的,由 MSBuild 的复制顺序决定。

引入数据库插件之后那份清单变成了 `velashell.sql`,Telnet 的整链路用例
把一份 SQL 清单铺进了 `velashell-telnet/`,报 `Sequence contains no matching element`。
**那条用例此前一直是绿的,只是恰好赢了那枚硬币** —— 第三个插件一进来就翻了。
修法是两条用例都改成按路径去**各插件自己的 bin** 取(`PluginOutputLocator`),
硬币就不存在了;顺带更贴近真实——铺开的是发布时会进安装包的那棵目录树。
**验过这个修法确实有效**:把 SQL 的清单强行盖到测试 bin 根上再跑,两条用例照样全绿。

**⑤ 还有一类只有扫源码才抓得到**：`Loc` 对不认识的键**原样返回键名**，
于是漏一个键的表现是**界面上出现一行 `Sql_NoOpsForDialect`**，而"中英键集一一对应"这条检查照样绿——
两张表同时缺同一个键，它们仍然对应。本轮踩了两次、一共漏过 4 个键，
其中 `Sql_CommitLabel` / `Sql_CommitTooltip` / `Sql_RevertLabel` **已经绑在结果网格的提交/撤销按钮上**，
也就是说那两个按钮当时显示的就是键名。现在由一条**扫源码**的用例守住
（`文案表_源码里取过的键一个都不能少`，并且反过来验过它确实会失败）。

**方言给不了的时候要说出来，而不是留一片空白**（§7.8）。这一轮三处都按这条走：
SQLite 的运维面给的是"嵌入式方言没有服务端会话与锁"，不是一张空表（空表与"现在真的没人连"长得一模一样）；
拿不到建表原文时给一句说明，不是空白；方言包出不了某条 DDL 时明说，而不是拼一条大概齐的让服务端去报语法错。

---

### 11.4 Oracle 真机（2026-08-20）——「拿不到真机」这个判定本身是错的

调研期判定"拉不动 Oracle 镜像"，于是整份 `OraclePack.cs`（1200+ 行）是照官方文档写的离线推断，
源码里散着十几处 `【未验证】`。**这一轮把它接上了真机。**

**怎么接上的**：官方源 `container-registry.oracle.com` 确实慢（5 MB/min）。
换 `gvenzl/oracle-free:23-slim`（Testcontainers 生态的标准 Oracle 测试镜像，1.99 GB）后
经 `docker.1ms.run` 镜像站拉下来，**约 20 分钟**。
中间试过的源与失败原因，本身就是一份可复用的记录：

| 源 | 结果 |
|---|---|
| `container-registry.oracle.com/database/free` | 通，但 5 MB/min → 6.7 小时 |
| `docker.m.daocloud.io/gvenzl/oracle-free` | **不在白名单**（daocloud 只镜像 library/*） |
| `docker.io/gvenzl/oracle-free` | 连不上（`registry-1.docker.io` i/o timeout） |
| `hub.rat.dev` | `authentication required` |
| `docker.xuanyuan.me` | DNS 解析不了 |
| **`docker.1ms.run/gvenzl/oracle-free:23-slim`** | **成功** |

真机：Oracle AI Database 26ai Free Release **23.26.2.0.0**，podman 容器 `velaspike-oracle`，
127.0.0.1:11521/FREEPDB1，测试用户 `VELASPIKE`（`connect, resource, select_catalog_role, alter system`）。

**逮到的真 bug（这份包里唯一一个致命的）**：
**`DATA_DEFAULT` 读回来是空串，不是默认值。**
原因是 **ODP.NET 托管版的 `InitialLONGFetchSize` 默认是 0** —— 不是"取一点点"，
是**一个字节都不取**，而 `ALL_TAB_COLUMNS.DATA_DEFAULT` 恰恰是 `LONG` 类型。
SQL 侧没有绕法（`LONG` 进不了 `SUBSTR` / `TO_CHAR` / `CAST`），只能设这个属性；
而它只在 `OracleCommand` 上，方言包手里只有 `DbCommand`。
修法是给 `DialectPackBase.QueryAsync` 开一个 `Action<DbCommand>? prepare` 口子，
Oracle 包用**反射**把它设成 `-1`（取完整值）——反射是因为方言包**不引用任何驱动类型**，
与 `SqlExceptionTranslator` 同一条纪律。

**值得记的是：源码里那条注释预言对了。** 它当时写的是
"本版没有为此在契约上开口子，所以**默认值这一格在真机上很可能是空的**。【未验证】【TODO】"。
一条诚实标注的推断，在真机到位的那一刻直接变成了一张修复工单——
这正是通篇坚持标 `【未验证】` 的价值所在。

**验过的**（13 条真机用例）：连接串装配（那份从没验过的 `DESCRIPTION=(ADDRESS=...)`）、
列类型原文带精度、可空性、默认值、主键、**虚拟列识别为生成列**、索引唯一性、外键（含 `ON DELETE CASCADE`）、
**identity 列只有一列是自增**（对照 §2.3：`IsIdentity` 在 PG/MSSQL/MySQL 上每列都返回 True）、
schema 与对象树、**视图的列**、`ROW_NUMBER` 分页窗口、会话 id、`DBMS_METADATA.GET_DDL` 建表原文、
两段式执行计划、**`EXPLAIN PLAN` 确实不执行被解释的 DML**、会话列表 id 形态与"不杀自己"对得上、
四条 DDL 生成器、以及 **19 个候选类型逐个真建一次列**。

**Oracle 上两个与别家不同的形态**（都已落进代码）：

1. **没有 `ADD COLUMN` 这种写法。** 基类的通用形态 `ALTER TABLE t ADD COLUMN c INT`
   在 Oracle 上是语法错，正确的是 `ALTER TABLE t ADD (c INT)`；
   而且 `DEFAULT` 必须排在 `NOT NULL` **之前**，反了是 ORA-00907。
2. **给不出"一条语句出计划"。** `EXPLAIN PLAN` 是 DDL、不返回结果集，
   它只把计划写进 `PLAN_TABLE`，要看还得再 `SELECT` 一次 `DBMS_XPLAN.DISPLAY`。
   所以 Oracle 的 `ExplainSql` 返回**两条语句**。顺带确认了 `analyze` 两档在 Oracle 上
   返回同一条是对的：`EXPLAIN PLAN` 从不执行被解释的语句（拿 `DELETE` 真验过，3 行一行没少）。

**`ALTER SYSTEM CANCEL SQL` 也验了，而且结论正是当初选它的理由**：
A 会话跑一条 4000 万行的 `connect by`，B 会话拿 A 的 `sid,serial#` 发 CANCEL——
A 收到 **`ORA-01013: 用户请求取消当前操作`** 提前返回，**而连接仍然可用**
（随后 `select 1 from dual` 照常）。这就是"取消查询"与"杀会话"的分界：
后者之后 A 连 ping 都发不出去。三个方言（PG 的 `pg_cancel_backend`、
MySQL 的 `KILL QUERY`、Oracle 的 `CANCEL SQL`）由此在真机上表现一致。

**仍然没验的**：`CANCEL SQL` 在 **12c / 11g** 上会不会 ORA-00933（手头没有老版本实例）、
降序索引的列名形态、`IsAutoToUpper` 那条大小写陷阱、以及 §5.4.5 "Oracle 系会打不开用户的表"。

---

### 11.5 M4 的方言资产：同一个按钮，五种方言五种含义

M4 在 `IDialectPack` 上加了八个成员（执行计划、会话列表、锁列表、类型候选、四条 DDL 生成器）。
它们**每一条都逐方言在真机上跑过**。结论很一致：**通用实现基本都是错的**。

#### 「跑一下计划」这个按钮，在五个方言里是五件不同的事

| 方言 | `analyze` 档真的执行语句吗 | 形态 |
|---|---|---|
| **PostgreSQL** | **会**。`EXPLAIN (ANALYZE) DELETE` 真把行删了（10 行 → 5 行） | 一条语句 |
| **SQL Server** | **会**。`SET STATISTICS PROFILE` 真跑（同样 10 行 → 5 行） | **三条**，且必须分三批发 |
| **MySQL 8.4** | **不会**（对 DML）。回一句 `<not executable by iterator executor>` | 一条语句 |
| **SQLite** | **不会**，而且**根本没有危险档** | 一条语句 |
| **Oracle** | **不会**。`EXPLAIN PLAN` 从不执行被解释的语句 | **两条语句** |

SQL Server 的静态档与 analyze 档**用的不是同一个开关**：静态档 `SET SHOWPLAN_ALL` 只编译不执行
（同一条 `delete` 走一遍，表里 10 行一行没少），analyze 档 `SET STATISTICS PROFILE` 真跑。
而 `SET SHOWPLAN_*` **必须是批里唯一的语句**（`Msg 1067`），所以那三条只能分三批发——
好在 `SET` 是连接级、跨批保持，三批拼起来仍是一次完整的"看计划"。

这张表是这一轮最值得记的东西：**「危险档」这个概念本身是方言相关的**。
PG 上它是一个真会删数据的开关（实测 10 行删到 5 行），
MySQL 8.4 上对 DML 它是空的，SQLite / Oracle 上它压根不存在。
`SqlGuard` 判红档就不放行 `analyze`——**这条护栏只有 PG 真正需要，但五个方言必须表现一致**，
否则同一个按钮在不同连接上后果不同，那是最糟的一种界面。

**SQL Server 的计划是三条语句，而它的失败形态会污染整条连接。**
脚本是 `SET SHOWPLAN_ALL ON` / 用户语句 / `SET ... OFF`。中间那条失败时（表名打错，Msg 208），
执行器一条失败即停，**第三条 `OFF` 就发不出去**——而 `SET` 是**连接级**的，
于是这条连接从此只出计划不出数据：再发一条完全正常的 `select`，
拿回来的是 `StmtText` / `EstimateRows` 那几列。

修在调用方，规则**方言无关**：脚本没跑完 ⇒ 尾巴上可能还有必须发出去的收尾语句，尽力补发。
**关键是"没跑完"这个进度必须是已知的**——执行器把每条的结果（含失败）都返回，
`results.Count` 就是停在第几条，补发 `[results.Count, plan.Count)` 正好只发到那条 `OFF`。
连接级失败（连接断了、被取消）那条路径上**刻意不补发**：那时进度无从得知，
从头补发会把**用户那条语句真的执行一遍**，而这正是"只看计划"必须避免的事。

顺带三条边界：`EXPLAIN` 只认可优化的语句——
PG 上 `EXPLAIN create table` / `EXPLAIN vacuum` 都是 42601，
MySQL 上 `EXPLAIN show tables` / `EXPLAIN create table` 都是 1064，而且**报错位置指在用户看不见的 SQL 上**
（PG 的前缀是 22 字符、加 `ANALYZE` 是 40 字符，错误定位要减掉它）。

#### 会话列表：「谁在跑」这一页，八成的行不是人

- **PG 18.1 全空闲时 `pg_stat_activity` 有 9 行，其中 8 行是后台进程**
  （autovacuum launcher、3 个 io worker、walwriter、checkpointer、logical replication launcher、
  background writer）。不过滤就是一页噪音。按 `datname IS NOT NULL` 滤而**不是**按
  `backend_type='client backend'`：后者会把并行 worker 和 autovacuum worker 一起滤掉，
  而且 `backend_type` 的取值随版本增删（`io worker` 是 18 才有的）。
- **PG 的「已跑多久」必须用 `clock_timestamp()`，不能用 `now()`。**
  `now()` 是 `transaction_timestamp()`，停在 `BEGIN` 那一刻——
  实测同一条会话里 `now() - query_start` 算出 **−2.02 秒**，`clock_timestamp() - query_start` 才是 0.0097。
  一个恒为负的「已跑多久」比没有更糟。
- **非超级用户看到的 `pg_stat_activity` 是「打了码」的**：别人的行里
  `client_addr` / `client_port` / `backend_type` / `state` / `query_start` 全是 NULL，
  `query` 显示 `<insufficient privilege>`。所以 host 那一格不能直接 `COALESCE(..., 'local')` 收尾——
  那会把一台远程机器上的会话显示成本地连接。`'local'` 只在 `client_port = -1`（Unix 域套接字）时才成立。
- **MySQL 8.4 的 `information_schema.PROCESSLIST` 已废弃**（Warning 1287），
  但仍然选它而不是 `performance_schema.processlist`：后者在 perf schema 关掉时是**空表**而不是报错——
  一张空表与「现在没人连」长得一模一样，正是 §7.8 要避免的那种沉默。
  另外 MySQL 的 `state` 要写成 `COALESCE(NULLIF(STATE,''), COMMAND)`：
  空闲连接的 `STATE` 是空串、`COMMAND` 才是 `Sleep`。
- **MySQL 的锁视图与会话列表用的是两套 id**：`data_lock_waits` 只有 `THREAD_ID`，
  会话列表用的是 `PROCESSLIST_ID`，**不 join `performance_schema.threads` 就对不上**——
  对不上的后果是「杀会话」杀错人。
- **Oracle 只列 `TYPE = 'USER'`**：后台进程（`PMON`、`SMON`、一大票 `Wnnn`）数量远超真人会话。
  锁用 `V$SESSION.BLOCKING_SESSION` 而不是手工 join `V$LOCK`——后者只覆盖 TX/TM 队列锁。

#### 四条 DDL 生成器：通用写法在三个方言上是语法错

| 方言 | 通用写法出的问题 |
|---|---|
| **Oracle** | **没有 `ADD COLUMN` 这种写法**，要 `ALTER TABLE t ADD (c INT)`；且 `DEFAULT` 必须在 `NOT NULL` **之前**，反了是 ORA-00907 |
| **MySQL** | `DROP INDEX \`ix\`` 是 1064——**MySQL 的 `DROP INDEX` 必须带 `ON <表>`** |
| **PostgreSQL** | `DROP INDEX "ix"` 在别的 schema 里是 42704，**必须 schema 限定**；但 `CREATE INDEX "app"."ix" ON ...` 又是 42601 语法错——**同一个索引名，建的时候不能限定、删的时候必须限定** |
| **SQL Server** | `ADD COLUMN` 是 `Msg 156`（T-SQL 是 `ALTER TABLE t ADD c INT`）；`DROP INDEX [ix]` 是 `Msg 159`，必须写成 `DROP INDEX ix ON t` |

五个方言里**只有 SQLite 能用基类那份通用 DDL 原样跑通**。

PG 那一条尤其阴险：不限定的 `DROP INDEX` 在**只有一个 schema 有同名索引时照样成功**，
只在多 schema 同名时才按 `search_path` 悄悄删掉另一个。

**还有一类是 DDL 生成器写对了也没用的**：主键与唯一**约束**背后的索引
`DROP INDEX` 删不掉（SQL Server `Msg 3723`、PG `2BP01`），要改走 `DROP CONSTRAINT`。
结构页照实把它们列出来（那是对的——它们确实存在），但设计器**在按钮那一层就拦下**并说明理由，
而不是让用户点下去等服务端报错。这里**刻意不偷偷改写成 `DROP CONSTRAINT`**：
用户点的是"删索引"，而删约束是另一件事。

还有一类是「**表达不了就返回 `null`**」：
基类的通用 `ADD COLUMN` 只写得出 名字+类型+NOT NULL+DEFAULT，
`IsGenerated` / `IsPrimaryKey` / `IsAutoIncrement` / `Comment` 会被**一声不吭地丢掉**，
而引擎会照办、出一个普通列且不报错——那是「成功了但不是你要的那一列」。
所以四份包一致地在这几种输入上返回 `null`，由界面明说「本方言表达不了，请去查询页手写」。
（MySQL 上还多一条：注释拼进去会变成十六进制字面量 `X'E4B8AD...'`，直接 1064。）

#### 类型候选也是方言资产

`CommonTypes` 每个方言一份，而且**每一条都在真机上真建过一次列**。
两条相反的实测：**MySQL 会悄悄改写**（`DECIMAL`→`DECIMAL(10,0)`、`CHAR`→`CHAR(1)`、
`BOOLEAN`→`TINYINT(1)`、`INT(11)` 的显示宽度被丢掉并给 Warning 1681），
**PG 会规范化成 `format_type` 的写法**（`varchar(50)`→`character varying(50)`、
`serial`→`integer` 外加一个 `nextval(...)` 默认值）。
两边都意味着：**结构页显示的类型不等于用户刚才选的那个词**，而这是对的——显示的必须是库里真实的样子。

---

### 11.6 真机 GUI 那一轮：六个只有"人点一下"才暴露的缺陷（2026-08-20）

前面所有验证都是**程序驱动**的：单元用例、真机集成用例、headless 渲染、宿主整链路。
它们加起来 2381 项全绿。然后把应用真的打开、由人点了二十分钟，**逮到六个缺陷,其中两个是高危**。

这一节值得单独写,因为它说明的是**验证方式本身的盲区**,而不是某一个 bug。

#### ① 元数据连接被并发使用(高危)

对象树上直接挂出一行 `A command is already in progress: SELECT a.attnum, a.attname::text, …`。

根因不在方言包,在**一条连接上同时跑了两条命令** —— Npgsql 与 MySqlConnector 都不允许
(SQL Server 要显式开 MARS)。而对象树**天然会并发**:展开是即发即忘的
(`IsExpanded` 的 setter 里 `_ = LoadAsync(...)`),用户飞快点开两个节点就够了。

**为什么之前所有用例都没抓到**:它们全是**顺序**调用方言包的。
"并发"这件事只存在于 UI 的事件驱动路径上,而那条路径没有任何用例走过。

修法是在 `SqlConnection` 上加一道闸(`UseAsync`),元数据查询一律排队。
回归用例真机复现了同一条异常原文,并且验过"去掉闸门就变红"。

#### ② 双击"库"或"schema"节点,被当成表打开(高危)

PG 上双击库 `ops_pg` → `SELECT * FROM "ops_pg"` → `42P01: relation "ops_pg" does not exist`;
Oracle 上双击 schema `SYSBACKUP` → `ORA-00942: 表或视图不存在`。

`OpenStructure` 当时已经挡了 `Category` 与 `Column`,却漏了 `Database` 与 `Schema`,
而"打开数据"那一侧的防护更弱。**一条"挡掉不适用的种类"的判断,漏掉一半等于没有。**

#### ③ 分类节点上右键,照样弹出"打开数据 / 查看结构"

点了静默无效。这正是本文反复反对的那种"摆一个不起作用的控件" ——
而它出现在我自己刚写的菜单里。

#### ④ PostgreSQL 上系统表**整个查不了**(高危)

`select * from pg_class limit 20` → `42883: no binary output function available for type aclitem`。

`pg_class.relacl` 是 `aclitem[]`,Npgsql 默认按二进制取,而这个类型在服务端**没有二进制输出函数**。
失败发生在**服务端返回数据之前**,所以 §5 那套"单元格级容错"完全接不住 ——
它守的是"读某一格失败",而这里是"整个结果集根本发不出来"。

**容错要分层**:一层守不住的东西,不会因为它写得足够小心就自动被守住。

#### ⑤ SQLite 连接被强制要求填用户名口令(高危)

五个方言收成一个页签之后,SQLite 那一档声明了 `NoCredentials`。
"新选一次 SQLite"看着完全正确 —— 用户名口令两栏确实收起来了。
但**打开一条已存的 SQLite 配置**就露馅:两栏冒出来,还被当成必填,
连接时还会弹一个填了也没用的登录框。

两处根因,而且是同一种形状 —— **变体这条新语义没有被所有读它的地方读到**:

| 位置 | 漏在哪 |
|---|---|
| `ConnectionProfileViewModel` | 变体只在**字段值变化**时套用,**装载已存配置时没套** |
| `MainWindowViewModel` 打开工作台文档那一路 | 只查**基础描述符**的能力位,没查变体 |

第二处尤其能说明问题:连接框和连接逻辑**各自判断了一次"要不要凭据",而判据不一致**。
用户看到的就是"明明没让我填,连的时候却非要我填"。

**教训:给一个共享契约加新语义时,要把"谁在读它"全找出来。**
加一个可选属性很容易,难的是它默认不生效的那些地方 —— 那里不会报错,只会行为不对。

#### ⑥ SQLite 的连接框仍摆着"端口"

主机标签改对了、凭据收起了,**端口那一栏还在**,还显示着上一个方言留下的值。
SQLite 是个文件,这一栏填什么都不会被用到。变体当时能表达"没有凭据",不能表达"没有端点"。

---

**这一轮真正的结论不是这六个 bug,而是:程序驱动的验证有一类系统性盲区。**
它验的是"我调用它时它对不对",而不是"人这样用它时它对不对"。
并发(①)、种类分派(②③)、跨层语义一致(⑤)这三类,恰恰只在真实交互路径上成立。

顺带一条方法教训:这一轮**先花了一小时试图用 `SendInput` 脚本驱动 Avalonia 窗口**,
踩了三个坑(绝对坐标归一化必须按 `size-1` 且四舍五入、`MOUSEEVENTF_MOVE` 必须与按下抬起同批提交、
下拉弹层 `PrintWindow` 抓不到),最后是**请人手工点、把截图交回来**才真正开始出结论。
`docs` 之外的记忆里早写过"能用 headless 验的就别开真机" ——
但反过来也成立:**headless 验不了的,脚本驱动多半也验不了,不如直接请人点。**

---

### 11.7 一个比那六个 bug 更该记的发现:**20 条 headless 用例的断言从来没生效过**

修完 §11.6 那批缺陷之后,顺手核了一件小事,结果炸出这一轮最不该发生的问题。

`HeadlessUnitTestSession` 只有两族重载:

```csharp
Task  Dispatch(Action action, CancellationToken ct);
Task<T> Dispatch<T>(Func<Task<T>> action, CancellationToken ct);
```

**没有 `Func<Task>` 那一支。** 于是这样写:

```csharp
public Task 面板能装载() => _session.Dispatch(async () =>
{
    Assert.AreEqual(3, grid.Columns.Count);    // ← 这条断言永远不会让用例变红
}, CancellationToken.None);
```

不返回值的 async lambda 被绑到 **`Action`** 上、变成 **async void**:
断言异常落在调度线程上没人接,而 `Dispatch` 返回的 `Task` **在 lambda 第一个 `await` 处就已经完成了**。
**编译通过、测试恒绿。**

判定方法极简单,而且应该早就做:**把 `Assert.Fail` 放进用例第一行,看它红不红。**
实测结果是 `dotnet test` 照样报 `5/5 通过`。

#### 波及面

全仓库 30 处这种调用,**20 处是哑的**,横跨 4 个测试项目:

| 位置 | 哑掉的条数 |
|---|---|
| 数据库插件 `SqlPanelUiTests` / `SqlPanelScreenshotTests` | 8(**本文档前面引用过它们的"通过"**) |
| 宿主 `PluginPanelUiTests` / `LocalFilePaneViewUiTests` / `PluginThemeTokensTests` | 8 |
| 宿主 `StandaloneSftpDocumentBehaviorTests` | 4 |

Redis 与 AI 插件的那 10 处**恰好**在 lambda 末尾有 `return`,于是绑对了重载 —— 它们是有效的,
但那是运气,不是设计。

#### 激活之后才看见的第二层

给 lambda 补上返回值让它们真的跑起来,数据库插件那 8 条**全绿**(它们本来就是对的,只是没在守着)。
而宿主的 `StandaloneSftpDocumentBehaviorTests` **6 条直接挂死超时**:

```csharp
var invocation = Task.Run(() => (Task)closeMethod.Invoke(vm, [document])!);
await closeStarted.Task;
await invocation;          // CloseSftpDocumentAsync 内部要回 UI 线程续跑
```

它在**占着 UI 线程**的同时 `await` 一个**需要 UI 线程才能完成**的任务 —— 一个标准的死锁。
即发即忘的旧绑定下,第一个 `await` 就让出了控制权、用例立刻报通过,所以这个死锁**从来没暴露过**。

修它要重构那几条用例的等待方式,是宿主侧的活;**这一轮刻意没有顺手带过** ——
改错了会掩盖真实的线程行为,而那正是它们要验的东西。
于是记成一条**棘轮**:`HeadlessDispatchGuardTests` 扫源码,名单里的可以先欠着,
**名单之外一处都不许新增**;欠账清掉之后连名单也必须删掉,否则它会变成一张没人看的白名单。

#### 教训

**"测试通过"是一个需要被验证的断言,不是一个可以直接相信的事实。**

这一轮前面所有的"全绿"都写进了文档(§11.3 的 216 项、§11.5 的 2381 项),
其中有 8 项是假的。它们**没有**掩盖真 bug(补上返回值之后全绿),但那是运气 ——
真正的问题是**当时没有任何机制能告诉我它们是假的**。

一个可迁移的做法:**任何新引入的测试脚手架,第一件事是往里塞一句必然失败的断言,确认它真的会红。**
这件事的成本是三十秒,而它这次省下的是"以为验过、其实没验"的八条用例。

---

## 十二、明确不做的边界

| 不做 | 理由 |
|---|---|
| **MongoDB（永不进本插件）** | 它不是关系库。要做就**另开一个工作台插件** |
| **ClickHouse / TDengine（v1 不做，排 M6）** | 能进本插件，但**必须先改界面**。驱动接上就宣布支持是假支持 |
| **ER 图** | 需要外键元数据 + 自动布局引擎。价值高但不是 v1 |
| **结构/数据比较同步** | 商业工具的付费点，工作量单独一个 M5 |
| **数据库设计（正向工程）** | 那是建模工具的活 |
| **BI 式图表** | 结果网格出数就够了 |
| **Excel 导入导出** | 需要 EPPlus/NPOI（额外依赖 + 许可要看清）。CSV/JSON 覆盖 90% 场景 |
| **`Odbc` / `Custom` 方言** | 前者元数据面太不确定，后者需要用户自带程序集 |
| **替用户改写他手敲的 SQL** | 一个管理工具替用户改写他敲的 SQL，是背叛。谓词层只负责"我们替他生成的那一半" |

---

## 十三、待决事项

本轮把初版的 5 条待决里的 4 条推到了可拍板，同时新增了 3 条。

### 已定（有真机依据）

| # | 初版问题 | 结论 |
|---|---|---|
| **2** | Oracle 等大驱动是否拆成可选包？ | **v1 不拆，全捆绑 22.19 MB**。删驱动本身安全（矩阵 diff=0），但失败是惰性的——"方言选得上、点连接才炸"体验太差；4 个驱动只占 7.24 MB。要拆必须先做 `File.Exists` 预检（§9.4） |
| **3** | `Npgsql` 是否升版覆盖？ | **升到 10.0.3**（保守可选 9.0.4）。10 个版本 × 40 项冒烟全绿，包体 +0.51 MB、零新增文件。代价是网格要认 `DateOnly`/`TimeOnly`/`IPNetwork`；收益是修掉 `interval` 静默折算、拿到证书校验档位、与服务端同代。**前提**：把"第一个 PG client 之前不碰 Npgsql"写成硬纪律 + 回归测试（§3.8） |
| **4** | ALC 泄漏：宿主是否加 pending-delete？ | **必须加，但方案改写**：不需要清单文件，卸载时 `Directory.Move` 进 `.trash` 隔离区（1 ms，必成功）、启动时清扫。宿主的 `UpdateApplier` 是现成范本（§4.3） |
| **5** | 真库测试环境 | **已解决**：本机有 podman（MySQL 8.4）+ 本机 PG 18.1 二进制（临时集群）+ 新建 LocalDB 实例（SQL Server 2025）。建议把起库脚本收编进 `tests/`（§十） |

### 待拍板（新增与保留）

| # | 问题 | 现状 |
|---|---|---|
| **1** | **结果网格：自研，还是宿主引入 `Avalonia.Controls.DataGrid`？** | 仍是最大的一笔工作量。A 路线版本对得上（12.1.1 严丝合缝），B 路线的"2–3k 行"估算**偏乐观**（仓库零可复用零件、`ItemsRepeater` 已被删）。**拍板前必须联网验证 DataGrid 做不做列虚拟化**（§4.4） |
| **6** | **口子零：驱动程序集是否划给宿主默认 ALC？** | 这是 ALC 泄漏唯一干净的解药（实测第 2/3 轮回收），但**与待决 3 直接冲突**：驱动归宿主 = 版本由宿主定 = 插件不能自带 Npgsql 10.0.3。倾向"口子零 + 宿主直接引 Npgsql 10.0.3"，两个收益一起拿（§4.2） |
| **7** | **SqlClient 的第三个钉子未定位** | PG/SQLite 摘掉 AppDomain 钩子 + 池定时器就能回收，SQL Server 全部手段无效。若不走口子零而要彻底解决，需要 ClrMD 级别的 GC 根路径分析。**另注**：本轮 SQL Server 只有 LocalDB（命名管道），第三个钉子有可能来自 `LocalDBAPI` 的静态缓存——**结论要推广到远程 SQL Server 需要一台真 TCP 实例复测**（§3.9） |
| **8** | **MySQL 的 `lower_case_table_names` 差异怎么在界面上表达？** | lctn=0（Linux）与 lctn=1（Windows）下同一个插件行为不同，而 SqlSugar 的元数据缓存还会串表。是连上后探测并在界面标注，还是干脆在 lctn=0 时禁用某些元数据路径？（§3.7） |
| **9** | **`InstanceFactory` 的静态污染：自己兜底，还是给 SqlSugar 提 issue？** | 已有一行解药（每次建 client 前复位 `CustomDllName`）且已验证。但这是给上游打补丁的性质——碰一次未捆绑方言就拖垮整个 ALC 是 SqlSugar 的 bug。建议**两条都做**：插件自己兜底（不能等上游），同时提 issue（§3.3） |
| **10** | **四个 provider 包在 nuget.org 查无此包** | `SqlSugar.Db2Core` / `SqlSugar.GaussDBCore` / `SqlSugar.HANAConnector` / `SqlSugar.TDSQLForOracleODBC`——私有源？改名？下架？**把 DB2/HANA/GaussDBNative/TDSQLForOracleODBC 排进 M5 之前必须先确认**（§6.2） |

---

## 附录 A：真机环境与复现

### A.1 四台真机怎么起的

```powershell
# PostgreSQL 18.1 —— 用本机二进制起一个独立临时集群，不动用户已装的 postgresql16 服务
$pgbin = "D:\Program Files\pgsql\bin"
& "$pgbin\initdb.exe" -D <scratch>\pgdata -U postgres --pwfile=<pwfile> -E UTF8 --locale=C -A scram-sha-256
& "$pgbin\pg_ctl.exe" -D <scratch>\pgdata -o "-p 55432 -c listen_addresses=127.0.0.1" -l <scratch>\pg.log start
# 连接串: Host=127.0.0.1;Port=55432;Username=postgres;Password=***;Database=<db>

# SQL Server 2025 —— 新建一个 LocalDB 实例，绕开默认实例上的登录触发器
sqllocaldb create VelaSpike
sqllocaldb start  VelaSpike
# 连接串: Server=(localdb)\VelaSpike;Integrated Security=true;Database=<db>;TrustServerCertificate=true

# MySQL 8.4.11 —— podman（本机 Docker Hub 不通，走国内镜像源）
podman machine start
podman pull docker.m.daocloud.io/library/mysql:8.4
podman run -d --name velaspike-mysql -p 127.0.0.1:13306:3306 `
  -e MYSQL_ROOT_PASSWORD=*** -e MYSQL_ROOT_HOST=% `
  docker.m.daocloud.io/library/mysql:8.4 `
  --character-set-server=utf8mb4 --collation-server=utf8mb4_0900_ai_ci --local-infile=1
# 连接串: Server=127.0.0.1;Port=13306;Database=<db>;Uid=root;Pwd=***;CharSet=utf8mb4
```

**podman 上踩到的两个环境坑**（下一个人会再踩，记下来）：

- VM 里 `/etc/resolv.conf` 指向 WSL 的 NAT 网关，DNS 被污染（`registry-1.docker.io` 解析到一个假 IPv6）。
  换成公共 DNS 即可（WSL 重启会自动还原）。
- VM 里配着一个失效的代理 `socks5://host.containers.internal:10808`——主机名不解析、端口也没监听，
  导致所有 `curl` 返回 000。绕开它直连国内镜像源即可（podman 服务本身不读 profile.d 的代理变量）。

### A.2 探针结构

两个项目：**Probe**（`EnableDynamicLoading=true` + `SqlSugarCore 5.1.4.217`，与真插件同构）与
**Runner**（复刻宿主 `PluginAssemblyLoadContext`，不引用 SqlSugar，跑完 `Unload()` + 40 轮 GC 可回收性检查）。

```powershell
dotnet build .\Probe\Probe.csproj  -c Release
dotnet build .\Runner\Runner.csproj -c Release
$p = ".\Probe\bin\Release\net11.0\Probe.dll"; $r = ".\Runner\bin\Release\net11.0\Runner.dll"

dotnet $r $p Smoke  MySql "Server=127.0.0.1;Port=13306;Database=spike_my;Uid=root;Pwd=***"
dotnet $r $p Matrix                 # 35 方言 × 23 模板离线矩阵
dotnet $r $p Entry  "-" "<mssql 连接串>"
```

Runner 是通用派发器：`Runner.dll <probe.dll> <用例类名> [参数...]`，
反射找 `Probe.<用例类名>` 上的 `public static string Run(...)`。新增用例只改 Probe，不必动 Runner。

---

## 附录 B：本轮没有覆盖的（诚实清单）

**引用本文任何数字之前，先读这一节。**

### B.1 完全没有真机的

- ~~**Oracle**~~ **→ 已补上真机，见 §11.4。** 留下当时的记录是因为它给出的教训比结论有用：
  本轮先尝试 `container-registry.oracle.com/database/free`，实测下载速率 **5 MB/min**、
  剩余约 2 GB 需要 6.7 小时，判定"拿不到 Oracle 真机"并据此写了一整份离线推断的方言包。
  **那个判定是错的——错在只试了一个源。** 后来换 `gvenzl/oracle-free:23-slim`
  （Testcontainers 生态的标准测试镜像，1.99 GB）就装上了，**总耗时约 20 分钟**。
  教训：把"某个源慢"记成"这个东西拿不到"，代价是一整份没验过的代码。
  仍然没验的那几条列在 §11.4 末尾。
- **达梦 / 人大金仓 / 神通 / 瀚高等国产库**：一个都没测。
- **T1 的十种同族方言**（TiDB / OceanBase / PolarDB / Doris / GoldenDB / TDSQL /
  openGauss / Vastbase / GaussDB）：一个都没测，"复用 MySQL/PG 包"仍是推断（§6.2）。
- **MariaDB**：没测。

### B.2 测了但结论有边界的

- **性能数字全部来自本机回环，没有真实网络延迟。** 一旦经 SSH 隧道或跨机房，
  所有"拉多少字节"类的耗时（Dispose 排水 6.5 s、200×1MB 文本 1.5 s）都会成倍放大，TTFB 还要加一个 RTT。
  测试机：i5-12400（6C/12T）/ 32 GB，测时空闲内存偏低，ms 数量级可信、个位数不必当真。
- **"100 列 × 100 万行 ≈ 3 GB"是线性外推**，不是真跑（本机内存不够）。
  而且复核者用另一张全 `text` 列的表量到近两倍——**正确写法是 3~6 GB，取决于列的 CLR 类型**。
- **SQL Server 只有 LocalDB**（共享内存/命名管道）。网络层错误号在真 TCP 实例上会不同
  （判据表按 `Class == 20` 归类正是为了不依赖具体号）；ALC 的"第三个钉子"也可能是 LocalDB 特有的。
- **SQL Server 只测了 `SQL_Latin1_General_CP1_CI_AS`**。区分大小写或二进制排序规则下，
  "按列名去重"与 `IsAnyColumn` 的行为可能不同。
- **MySQL 只测到 `lower_case_table_names=0`**（Linux 容器）。lctn=1（Windows 默认）与 lctn=2（macOS）
  **未实测**——文中凡是写 Windows MySQL 行为的地方都是推论。
- **`ProtocolCertificateTrustException` 的判据没测出来**：PG 侧捆绑的 Npgsql 5 根本没有证书校验档位；
  MSSQL 侧 LocalDB 走命名管道，`Encrypt=true;TrustServerCertificate=false` 直接连上不报错。
  要拿真证据得给 PG 开 `ssl=on` + 自签证书。**这条在文档里继续标"未实测"。**
- **SSH 隧道场景一次都没测。** 而这恰恰是线上库的主要连法，且 MySQL 的取消需要第二条转发通道（§3.10）。
- **多 ALC 交叉场景没测**：两个插件各带一份 Npgsql 时，`AppContext` 开关的先后手会怎样（§3.8）。
- **`AS()` 注入在 SQL Server 上是已执行确证的**（真删了 200 行 + drop 了陪葬表），
  在 PG 与 MySQL 上"同样不转义，但我没构造出可用的利用链"——按"转义有破口 + 未验证可利用"读，
  不要升格成"存在注入漏洞"。
- **PG 上的分页重复/丢行只在 PG 复现**；SQL Server 2025 在同一组用例上全 0/0——
  **未复现不等于安全**（`ROW_NUMBER` 遇并列同样无顺序保证）。

### B.3 想测但没测成的

- **`Avalonia.Controls.DataGrid` 的包内容**：版本索引拿到了（12.1.1/12.1.2 存在），
  但 nupkg 取不下来（nuget.org 被本机代理 302 到镜像，取包体连续 90 s 超时）。
  **它是否真做列虚拟化没验证**——而 §4.4 的 A/B 抉择恰恰取决于这一条。
- **`ExcludeAssets="all"` 与手删 dll 产出一致**这一条只做了手删（§9.4）。
- **仿宿主的 MSBuild 管线实验**（`VelaPluginRid` 穿过 `RemoveProperties`）没在真仓库验证（§9.5）。
- **QuestDB 到底要装什么包**：provider 类全家就在 `SqlSugar.dll` 里，却被 `CheckDbDependency` 拦下，
  IL 里与它相关的外部包只有 BulkCopy 用的 `SqlSugar.QuestDb.RestAPI`。真正的 provider 包名**未知**。
- **`InstanceFactory` 静态污染在并发建连下的表现**没测（本轮是单线程时间线）。
  另：本轮只复位 `CustomDllName` 就够，但若插件将来真去加载第三方 provider 包，
  `CustomDlls` 这个 `List` 的语义会变，需要重测。
- **SQLite `GetIndexList` 返回 `"0"` 的根因**（IL 显示走 `PRAGMA index_list`，疑似读了第一列 `seq` 而非 `name`）
  只是 IL 层推断，**未在真机二分验证**。
- **神通与达梦的两处可疑但无实例可验**：Oscar 的 `GetDataBaseSql` 与人大金仓逐字节相同
  （`SELECT datname FROM sys_database`）；达梦的 `CreateTableIdentity` 与 SQL Server 相同（`IDENTITY(1,1)`）。
  两条都未计入 §3.3 的扣分。
