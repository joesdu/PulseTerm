# VelaShell.Plugin.Cli(`vela-plugin`)

VelaShell 插件开发者的命令行工具,以 .NET 全局工具形式分发。

```bash
dotnet tool install -g VelaShell.Plugin.Cli
vela-plugin --help
```

| 命令 | 作用 |
| --- | --- |
| `vela-plugin validate [dir]` | 校验 `plugin.json` 与入口程序集(与宿主同一套规则) |
| `vela-plugin pack <dir>` | 把插件产物目录打成 `.vpx` 包(可同时签名) |
| `vela-plugin info <pkg.vpx>` | 查看容器头、签名状态与清单 |
| `vela-plugin verify <pkg.vpx>` | 校验载荷摘要与签名 |
| `vela-plugin unpack <pkg.vpx> [dir]` | 解包(排障用) |
| `vela-plugin keygen` | 生成 P-256 签名密钥对 |
| `vela-plugin sign <pkg.vpx> -k key.pem` | 给已有包补签名 |
| `vela-plugin install <pkg.vpx>` | 装进本机 VelaShell 的用户插件目录 |
| `vela-plugin dev-link [dir]` | 把插件工程输出目录登记为开发期插件根(管理页显示 DEV) |
| `vela-plugin dev-unlink [dir]` | 取消上面的登记 |

打包不必装这个工具:`VelaShell.PluginSdk.Build` 包内已带同一份可执行体,
插件工程 `dotnet build -t:PackVpx` 直接出包。装全局工具是为了在构建之外
随手校验、签名、查看包内容。

- 插件开发指南:<https://github.com/joesdu/VelaShell/blob/main/docs/plugins/dev-guide.md>
- 包格式说明:`VelaShell.PluginSdk` 的 `Packaging/VpxContainer.cs`
