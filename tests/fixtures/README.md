# tests/fixtures/ —— 插件运行时的测试夹具

宿主的插件运行时测试(发现、装载、惰性激活、隔离进程、装卸载、开发挂载、终端协议
适配)需要**一个真实的插件程序集**才能端到端跑通 —— 手写一个假的 `IVelaPlugin` 类
放在测试程序集里不行,那样走不到"从磁盘上的目录发现 `plugin.json` → 在可收集 ALC
里装载一个独立 dll → 跨进程激活"这条真实路径。

拆库之前这个角色由 `plugins/VelaShell.Plugin.HelloWorld` 和
`plugins/VelaShell.Plugin.Telnet` 兼任。插件已于 2026-08-21 搬到独立的插件仓库
<https://github.com/VelaShellLabs/velashell-plugins>,本仓库拿不到它们了,
于是把"当夹具"这件事**显式化**成这两个工程:

| 工程 | id | 用途 |
| --- | --- | --- |
| [VelaShell.TestPlugin](VelaShell.TestPlugin/) | `velashell.test-fixture` | 通用夹具:激活写存储、注册若干命令。驱动发现/装载/激活/停用/启停/装卸载/惰性激活/隔离进程的全部用例 |
| [VelaShell.TestPlugin.Terminal](VelaShell.TestPlugin.Terminal/) | `velashell.test-terminal` | 终端协议夹具:清单里声明 `contributes.protocols` + `onProtocol` 惰性激活,激活后注册成终端协议。驱动"清单发现 → 惰性激活 → 终端协议 → 宿主适配 → 真实套接字"整条链 |

两个夹具都**刻意保持零第三方依赖**(只引 `VelaShell.PluginSdk`):用例大量使用
"只把入口 dll 复制到临时插件根"的铺法,多一个依赖就要多复制一个文件,
而那不是这些用例要验的东西。

它们不是示例代码 —— 想看插件怎么写,读第一方插件仓库的
[`plugins/`](https://github.com/VelaShellLabs/velashell-plugins/tree/main/plugins)
与 [开发指南](https://github.com/VelaShellLabs/velashell-docs/blob/main/zh/templates/dev-guide.md)。
