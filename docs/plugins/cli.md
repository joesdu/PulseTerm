# `vela-plugin` 命令行手册

> 适用版本:VelaShell 插件 SDK **1.4.0**(`vela-plugin --version` 看本机装的是哪版)。
> 相关文档:[开发指南](dev-guide.md) · [打包与发布](publishing.md) · [SDK 参考](sdk-reference.md)

`vela-plugin` 是插件作者的命令行工具。它与宿主共用同一份实现(`VelaShell.PluginSdk`
里的清单解析、`.vpx` 容器读写、签名校验),所以**不存在"工具认、宿主不认"的缝**。

```bash
dotnet tool install -g VelaShell.Plugin.Cli     # 安装
dotnet tool update  -g VelaShell.Plugin.Cli     # 升级
vela-plugin --version
```

> **打包不需要装这个工具**:打包器随 `VelaShell.PluginSdk.Build` 一起分发,
> `dotnet build -t:PackVpx` 直接可用。装全局工具是为了开发内环(`dev init`)、
> 体检(`doctor`)、签名与包检查。

---

## 0. 一分钟工作流

```bash
dotnet new install VelaShell.Plugin.Templates      # 一次性
dotnet new velaplugin -n Snippets --publisher acme
cd Snippets

dotnet build
vela-plugin dev init      # 配好 IDE 启动配置 → 按 F5 即可断点调试
vela-plugin doctor        # 有问题先问它

dotnet build -c Release -t:PackVpx                 # → bin/vpx/acme.snippets-0.1.0.vpx
vela-plugin sign bin/vpx/acme.snippets-0.1.0.vpx -k ~/keys/acme.pem
vela-plugin verify bin/vpx/acme.snippets-0.1.0.vpx
```

---

## 1. 命令总览

| 命令 | 作用 |
| --- | --- |
| [`dev init`](#dev-init) | 生成 IDE 启动配置:以调试器启动本机装的 VelaShell 并挂载本工程 |
| [`dev run`](#dev-run) | 不开 IDE,直接用同样的参数拉起宿主 |
| [`dev list` / `dev prune`](#dev-list--dev-prune) | 查看 / 清理全局登记的开发根 |
| [`dev link` / `dev unlink`](#dev-link--dev-unlink) | 把一个输出目录常挂进宿主(旧名 `dev-link` / `dev-unlink`) |
| [`hosts`](#hosts) | 列出本机登记过的 VelaShell 安装 |
| [`doctor`](#doctor) | 体检:宿主、清单、构建产物、启动配置 |
| [`validate`](#validate) | 校验 `plugin.json` 与入口程序集 |
| [`pack`](#pack) | 把产物目录打成 `.vpx` |
| [`sign`](#sign) / [`verify`](#verify) | 给包签名 / 验签 |
| [`keygen`](#keygen) | 生成 P-256 签名密钥对 |
| [`info`](#info) / [`unpack`](#unpack) | 查看包头与清单 / 解包(排障) |
| `install` | **禁用**:安装必须走宿主,否则绕过发布者授权与受保护安装收据 |

全局约定:

- 退出码 `0` 成功,`1` 失败(可读错误打在 stderr,前缀 `error:` / `warning:`)。
- 路径参数都接受相对路径,输出里一律打成绝对路径。
- 所有命令都不需要管理员权限;除 `dev init` 写工程内的
  `Properties/launchSettings.json` 外,只写 `~/.velashell` 与你显式指定的输出路径。

---

## 2. 开发内环

### `dev init`

```bash
vela-plugin dev init [projectDir] [选项]
```

在工程里写出(或并入)`Properties/launchSettings.json` 的一个启动配置,内容是
**以调试器附着的方式启动本机已安装的 VelaShell**,并把本工程的构建输出挂进去。

它从 `~/.velashell/host.json` 找到宿主 —— 这份文件由 VelaShell **每次启动时自己写**
(路径、版本、apiLevel、内置 SDK 版本、Avalonia 版本、数据根)。所以:

> **前置条件:本机至少完整启动过一次 VelaShell。** 没有的话用 `--exe` 直指可执行文件。

生成的配置形如:

```jsonc
{
  "profiles": {
    "VelaShell": {
      "commandName": "Executable",
      "executablePath": "C:\\Users\\joe\\AppData\\Local\\Programs\\VelaShell\\VelaShell.exe",
      "commandLineArgs": "--dev-root C:\\work\\Snippets\\bin\\Debug --wait-debugger acme.snippets --data-root C:\\Users\\joe\\.velashell-dev",
      "workingDirectory": "C:\\Users\\joe\\AppData\\Local\\Programs\\VelaShell"
    }
  }
}
```

三个参数各自解决一件事:

| 参数 | 解决的问题 |
| --- | --- |
| `--dev-root <目录>` | 把工程输出挂进宿主(挂的是**父目录**,宿主扫它的一级子目录)。跟着启动配置走,不写机器级全局状态 |
| `--wait-debugger <id>` | 隔离插件的子进程在装载程序集**之前**挂起等你附加(`inProcess` 插件不需要:F5 起来就已经附着了) |
| `--data-root <目录>` | 调试实例用独立数据根,于是你日常那份 VelaShell 可以一直开着 —— 共用数据根会撞上单实例保护,第二个实例直接退出 |

选项:

| 选项 | 默认 | 说明 |
| --- | --- | --- |
| `--host <版本或路径>` | 最近启动过的那份 | 从已登记的多份安装里挑一份(正式版 + 预览版并存时用它) |
| `--exe <路径>` | — | 直指某个可执行文件,不查注册表(便携版、CI、还没启动过的构建) |
| `-o, --output <目录>` | `bin/` 下最新的那个含 `plugin.json` 的目录 | 插件构建产物目录 |
| `--data-root <目录>` | `~/.velashell-dev` | 调试实例的数据根 |
| `--shared-data` | 关 | 改用日常数据根(想在真实配置里试插件时才用;**此时必须先退出正在运行的 VelaShell**) |
| `--no-wait-debugger` | 关 | 不加 `--wait-debugger`(隔离插件不想每次都停在等待附加) |
| `--watch` | 关 | 加 `--dev-watch`:宿主监视开发根,重编后自动重载插件 |
| `--profile <名字>` | `VelaShell` | 启动配置名(想同时留几套配置时用) |
| `--link` | 关 | 顺便把开发根登记进 `plugins.dev.txt`(不开 IDE 时也想挂着) |

输出示例:

```text
Host       1.4.2 (C:\Users\joe\AppData\Local\Programs\VelaShell\VelaShell.exe)
Plugin     acme.snippets v0.1.0 [InProcess]
Dev root   C:\work\Snippets\bin\Debug
Data root  C:\Users\joe\.velashell-dev
Profile    VelaShell -> C:\work\Snippets\Properties\launchSettings.json

Press F5 in your IDE (profile above), or run `vela-plugin dev run`.
```

### `dev run`

```bash
vela-plugin dev run [projectDir] [--wait] [--wait-debugger] [--watch]
                    [--data-root <目录>] [--shared-data] [--host <…>] [--exe <…>]
```

不开 IDE 时用同一套参数把宿主拉起来,打印 pid 后返回;`--wait` 则等它退出并透传退出码
(CI 里跑冒烟脚本时用)。注意:这条路**没有调试器**;要断点还是走 `dev init` + F5。

### `dev list` / `dev prune`

```bash
vela-plugin dev list     # 列出 plugins.dev.txt 里的开发根,并标出它是否还有效
vela-plugin dev prune    # 删掉其中已不存在的目录
```

```text
  [ok                    ] C:\work\Snippets\bin\Debug
  [missing               ] D:\old\Removed\bin\Debug
  [no plugin sub-directory] C:\tmp\empty
  3 root(s) in C:\Users\joe\.velashell\plugins.dev.txt
```

### `dev link` / `dev unlink`

```bash
vela-plugin dev link   bin/Debug/net11.0     # 旧名 dev-link,仍可用
vela-plugin dev unlink bin/Debug/net11.0
```

把一个目录写进 `~/.velashell/plugins.dev.txt`,**对所有 VelaShell 实例长期生效**。
传插件目录时会自动上移一级(宿主扫的是根目录下的一级子目录)。

什么时候用哪个:

- **`dev init`(推荐)**:挂载信息跟着工程走。同时开两个插件工程、或在分支间切换都互不干扰。
- **`dev link`**:你想让日常那份 VelaShell 一直带着这个插件跑(比如自用工具),而不是每次 F5。

---

## 3. 环境体检

### `hosts`

```bash
vela-plugin hosts [--all]
```

列出本机登记过的 VelaShell 安装(按最近启动倒序;`--all` 连已被删掉的也列出来)。

```text
1.4.2            api 1  sdk 1.4.0   avalonia 12.1.1
  exe        C:\Users\joe\AppData\Local\Programs\VelaShell\VelaShell.exe
  data root  C:\Users\joe\.velashell
  last seen  2026-08-21 17:51
```

注册表最多保留 8 份安装,可执行文件已消失的条目会在下一次登记时被自动剔除。

### `doctor`

```bash
vela-plugin doctor [projectDir] [--host <…>] [--exe <…>]
```

一次性检查开发环境与工程:

| 检查项 | 不通过时的意思 |
| --- | --- |
| 宿主是否已登记 | 没启动过 VelaShell,或用的是便携版 → 用 `--exe` |
| `apiLevel` ≤ 宿主 | 插件根本装不上 |
| `minSdkVersion` ≤ 宿主内置 SDK | 会被标 Incompatible |
| `minHostVersion` ≤ 宿主版本 | 会被标 Incompatible |
| 隔离插件 + 宿主是否带 PluginHost | 隔离模式跑不起来 |
| 输出目录里有 `plugin.json` | 宿主靠它发现插件,没有等于插件不存在 |
| 入口程序集存在 | 忘了构建 |
| 入口旁有 `.deps.json` | 少了 `EnableDynamicLoading`,插件自带的 NuGet 依赖运行期一个都找不到 |
| 输出里没有 `VelaShell.PluginSdk.dll` / `Avalonia*.dll` | 多半绕过了 `VelaShell.PluginSdk.Build`;装载器强制共享宿主那份,带着只是徒增体积 |
| 启动配置是否已配好 | 还留着 `%VELASHELL_EXE%` 占位符 → 跑 `dev init` |

有阻断性问题时退出码为 `1`(适合放进 CI)。

---

## 4. 清单与打包

### `validate`

```bash
vela-plugin validate [dir|plugin.json]
```

用**宿主装载时的同一套规则**校验清单并确认入口程序集存在。
`VelaShell.PluginSdk.Build` 已把它接进构建后步骤(`VelaValidateManifestOnBuild`,增量执行),
所以正常情况下你不必手动跑。

### `pack`

```bash
vela-plugin pack <产物目录> [-o <输出>] [-k <key.pem>] [--no-mask]
```

把插件产物目录打成 `.vpx`。等价的一步到位写法是 `dotnet build -c Release -t:PackVpx`
(产物落 `bin/vpx/<id>-<version>.vpx`)。

- `-o` 传目录时用约定文件名 `<id>-<version>.vpx`,传完整路径则按你说的写。
- `-k` 同时签名(见 [`sign`](#sign))。
- `--no-mask` 关掉载荷掩码变换,**仅供排障**:关掉后包内就是可直接解开的 zip。

### `sign`

```bash
vela-plugin sign <pkg.vpx> -k <key.pem> [-o <输出>]
```

给已有的包补(或换)签名。默认原地重写。签名覆盖整个载荷摘要,所以先打包再签名与
打包时直接签名的结果等价。

### `verify`

```bash
vela-plugin verify <pkg.vpx> [-k <base64 公钥>]
```

校验载荷摘要与签名。不给 `-k` 时只验"签名自洽"(**不代表发布者可信**);
给了 `-k` 则要求签名必须出自该公钥。签名无效或不匹配时退出码为 `1`。

### `keygen`

```bash
vela-plugin keygen [-o <key.pem>] [--force]
```

生成 P-256 密钥对。私钥以 PKCS#8 PEM 落盘(非 Windows 上文件权限为 `0600`),
公钥与指纹打在输出里。

> **私钥丢了 = 换身份。** 用户信任的是"这个公钥指纹",换钥意味着老用户升级时会被
> 重新问一次信任。请离线备份,不要提交进仓库,CI 里走加密的机密变量。

### `info` / `unpack`

```bash
vela-plugin info   <pkg.vpx>          # 容器头、签名状态、清单摘要
vela-plugin unpack <pkg.vpx> [dir]    # 解包(带 zip-slip / 解压炸弹防护)
```

---

## 5. 宿主侧的启动参数

`dev init` 写进启动配置的那几个参数,也可以手工使用;它们都有等价的环境变量,
**参数优先**(参数跟着工程走,环境变量是机器级全局状态,同时开两个工程必然串味):

| 参数 | 等价环境变量 | 说明 |
| --- | --- | --- |
| `--dev-root <目录>` | `VELA_PLUGIN_DEV_ROOT`(路径分隔符分隔多条) | 开发期插件根,可重复 |
| `--wait-debugger[=<ids>]` | `VELA_PLUGIN_WAIT_DEBUGGER`(逗号/分号分隔) | 隔离插件等待调试器;不带值等同 `*`(全部) |
| `--data-root <目录>` | — | 数据根;连带切换单实例互斥键与数据库位置 |
| `--dev-watch` | — | 监视开发根,重编后自动重载 |

第三个来源是 `~/.velashell/plugins.dev.txt`(每行一个目录,`#` 起头为注释)。
三者叠加,顺序为:参数 → 环境变量 → 登记文件。开发根整体排在正式插件根**之后**,
同 id 先到先得 —— 本机开发中的插件不会顶掉用户已安装的同名插件。

---

## 6. 常见问题

**`No VelaShell installation is registered`**
本机没启动过 VelaShell(或只用过便携版且从未启动)。启动一次即可,或 `dev init --exe <路径>`。

**F5 起来弹"VelaShell 已在运行"然后就没了**
你的启动配置用的是共用数据根(`--shared-data`)。改回默认的独立数据根,或先退出日常实例。

**改了代码,重启宿主还是老行为**
先确认 `dotnet build` 真的过了(`vela-plugin doctor` 会告诉你入口程序集的状态);
再确认挂的是**新的**输出目录(`--dev-root` 指向的是 `bin/Debug` 这一级,不是 `net11.0`)。

**Windows 上重编报 dll 被占用**
不应该再发生:开发期插件从影子副本装载(`~/.velashell/dev-shadow/<id>/gen-N`),
宿主不锁工程 `bin`。若仍出现,多半是别的进程(上一次没退干净的宿主、杀软扫描)占着。

**隔离插件下断点后插件就"没了"**
没走 `--wait-debugger`。命中该开关的插件,宿主会**放宽激活超时并停掉心跳**;
不然断点冻住插件进程的全部线程,心跳连续失败就会把它当挂死强杀。

**怎么知道该附加到哪个进程**
pid 会打进日志、显示在插件管理页,并落在 `~/.velashell/logs/plugin-host-<id>.pid`。
