# VelaShell.Plugin.Templates

VelaShell 插件工程的 `dotnet new` 模板。

```bash
dotnet new install VelaShell.Plugin.Templates
```

| 模板 | 短名 | 内容 |
| --- | --- | --- |
| VelaShell plugin | `velaplugin` | 入口类 + 一条命令 + `plugin.json`,可直接出 `.vpx` |
| VelaShell plugin with a panel | `velaplugin-ui` | 上面的一切 + 一个 Avalonia 面板(AXAML) |

```bash
# 基础插件(id = acme.snippets)
dotnet new velaplugin -n Snippets --publisher acme --authorName "Your Name"

# 带面板、跑在独立进程里
dotnet new velaplugin-ui -n Dashboard --publisher acme --hostMode isolated
```

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--publisher` | `acme` | 插件 id 的发布者段(小写) |
| `--authorName` | 空 | 插件管理页展示的作者(`author` 是模板引擎的保留名,故加了后缀) |
| `--hostMode` | `inProcess` | `inProcess` / `isolated` |
| `--sdkVersion` | 当前版本 | 引用的 `VelaShell.PluginSdk.Build` 版本 |

插件 id 由 `--publisher` 与 `-n` 拼出并全部转小写(`acme` + `Snippets` → `acme.snippets`)。
**发布后不要再改 id**:它同时是命令 id 前缀与插件私有数据/机密的命名空间。

卸载模板:`dotnet new uninstall VelaShell.Plugin.Templates`。

完整开发指南:<https://github.com/joesdu/VelaShell/blob/main/docs/plugins/dev-guide.md>

## 生成之后

```bash
cd Snippets
dotnet build
vela-plugin dev init      # 配好 IDE 启动配置,然后按 F5 断点调试
```

`dev init` 会读 `~/.velashell/host.json`(VelaShell 每次启动自己写的)找到本机安装,
生成的启动配置用独立数据根起一个调试实例,于是日常那份 VelaShell 可以一直开着。
改完代码 `dotnet build`,在插件管理页点"重新加载"即可跑上新代码。

- 命令行手册:<https://github.com/joesdu/VelaShell/blob/main/docs/plugins/cli.md>
- 打包与发布:<https://github.com/joesdu/VelaShell/blob/main/docs/plugins/publishing.md>
