# VelaShell.Plugin.Cli(`vela-plugin`)

VelaShell 插件开发者的命令行工具,以 .NET 全局工具形式分发。

```bash
dotnet tool install -g VelaShell.Plugin.Cli
vela-plugin --help
```

## 开发内环:三步到断点

```bash
dotnet build
vela-plugin dev init      # 生成 IDE 启动配置(读 ~/.velashell/host.json 找到本机安装)
# 在 IDE 里按 F5
```

`dev init` 生成的启动配置会以调试器附着的方式启动**本机已安装的 VelaShell**,
用 `--dev-root` 挂载本工程输出、用 `--data-root` 起一个独立数据根的调试实例
(于是日常那份 VelaShell 可以一直开着),隔离插件再加 `--wait-debugger`。

改完代码 `dotnet build`,在插件管理页点"重新加载"即可跑上新代码,不必重启宿主;
`dev init --watch` 则重编后自动重载。

## 命令

| 命令 | 作用 |
| --- | --- |
| `vela-plugin dev init [dir]` | 生成 IDE 启动配置(`--host` / `--exe` / `--data-root` / `--shared-data` / `--watch` / `--profile` / `--link`) |
| `vela-plugin dev run [dir]` | 不开 IDE,直接用同样的参数拉起宿主(`--wait` 等它退出) |
| `vela-plugin dev list` / `dev prune` | 查看 / 清理全局登记的开发根 |
| `vela-plugin dev link [dir]` / `dev unlink [dir]` | 把输出目录常挂进宿主(旧名 `dev-link` / `dev-unlink`) |
| `vela-plugin hosts` | 列出本机登记过的 VelaShell 安装 |
| `vela-plugin doctor [dir]` | 体检:宿主、清单兼容闸、构建产物、启动配置(有问题退出码 1) |
| `vela-plugin validate [dir]` | 校验 `plugin.json` 与入口程序集(与宿主同一套规则) |
| `vela-plugin pack <dir>` | 把插件产物目录打成 `.vpx` 包(可同时签名) |
| `vela-plugin info <pkg.vpx>` | 查看容器头、签名状态与清单 |
| `vela-plugin verify <pkg.vpx>` | 校验载荷摘要与签名 |
| `vela-plugin unpack <pkg.vpx> [dir]` | 解包(排障用) |
| `vela-plugin keygen` | 生成 P-256 签名密钥对 |
| `vela-plugin sign <pkg.vpx> -k key.pem` | 给已有包补签名 |
| `vela-plugin install <pkg.vpx>` | **禁用**:安装走宿主的插件管理页,以免绕过发布者授权与安装收据 |

打包不必装这个工具:`VelaShell.PluginSdk.Build` 包内已带同一份可执行体,
插件工程 `dotnet build -t:PackVpx` 直接出包。装全局工具是为了开发内环、体检、
签名与包检查。

- 命令行手册:<https://github.com/joesdu/VelaShell/blob/main/docs/plugins/cli.md>
- 插件开发指南:<https://github.com/joesdu/VelaShell/blob/main/docs/plugins/dev-guide.md>
- 打包与发布:<https://github.com/joesdu/VelaShell/blob/main/docs/plugins/publishing.md>
- 插件商店:<http://market.easilynet.top>
