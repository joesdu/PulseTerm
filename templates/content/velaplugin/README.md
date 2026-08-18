# VelaPlugin1

VelaShell 插件。插件 id:`PLUGIN_ID`。

## 开发内环

```bash
dotnet build

# 一次性:把本工程的输出目录登记为宿主的开发期插件根
dotnet tool install -g VelaShell.Plugin.Cli    # 只需装一次
vela-plugin dev-link bin/Debug/net11.0
```

之后每次 `dotnet build` 完重启 VelaShell 即加载到最新代码,插件在管理页带 **DEV** 角标。
不想再挂:`vela-plugin dev-unlink bin/Debug/net11.0`。

## 断点调试

- **inProcess 插件**:插件代码跑在 VelaShell 进程里 —— 用 IDE 的"附加到进程"选 `VelaShell`,
  或直接用本工程的 `VelaShell` 启动配置(需先把环境变量 `VELASHELL_EXE` 指向 VelaShell 可执行文件)。
- **isolated 插件**:插件跑在 `VelaShell.PluginHost` 子进程里。设环境变量
  `VELA_PLUGIN_WAIT_DEBUGGER=PLUGIN_ID` 后启动 VelaShell,子进程会在**装载插件程序集之前**
  挂起等你附加(进程 id 打在宿主日志里),于是 `ActivateAsync` 的第一行断点也能命中。
  这期间宿主会自动放宽激活超时并停掉心跳,不会把停在断点上的你判成"插件挂死"。

## 出包

```bash
dotnet build -c Release -t:PackVpx
# → bin/vpx/PLUGIN_ID-0.1.0.vpx

# 带签名(密钥用 `vela-plugin keygen` 生成,别提交进仓库)
dotnet build -c Release -t:PackVpx -p:VelaSigningKey=/path/to/key.pem
```

`.vpx` 在 VelaShell 的插件管理页"安装 .vpx…"一键装上,或 `vela-plugin install <包>`。

## 要点

- `plugin.json` 的 `id` 发布后**不要再改**:它同时是命令 id 前缀与插件私有数据/机密的命名空间。
- 插件 UI 直接用完整的 Avalonia,但**版本必须与宿主一致** —— 这一条由 SDK 包锁定,
  自己另外引用别的版本会在构建期直接报错(VELA1001)。
- `VelaShell.PluginSdk.dll` 与 `Avalonia*.dll` 永远不进插件目录:装载器强制共享宿主那一套。

完整开发指南:<https://github.com/joesdu/VelaShell/blob/main/docs/plugins/dev-guide.md>
