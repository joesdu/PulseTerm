# 编译、打包、签名与发布

> 相关文档:[开发指南](dev-guide.md) · [CLI 手册](cli.md) · [SDK 参考](sdk-reference.md)
> 插件商店:<http://market.easilynet.top>

本篇讲插件从"本机能跑"到"别人能装"的全过程。三件事:**出一个正确的包**、
**签一个稳定的名**、**把它交到用户手里**。

---

## 1. 发布前先定的三件事

### 1.1 插件 id 不可变

`plugin.json` 的 `id` 一旦发布就**不要再改**。它同时是:

- 命令 id 的前缀(`acme.snippets.run`);
- 插件私有数据与机密的命名空间(改 id = 用户数据凭空消失);
- 升级识别的依据(同 id 覆盖安装)。

命名约定 `<发布者>.<名称>`,字符集 `[a-z0-9.-]`,首尾必须是字母或数字,≤64 字符。

### 1.2 版本号与三道兼容闸

| 字段 | 谁来判 | 写错的后果 |
| --- | --- | --- |
| `version` | 语义化版本,`1.2.0` / `1.2.0-beta.1` | 商店与升级逻辑按它排序 |
| `apiLevel` | 高于宿主支持的代际 → 拒载 | 只在**破坏性**变更时才动,平时保持 `1` |
| `minHostVersion` | 低于它的宿主 → Incompatible | 用到了宿主新功能却不声明 = 老宿主上运行期炸 |
| `minSdkVersion` | 宿主内置 SDK 低于它 → Incompatible | **用到新 SDK 面的插件必须声明**,见下 |

`apiLevel` 太粗:它只管破坏性变更,而 SDK 的新增接口方法/DTO 字段不算破坏性。
用了 `IRemoteTunnelApi`(SDK 1.2)、`ITerminalViewApi`(1.3)、工作区变体(1.3.1)
却不声明 `minSdkVersion` 的插件,会在老宿主上装上、激活,然后在第一次调用新方法时抛
`MissingMethodException`。声明了,老宿主就在**发现期**干净地标 Incompatible 并说清该升级什么。

> 宿主自身的工具链能力(如 SDK 1.4 的 `HostRegistry`)不需要声明 —— 插件代码不调用它。

`vela-plugin doctor` 会拿本机宿主核对这三道闸。

### 1.3 宿主模式

`hostMode` 决定插件跑在哪:

- `inProcess`(默认):宿主进程内的可收集 ALC。面板可停靠成主窗口标签页,可用
  `Protocols` / `Workspaces` / `RemoteTunnel` / `TerminalView` 这几项能力。
- `isolated`:独立 `VelaShell.PluginHost` 进程,崩溃/卡死不影响宿主;面板是独立卡片窗口。

发布后改 `hostMode` 是**行为变更**,请当作次版本升级并在更新说明里写清。

---

## 2. Release 构建

```bash
dotnet build -c Release
```

插件工程只引一个包 `VelaShell.PluginSdk.Build`,它把这些一并带到:

- `EnableDynamicLoading=true` → 生成 `deps.json`(ALC 解析插件自带依赖的依据)、
  依赖复制到输出目录;
- `plugin.json` 复制进输出目录(宿主的插件发现只认它);
- **共享程序集不落地**:`VelaShell.PluginSdk.dll` 与 `Avalonia*.dll` 被排除出输出与包 ——
  装载器强制它们回落到宿主那一份(跨 ALC 的类型必须同一),带着只是徒增体积;
- Avalonia 版本一致性检查(不一致直接报 `VELA1001`,而不是等用户装上后在运行期
  表现为控件类型转换失败);
- 构建后的清单校验(与宿主装载时同一套规则)。

> **名字以 `Avalonia` 开头的第三方包用不了**:装载器按前缀强制共享,而宿主并不提供它们。
> 选用不以 `Avalonia` 开头的包,或把该依赖交给宿主。

原生依赖(P/Invoke 的 `.so` / `.dylib` / `.dll`)随 `deps.json` 的 RID 资产解析,
你需要为目标平台各出一份包,或在包里带齐全部 RID 的原生资产。

---

## 3. 打包 `.vpx`

```bash
dotnet build -c Release -t:PackVpx
# → bin/vpx/acme.snippets-0.1.0.vpx

# 或显式打包某个产物目录
vela-plugin pack bin/Release/net11.0 -o dist/
```

`.vpx` 是 VelaShell 的**专属容器**,不是改了后缀的 zip:

```text
┌ 64 字节头部 ─────────────────────────────────────────────┐
│ 魔数 56 50 58 1A · 格式版本 · 标志位 · 载荷长度            │
│ 载荷 SHA-256 · 掩码随机数 · 头部 CRC32                    │
└──────────────────────────────────────────────────────────┘
  掩码变换后的 zip 载荷
  可选:包尾签名块(JSON:alg / publicKey / signature)
```

签名签的是那 64 字节头部,而头部里含载荷长度与摘要 —— 等价于对全包签名。

包内应当有:入口 dll、`deps.json`、插件自带的第三方依赖、`plugin.json`、资源文件。
**不应当有**:`VelaShell.PluginSdk.dll`、`Avalonia*.dll`、`.pdb`(可选,调试符号会让包变大)、
任何密钥或凭据。`vela-plugin doctor` 与 `vela-plugin info` 都能帮你核。

---

## 4. 签名

### 4.1 生成密钥(一次性)

```bash
vela-plugin keygen -o ~/keys/acme.pem
# Private key written to ...  (keep it secret, keep it backed up)
# Public key (base64 SPKI): MFkwEw...
# Fingerprint: 3F2A ...
```

ECDSA P-256 + SHA-256(不用 Ed25519:它不在 BCL 里,而契约程序集不允许引重量级第三方库)。

> **私钥 = 你的发布者身份。** 离线备份,别提交进仓库。丢了就只能换钥,而换钥意味着
> 所有老用户在升级时会被重新问一次"是否信任这个发布者"。

### 4.2 签名与验签

```bash
# 打包时直接签
dotnet build -c Release -t:PackVpx -p:VelaSigningKey=$HOME/keys/acme.pem

# 或给已有的包补签
vela-plugin sign dist/acme.snippets-0.1.0.vpx -k ~/keys/acme.pem
vela-plugin verify dist/acme.snippets-0.1.0.vpx -k "MFkwEw..."   # 期望公钥
```

### 4.3 用户那一侧看到什么

| 包的状态 | 安装体验 |
| --- | --- |
| 签名有效 + 公钥已在本机受信 | 直接安装 |
| 签名有效 + 发布者未受信 | 弹出**公钥指纹**,要求用户经你的官方渠道核对后选择"信任发布者并安装" |
| 未签名 | 黄色警示("插件能以你的账号权限执行代码,仅在信任来源时安装"),需明确确认 |
| 签名损坏 / 内容被改过 | **一律拒装**,没有绕过入口 |

安装成功后宿主会落一份**受保护的安装收据**(内容哈希 + 完整性保护)。此后插件目录里的文件
被改动过,启动时会被标为 Invalid 并提示重装 —— 这是防"装完之后被别的程序掉包"。

所以:**请签名并公开你的公钥指纹**(README、商店页、官网都放一份)。指纹是用户唯一能
核对的东西,而且升级时的连续性也靠它。

---

## 5. 发布到插件商店

VelaShell 的插件商店在 <http://market.easilynet.top>。

> **当前状态(2026-08):客户端尚未内置商店客户端。** 用户从商店页下载 `.vpx`,
> 在 VelaShell 的「插件管理页 → 安装 .vpx…」安装。商店侧的具体提交表单/接口以站点为准,
> 下面列的是**无论走哪种表单都需要准备好的材料**。

### 5.1 提交材料清单

| 材料 | 来源 | 说明 |
| --- | --- | --- |
| `.vpx` 包 | `dotnet build -c Release -t:PackVpx` | **务必是签过名的** Release 包 |
| 插件 id / 版本 / 显示名 | `plugin.json` | 与包内清单完全一致,商店按 id 归档、按版本排序 |
| 发布者与作者 | `plugin.json` 的 `publisher` / `author` | 管理页会显示 `author`,缺省回退 `publisher` |
| 公钥指纹 | `vela-plugin keygen` / `sign` 的输出 | 用户核对身份的唯一凭据;**首次提交后不要更换** |
| 简介与截图 | 自备 | 一句话说清"它解决什么问题",截图至少一张主界面 |
| 兼容性声明 | `apiLevel` / `minHostVersion` / `minSdkVersion` | 商店据此告诉用户"你的 VelaShell 版本能不能装" |
| 更新说明 | 自备 | 每个版本一段;新增权限/新增能力务必写明 |
| 许可与源码地址 | `plugin.json` 的 `license` / `homepage` | 开源插件强烈建议给出仓库地址 |

提交前跑一遍:

```bash
vela-plugin doctor                     # 环境与清单体检
vela-plugin verify dist/xxx.vpx        # 签名自洽
vela-plugin info   dist/xxx.vpx        # 包头、指纹、清单三者对得上
```

### 5.2 发布节奏与更新

- **同 id 覆盖升级**:用户装新版时旧目录被替换,插件数据(KV / 机密 / 时序库)保留。
  数据结构变了要自己做迁移 —— 卸载才会清数据,升级不会。
- **同 id 请始终用同一把私钥**:换钥会让用户在升级时重新面对信任提示。
- **降级**:用户可以装回旧版 `.vpx`;若新版写过不兼容的数据结构,旧版要能容忍(读不懂就重建)。
- **撤回**:发现严重问题时在商店下架该版本并尽快发补丁版。宿主目前**没有**远程吊销机制,
  所以已经装到用户机器上的版本不会自动消失 —— 这是把兼容性与安全性做在发布前的理由。

### 5.3 CI 里出包(GitHub Actions 示例)

```yaml
name: release-plugin
on:
  push:
    tags: ['v*']

jobs:
  pack:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '11.0.x' }

      # 私钥走加密机密,落盘后立刻用完即弃
      - name: Restore signing key
        run: |
          install -m 600 /dev/null "$RUNNER_TEMP/key.pem"
          printf '%s' "${{ secrets.VELA_PLUGIN_KEY }}" > "$RUNNER_TEMP/key.pem"

      - name: Pack
        run: dotnet build -c Release -t:PackVpx -p:VelaSigningKey="$RUNNER_TEMP/key.pem"

      - name: Verify
        run: |
          dotnet tool install -g VelaShell.Plugin.Cli
          vela-plugin verify bin/vpx/*.vpx -k "${{ vars.VELA_PLUGIN_PUBKEY }}"

      - uses: actions/upload-artifact@v4
        with:
          name: vpx
          path: bin/vpx/*.vpx
```

`vela-plugin doctor` 在 CI 里同样可用(有阻断性问题时退出码 1),但它需要一份宿主才能
核对版本闸;CI 上没有宿主时它只做工程侧检查。

---

## 6. 发布前检查清单

- [ ] `id` 与已发布版本一致,`version` 已递增
- [ ] `apiLevel` / `minHostVersion` / `minSdkVersion` 与实际用到的 API 相符
- [ ] `displayName` / `description` / `author` / `license` / `homepage` 填好
- [ ] Release 构建,`vela-plugin doctor` 无阻断问题
- [ ] 包内没有 `VelaShell.PluginSdk.dll` / `Avalonia*.dll` / 密钥 / 凭据
- [ ] 已签名,`vela-plugin verify -k <公钥>` 通过
- [ ] 在**干净机器/干净数据根**上装过一次并跑通主流程
      (`vela-plugin dev run --data-root ~/.velashell-clean` 可以快速造一个干净环境)
- [ ] 更新说明写清了新增能力与行为变更
