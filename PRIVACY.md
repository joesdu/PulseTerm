# VelaShell Privacy Policy / 隐私政策

**Last updated / 最后更新:2026-07-26**

[English](#english) · [简体中文](#简体中文)

---

## English

### Summary

VelaShell is an open-source SSH and SFTP client that runs entirely on your own computer.

**The developer of VelaShell operates no servers and receives no data from you.** There is no
account to create, no telemetry, no analytics, no crash reporting, and no advertising. Nothing
you type, no host you connect to, and no file you transfer is ever sent to us — we have no
infrastructure that could receive it.

Every network connection VelaShell makes is listed in full below.

### Information we collect

**None.** VelaShell has no backend service. We do not collect, store, transmit, sell, or share
any personal information.

### Information stored on your device

VelaShell stores the following locally so the application can work. This data never leaves your
computer unless you explicitly enable the optional sync feature described below.

| Data | Location |
| --- | --- |
| Connection profiles (host, port, username), groups, and settings | `~/.velashell` |
| Passwords and private-key passphrases you choose to save | Same folder, encrypted with AES-256 |
| The encryption key protecting the above | Same folder, generated on your device |
| Known SSH host keys | Same folder |
| Saved commands and snippets | Same folder |
| Session logs — raw terminal output, **off by default** | `logs` subfolder, auto-deleted after a retention period you set |
| Offline IP geolocation database, if you add one | `geoip` subfolder |
| Plugins you manually install | `~/.velashell/plugins` |

The application data folder is `~/.velashell` on Windows, macOS, and Linux. On first launch
after this location changed, VelaShell migrates the former platform-specific data directory
into this folder and removes the former directory after verification.

VelaShell also **reads** your existing OpenSSH configuration in `~/.ssh` (keys and
`known_hosts`) so that keys you already use with OpenSSH, Git, and other tools work without
being duplicated. These files are read from and written to on your device only.

You can delete all of this at any time by removing the corresponding folder, or by using the
in-app controls under Settings or Plugin Manager.

### Network connections

VelaShell connects to the network only in these situations:

1. **Servers you connect to.** SSH and SFTP sessions go directly from your computer to the
   host you specify. Credentials are sent only to that host, over the encrypted SSH channel.
   No traffic is proxied through us.

2. **Hosts you diagnose.** The ping and route-tracing tools send ICMP/UDP probes to the
   address you enter. IP geolocation is resolved **entirely offline** against a database file
   stored on your computer; no address is ever sent to a lookup service. The world map is drawn
   from vector data bundled inside the application — no online map tiles are requested.

3. **Update checks — manual only.** When you click "Check for updates" in Settings, VelaShell
   requests a release manifest from GitHub. GitHub receives your IP address and the request, as
   with any website visit. VelaShell never checks automatically in the background. **In the
   Microsoft Store version this feature is disabled entirely**, because the Store handles updates.

4. **Contributor avatars.** Opening the About page loads contributor profile pictures from
   GitHub, which discloses your IP address to GitHub in the same way. If the request fails,
   placeholder initials are shown and nothing else is affected.

5. **Cloud sync — off by default, entirely optional.** If you enable it, VelaShell stores your
   settings, connection profiles, and snippets in a **secret GitHub Gist in your own GitHub
   account**, using a personal access token that you supply. The data goes to your GitHub
   account, never to us. You may additionally set an end-to-end encryption passphrase, in which
   case the payload is encrypted with AES-GCM on your device before upload and GitHub stores
   only ciphertext. You choose which categories to sync, and you can turn it off or delete the
   Gist at any time.

VelaShell does not download the IP geolocation database itself. If you want that optional
feature, the application opens the DB-IP download page in your browser and you choose the
downloaded file manually.

### Third parties

VelaShell has no third-party SDKs, trackers, or advertising libraries.

The only third parties that can receive anything are those you direct it to:

- **The SSH/SFTP servers you connect to**, which are yours or your organization's.
- **GitHub**, for manual update checks, About-page avatars, and optional cloud sync into your
  own account. See the [GitHub Privacy Statement](https://docs.github.com/site-policy/privacy-policies/github-privacy-statement).
- **DB-IP**, only if you choose to download their free offline database, and only through your
  own browser. See the [DB-IP privacy policy](https://db-ip.com/legal/privacy-policy).

### Security

Saved passwords and passphrases are encrypted with AES-256 using a key generated on and stored
on your device. Optional sync payloads can additionally be end-to-end encrypted with AES-GCM
using a passphrase only you know.

Please note that session logging, when you turn it on, records **raw terminal output**,
which may include anything displayed on screen during a session. These logs are plain files on
your disk. Keep this in mind before enabling the feature on a shared machine.

No method of storage is perfectly secure, and VelaShell cannot protect data on a computer that
has already been compromised.

### Children

VelaShell is a developer tool and is not directed at children. We do not knowingly collect
information from anyone, including children.

### Changes

If this policy changes, the revised version will be published at this address with an updated
date above.

### Contact

VelaShell is open source. You can read every line of the code, including everything described
above, at <https://github.com/joesdu/VelaShell>.

Questions or concerns: <https://github.com/joesdu/VelaShell/issues>

---

## 简体中文

### 概述

VelaShell 是一款开源的 SSH / SFTP 客户端,完全运行在你自己的计算机上。

**VelaShell 的开发者不运营任何服务器,也不会收到你的任何数据。** 无需注册账号,没有遥测、
没有数据分析、没有崩溃上报、没有广告。你输入的内容、连接的主机、传输的文件,都不会被发送
给我们 —— 我们根本没有可以接收这些数据的基础设施。

VelaShell 发起的每一个网络连接,都完整列在下方。

### 我们收集的信息

**没有。** VelaShell 没有后端服务。我们不收集、不存储、不传输、不出售、不共享任何个人信息。

### 存储在你设备上的信息

以下数据保存在本地以支撑应用运行。除非你主动启用下文所述的可选同步功能,否则它们不会离开
你的计算机。

| 数据 | 位置 |
| --- | --- |
| 连接配置(主机、端口、用户名)、分组与应用设置 | `~/.velashell` |
| 你选择保存的密码与私钥口令 | 同上,以 AES-256 加密 |
| 保护上述内容的加密密钥 | 同上,在你的设备上生成 |
| 已知的 SSH 主机密钥 | 同上 |
| 快捷命令与代码片段 | 同上 |
| 会话日志 —— 终端原始输出,**默认关闭** | `logs` 子目录,按你设定的保留天数自动清理 |
| 离线 IP 归属地数据库(如果你添加了) | `geoip` 子目录 |
| 你手动安装的插件 | `~/.velashell/plugins` |

应用数据目录在 Windows、macOS 与 Linux 上统一为 `~/.velashell`。位置变更后的首次启动会把
原平台数据目录迁入此目录，校验成功后删除原目录。

VelaShell 还会**读取**你既有的 OpenSSH 配置(`~/.ssh` 下的密钥与 `known_hosts`),使你已经在
OpenSSH、Git 等工具中配置好的密钥无需重复配置即可使用。这些文件的读写全部发生在你的设备上。

你可以随时删除对应目录,或通过设置、插件管理器中的相应功能清除这些数据。

### 网络连接

VelaShell 仅在以下情形联网:

1. **你所连接的服务器。** SSH 与 SFTP 会话由你的计算机直连你指定的主机。凭据只通过加密的
   SSH 通道发送给该主机。没有任何流量经我们中转。

2. **你所诊断的主机。** Ping 与路由追踪向你输入的地址发送 ICMP/UDP 探测包。IP 归属地查询
   **完全离线**,基于保存在你计算机上的数据库文件完成,任何地址都不会被发往查询服务。
   世界地图由应用内置的矢量数据绘制,不请求任何在线地图瓦片。

3. **检查更新 —— 仅手动触发。** 当你在设置中点击"检查更新"时,VelaShell 会向 GitHub 请求
   发布清单。与访问任何网站一样,GitHub 会收到你的 IP 地址与该请求。VelaShell 从不在后台
   自动检查。**Microsoft Store 版本完全禁用了该功能**,更新由商店接管。

4. **贡献者头像。** 打开"关于"页面会从 GitHub 加载贡献者头像,同样会向 GitHub 暴露你的
   IP 地址。请求失败时显示首字母占位图,不影响其他功能。

5. **云同步 —— 默认关闭,完全可选。** 若你启用,VelaShell 会使用你自己提供的个人访问令牌,
   把设置、连接配置与代码片段保存到**你自己 GitHub 账户下的一个 secret Gist** 中。数据进入
   的是你的 GitHub 账户,而非我们。你还可以额外设置端到端加密口令,此时载荷会在你的设备上
   用 AES-GCM 加密后再上传,GitHub 只能拿到密文。同步范围由你勾选,可随时关闭或删除该 Gist。

VelaShell 不会自行下载 IP 归属地数据库。若你需要该可选功能,应用会在你的浏览器中打开 DB-IP
的下载页面,由你手动选择下载好的文件。

### 第三方

VelaShell 不含任何第三方 SDK、追踪器或广告库。

唯一可能接收到信息的第三方,都是由你指定的:

- **你连接的 SSH/SFTP 服务器**,它们属于你或你的组织。
- **GitHub**,用于手动检查更新、"关于"页头像,以及同步到你自己账户的可选云同步。参见
  [GitHub 隐私声明](https://docs.github.com/site-policy/privacy-policies/github-privacy-statement)。
- **DB-IP**,仅当你选择下载其免费离线数据库时,且全程通过你自己的浏览器。参见
  [DB-IP 隐私政策](https://db-ip.com/legal/privacy-policy)。

### 安全

已保存的密码与口令使用 AES-256 加密,密钥在你的设备上生成并保存在本地。可选的同步载荷还可
使用只有你知道的口令做 AES-GCM 端到端加密。

请注意:会话日志功能一旦开启,会记录**终端原始输出**,其中可能包含会话期间显示在屏幕上的
任何内容。这些日志是磁盘上的普通文件。在共用计算机上启用该功能前请考虑这一点。

没有任何存储方式是绝对安全的;对于一台已被入侵的计算机,VelaShell 无法保护其上的数据。

### 儿童

VelaShell 是开发者工具,并非面向儿童。我们不会在知情的情况下收集任何人(包括儿童)的信息。

### 变更

本政策如有修订,将在此地址发布新版本,并更新页首的日期。

### 联系方式

VelaShell 是开源软件。上述所有行为对应的每一行代码都可以在
<https://github.com/joesdu/VelaShell> 查阅。

疑问或建议请提交至 <https://github.com/joesdu/VelaShell/issues>
