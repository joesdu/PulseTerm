# 商店版(MSIX)打包与上架

便携版与商店版是**同一份源码、同一条发布命令**,差异只在运行时:
`Services/AppPackaging.cs` 用 `GetCurrentPackageFullName` 探测包身份,装成 MSIX 就自动
关掉应用内自更新、改由商店接管(见 `IUpdateService.IsStoreManaged`)。
因此这里没有任何特殊的编译配置,也不存在"商店构建误发到 GitHub"的可能。

## 打包

```powershell
pwsh build/msix/Build-Msix.ps1 -Version 1.0.5
```

产出 `dist/VelaShell-<版本>.msixbundle`(x64 + arm64)。CI 里由 `release.yml` 的
`build-msix` 任务自动跑,产物作为工作流 artifact **`store-msix`** 留存 ——
它刻意不附加到 GitHub Release:未签名的 MSIX 用户装不上,挂上去只会造成困惑。

本机安装自测加 `-SelfSignForLocalTest`(自签证书需先导入"受信任的人")。
**提交商店的包不要自己签**:认证通过后微软会用商店证书重签,自签的会被替换掉。

## 产品标识

脚本默认值即本产品的真实标识,无需额外配置:

| 项 | 值 |
|---|---|
| `Package/Identity/Name` | `B0B5EDED.VelaShell` |
| `Package/Identity/Publisher` | `CN=3CDE21EE-6AB1-414B-BD2D-EDE8A225854B` |
| `Package/Properties/PublisherDisplayName` | `鹿宝丶` |
| 包系列名(PFN) | `B0B5EDED.VelaShell_xj3aay9s28z3c` |

这些不是机密 —— 任何人从商店下载的包里都能读到。`B0B5EDED.` 是账户前缀,
以后上架别的应用也是 `B0B5EDED.<应用名>`。

**PFN 不用填,是算出来的**:对 UTF-16LE 编码的 Publisher 串取 SHA-256,截前 8 字节,
按 13 组 5 位做 base32(字母表去掉了 i/l/o/u)。前两项填对,PFN 自然就对。
脚本每次打包都会算出来打印,与 Partner Center「产品管理 → 产品标识」页一比即知,
不必等上传失败才发现填错。

`PublisherDisplayName` 跟随**账户**的发布者显示名:要改它得去账户设置改账户级别的值
(影响名下所有产品),而不是改 manifest 迁就代码。

## 商店listing 徽标

```powershell
pwsh build/msix/New-StoreLogos.ps1
```

从 `src/VelaShell/Assets/velashell.png` 生成 `build/msix/store-logos/` 下的
300x300 / 150x150 / 71x71,在 Partner Center 的「应用商店列表 → 应用商店徽标」手动上传。

这三张是 **listing 资产**,决定商店页面上的展示;与打进包里的磁贴图标
(`Square150x150Logo` 等,由 `Build-Msix.ps1` 自动生成)是两码事,后者决定装到本机后
开始菜单/任务栏的样子。尺寸有重叠但用途不同,别混用。

默认裁掉源图四周的透明边距使图形填满画布 —— 那圈留白是图标文件的封装边距,
磁贴资产本身就该占满画框,71x71 下留白会明显吃掉可视面积。需要保留留白时加 `-KeepPadding`。

## 包内图标:为什么必须有 resources.pri

任务栏和开始菜单**不用** manifest 里写的 `Assets\Square44x44Logo.png`,而是按限定符
挑同名变体,`Build-Msix.ps1` 为 16/24/32/48/256 各出三张:

| 文件名后缀 | 用途 |
|---|---|
| `.targetsize-<N>` | 带底板(plated),开始菜单等处用 |
| `.targetsize-<N>_altform-unplated` | 无底板,**深色任务栏用的就是这张** |
| `.targetsize-<N>_altform-lightunplated` | 无底板,浅色任务栏用 |

三种要成套给:只给 unplated 而没有对应的 plated,系统会当这一档不存在。
`targetsize-*` 按规范不留边距(圆角与底板由系统加),故这些图裁掉了源图那圈封装留白;
`Square*Logo` / `Wide310x150Logo` 磁贴反过来需要留白,保持原样。

**限定符只有通过 `resources.pri` 才会被解析**,而 `makeappx pack` 不生成 PRI ——
必须先跑一遍 `makepri`(脚本里的 `New-ResourcesPri`)。包里没有 PRI 时,上面那一整套
变体全是死文件,系统只认 manifest 里写死的那张基图,并在它背后垫一块 `BackgroundColor`
的底;`BackgroundColor="transparent"` 垫的是用户主题色,于是任务栏图标变成一块纯色方块
(issue #135)。`makepri createconfig` 的默认配置还带 `<packaging>/<autoResourcePackage>`,
会把资源拆成独立的 `.pri` 资源包 —— 我们是单包提交,脚本会把这段删掉再索引。

改完可以验证一下限定符确实进了索引:

```powershell
makepri dump /if out/msix/win-x64/resources.pri /of dump.xml /o
```

`Square44x44Logo.png` 那个 `NamedResource` 下应能看到 15 个带
`TargetSize-* / AlternateForm-*` 限定符的 `Candidate`。

## 上架检查清单

- **版本号**:MSIX 只认四段纯数字,且第四段(修订号)保留给商店、必须为 0。
  脚本会把 `1.2.3-beta.1` 截成 `1.2.3.0` 并发警告 —— 打算上架的标签请用纯 semver,
  否则同一基础版本的多个预发布会撞号。
- **设备系列**:提交页只勾选 **Windows 10/11 桌面版**。勾了 Xbox / IoT / HoloLens 会被
  拦下("软件包必须支持选定的每个设备系列"),因为 manifest 只声明了 `Windows.Desktop`。
  SSH 客户端在那些设备上也没有意义,取消勾选即可,不要试图让包去支持它们。
- **`runFullTrust` 警告**:所有打包的 Win32 桌面应用都会出现,是警告不是错误,可继续提交。
  建议在「认证说明」里写明本应用是 Avalonia 桌面程序、需要 ConPTY 与 Win32 socket API、
  无法在 UWP 沙箱内运行,可减少来回。会走人工审核。
- **定价**:免费且无内购的应用不需要填付款账户与税务信息;一旦设为收费就会强制要求
  完成税务配置文件(非美国个人填 W-8BEN,填的是本国地址;要求填美国地址说明被
  误判成了 US person,检查税务问卷里的身份回答与账户国家/地区)。
- **隐私政策 URL**:联网应用的硬性要求。

## 数据目录

商店版与便携版都以用户主目录下的 `~/.velashell` 作为应用数据根。应用不再把持久化数据
写入 `%LocalAppData%\VelaShell`；旧目录只在升级后的首次启动中读取、校验迁移并删除。
