# VelaShell Privacy Policy / 隐私政策

**Last updated / 最后更新:2026-08-31**

[English](#english) · [简体中文](#简体中文)

---

## English

### Summary

VelaShell is an open-source SSH and SFTP client that runs entirely on your own computer.

There is no account to create, no telemetry, no analytics, and no crash reporting. Nothing you
type, no host you connect to, and no file you transfer is ever sent to us.

The developer does operate **one** server that VelaShell talks to on its own: the news feed
behind the message centre (`feeds.easilynet.top`), which publishes security advisories and
product announcements. It is **on by default**, and VelaShell downloads a public JSON file from
it at startup and every few hours after that. The request carries nothing about you or your
installation — no account, no identifier, no version, no list of your hosts — so that server
learns only what any web server learns from a plain download: your IP address and the time.
Clearing the address in Settings → General → Message centre stops it completely: not "less
often", but never again.

Every network connection VelaShell makes is listed in full below.

### Information we collect

**None.** We do not collect, store, transmit, sell, or share any personal information. The news
feed is a one-way download of a file that is identical for everyone: nothing is uploaded to it,
it has no user accounts, and it cannot tell one VelaShell installation from another.

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

3. **The news feed — on by default.** VelaShell downloads
   <https://feeds.easilynet.top/feed.json> (a public file, https only) when it starts and then
   once every few hours, and shows what it finds — security advisories and announcements — in
   the message centre. The request is a plain GET: it sends no account, no identifier, and no
   information about your machine or your connections, so the server sees only your IP address,
   the same as any website you visit. Which entries apply to your platform, language, and
   version is decided **on your computer**, after the whole file is downloaded. You can point
   the address at your own feed, or clear it in Settings → General → Message centre, in which
   case no request is ever sent.

4. **Update checks.** VelaShell asks GitHub for a release manifest when you click "Check for
   updates" in Settings, and also once at startup while "Check for updates at startup" is on
   (Settings → General; on by default) so that a new version can appear in the message centre.
   GitHub receives your IP address and the request, as with any website visit. **In the
   Microsoft Store version this feature is disabled entirely**, because the Store handles updates.

5. **Contributor avatars.** Opening the About page loads contributor profile pictures from
   GitHub, which discloses your IP address to GitHub in the same way. If the request fails,
   placeholder initials are shown and nothing else is affected.

6. **Cloud sync — off by default, entirely optional.** If you enable it, VelaShell stores your
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
- **GitHub**, for update checks, About-page avatars, and optional cloud sync into your
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

无需注册账号,没有遥测、没有数据分析、没有崩溃上报。你输入的内容、连接的主机、传输的文件,
都不会被发送给我们。

开发者确实运营着**一台** VelaShell 会主动访问的服务器:消息中心背后的资讯源
(`feeds.easilynet.top`),发布安全资讯与产品公告。它**默认开启** —— 启动时拉一次,
之后每隔几小时再拉一次,下载的是一个所有人都一样的公开 JSON 文件。请求里不带任何与你、
与你这台机器有关的东西(没有账号、没有标识符、没有版本号、没有你的主机列表),因此那台
服务器能知道的,和任何网站从一次普通下载里能知道的一样多:你的 IP 地址与访问时间。
在「设置 → 常规 → 消息中心」把地址清空即可彻底停止 —— 不是"少发一点",而是一个请求都不再发。

VelaShell 发起的每一个网络连接,都完整列在下方。

### 我们收集的信息

**没有。** 我们不收集、不存储、不传输、不出售、不共享任何个人信息。资讯源是**单向下载**
一份对所有人完全相同的文件:没有任何东西被上传给它,它没有用户账号,也分不出两个 VelaShell
安装之间的区别。

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

3. **资讯源 —— 默认开启。** VelaShell 在启动时以及此后每隔几小时,下载一次
   <https://feeds.easilynet.top/feed.json>(公开文件,仅 https),把其中的安全资讯与公告
   显示在消息中心。请求是一次普通的 GET:不带账号、不带标识符,也不带任何关于你这台机器
   或你的连接的信息,那台服务器看到的只有你的 IP 地址 —— 与你访问任何网站时一样。
   哪些条目适用于你的平台、语言与版本,是在**你的计算机上**、把整份文件下载完之后才判定的。
   你可以把地址换成自建资讯源,也可以在「设置 → 常规 → 消息中心」把它清空,清空后一个请求
   都不会发出。

4. **检查更新。** 你在设置中点击"检查更新"时,以及"启动时检查更新"处于开启状态时
   (「设置 → 常规」,默认开启)启动时的那一次,VelaShell 会向 GitHub 请求发布清单,
   以便有新版本时投一条消息到消息中心。与访问任何网站一样,GitHub 会收到你的 IP 地址与
   该请求。**Microsoft Store 版本完全禁用了该功能**,更新由商店接管。

5. **贡献者头像。** 打开"关于"页面会从 GitHub 加载贡献者头像,同样会向 GitHub 暴露你的
   IP 地址。请求失败时显示首字母占位图,不影响其他功能。

6. **云同步 —— 默认关闭,完全可选。** 若你启用,VelaShell 会使用你自己提供的个人访问令牌,
   把设置、连接配置与代码片段保存到**你自己 GitHub 账户下的一个 secret Gist** 中。数据进入
   的是你的 GitHub 账户,而非我们。你还可以额外设置端到端加密口令,此时载荷会在你的设备上
   用 AES-GCM 加密后再上传,GitHub 只能拿到密文。同步范围由你勾选,可随时关闭或删除该 Gist。

VelaShell 不会自行下载 IP 归属地数据库。若你需要该可选功能,应用会在你的浏览器中打开 DB-IP
的下载页面,由你手动选择下载好的文件。

### 第三方

VelaShell 不含任何第三方 SDK、追踪器或广告库。

唯一可能接收到信息的第三方,都是由你指定的:

- **你连接的 SSH/SFTP 服务器**,它们属于你或你的组织。
- **GitHub**,用于检查更新、"关于"页头像,以及同步到你自己账户的可选云同步。参见
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
