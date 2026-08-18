# VelaShell.PluginSdk.Build

VelaShell 插件工程唯一需要引用的包。引一个,下面这些全都到位:

```xml
<PackageReference Include="VelaShell.PluginSdk.Build" Version="1.0.0-preview.1" />
```

| 它做了什么 | 为什么 |
| --- | --- |
| 传递引入 `VelaShell.PluginSdk`(契约)与 **与宿主版本一致的 Avalonia**,均为编译期引用 | 装载器强制插件与宿主共享这两类程序集;版本不一致会在用户机器上才炸 |
| `EnableDynamicLoading=true`、`plugin.json` 进输出目录 | 插件是被动态装载的组件,ALC 靠 `deps.json` 解析自带依赖 |
| 共享程序集的运行时资产不落插件目录 | 放进去也不会被加载,只是撑大包体 |
| Avalonia 版本冲突从警告升为错误(NU1608/NU1605) | 版本漂移必须在构建期就红,而不是装机后才现形 |
| 构建后按宿主同一套规则校验 `plugin.json` | 杜绝"本机构建过、宿主装不上" |
| `dotnet build -t:PackVpx` 一步出 `.vpx` | 打包器随包分发,不必安装任何全局工具 |

## 出包

```bash
dotnet build -c Release -t:PackVpx
# → bin/vpx/<插件id>-<版本>.vpx

# 带签名(密钥用 `vela-plugin keygen` 生成,不要提交进仓库)
dotnet build -c Release -t:PackVpx -p:VelaSigningKey=/path/to/key.pem
```

## 可调属性

| 属性 | 默认 | 说明 |
| --- | --- | --- |
| `VelaPluginManifest` | `$(MSBuildProjectDirectory)\plugin.json` | 清单路径 |
| `VelaVpxOutputDirectory` | `bin\vpx\` | `.vpx` 产物目录 |
| `VelaSigningKey` | 空 | 打包时用的 PEM 私钥 |
| `VelaPackMask` | `true` | 是否对载荷做掩码变换 |
| `VelaValidateManifestOnBuild` | `true` | 构建后是否校验清单 |
| `VelaSkipAvaloniaVersionCheck` | `false` | 跳过 Avalonia 版本一致性检查 |

完整开发指南:<https://github.com/joesdu/VelaShell/blob/main/docs/plugins/dev-guide.md>
