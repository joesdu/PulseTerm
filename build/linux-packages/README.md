# Linux 发行版原生包(deb / rpm)

给"装进系统"的那批用户:`apt install ./velashell_*.deb` 或 `dnf install ./velashell-*.rpm`,
装完出现在应用菜单里,终端里敲 `velashell` 也能起。
免安装的单文件 AppImage 是另一条路,在 [`build/appimage/`](../appimage/README.md) ——
它和这两个包共享不了任何东西(布局不同、受众不同),所以刻意分开放。

## 打包

```bash
# 先按发布流水线的方式产出 self-contained 目录
dotnet publish src/VelaShell/VelaShell.csproj -c Release -r linux-x64 \
  -o out/VelaShell-1.2.3-linux-x64 -p:Version=1.2.3 -p:SelfContained=true
# 再分别套壳
build/linux-packages/Build-Deb.sh out/VelaShell-1.2.3-linux-x64 linux-x64 1.2.3 dist
build/linux-packages/Build-Rpm.sh out/VelaShell-1.2.3-linux-x64 linux-x64 1.2.3 dist
```

需要 `dpkg-deb`(Debian 系自带)、`rpmbuild`(`apt install rpm`)与 `imagemagick`。
CI 里由 `release.yml` 的 `build-linux` 任务自动跑,两个 RID 各出一份 deb 与一份 rpm。

**只需要 x64 runner。** 这两步都不编译任何东西,只是把已有产物换个外壳:
deb 的架构写在 `control` 里,rpm 的架构由 `rpmbuild --target` 指定,x64 上照样产出 arm64/aarch64 包。

## 安装布局

`Stage-Payload.sh` 铺的这棵树 deb 与 rpm **完全共用**,两边只有外壳不同:

| 路径 | 内容 |
|---|---|
| `/opt/velashell/` | 整个自包含发布目录(与 tar.gz 内容一致) |
| `/usr/bin/velashell` | → `/opt/velashell/VelaShell` 的软链 |
| `/usr/share/applications/velashell.desktop` | 菜单项 |
| `/usr/share/icons/hicolor/<N>x<N>/apps/velashell.png` | 7 档尺寸的图标 |
| `/usr/share/doc/velashell/copyright` | AGPL 全文 |

装进 `/opt` 是 FHS 给"发行版仓库之外的整包软件"留的位置,而
`src/VelaShell/VelaShell.desktop` 的 `Exec` 从一开始写的就是 `/opt/velashell/VelaShell` ——
那份 .desktop 正是为这条路径准备的,这里原样装进去(AppImage 才需要改写 `Exec`)。

**图标必须现降采样。** 仓库里只有一张 1024×1024 的源图,而 hicolor 主题的 `index.theme`
根本没有 1024 这一档,直接装进去等于没装(图标查找会跳过未声明的尺寸)。

## 命名

刻意跟随各自发行版的惯例,而不是本仓库其它资产的 `VelaShell-<版本>-<RID>`:

```
velashell_1.2.3_amd64.deb        velashell-1.2.3-1.x86_64.rpm
velashell_1.2.3_arm64.deb        velashell-1.2.3-1.aarch64.rpm
```

这两个文件名是给 `dpkg`/`rpm` 和各类仓库工具解析的(包名、版本、架构都从里面读),
改成自定义样式会让它们认不出来。

**预发布版号里的第一个 `-` 会换成 `~`**,`1.2.0-preview.1` → `1.2.0~preview.1`。两个理由:

* rpm 的 `Version` 字段里 `-` 是非法字符(那是 `Version-Release` 的分隔符);
* deb 会把 `-` 之后的部分当成 `debian_revision`,于是 `1.2.0-preview.1` 被判定为**比**
  `1.2.0` 更新 —— 预发布反而盖过正式版。`~` 排在"空"之前,顺序才对。

脚本里写的是 `${version/-/\~}`,**反斜杠不能省**:替换串会先做波浪号展开,裸 `~` 会变成 `$HOME`。

## 依赖

分档是刻意的,底线是"宁可装上之后缺库报错,也不要根本装不上":

* **deb `Depends`** 只放十几年没改过名、各版本都在的那些(`libc6`、`libstdc++6`、
  `libfontconfig1`、`libx11-6` …)。写错一个包名就是装不上。
* **deb `Recommends`** 放 ICU 与 OpenSSL。.NET 确实要它们(没开 `InvariantGlobalization`),
  但 Debian/Ubuntu 每换一代就改一次 soname 包名(`libicu67`→`70`→`71`→`72`→`74`→`76`,
  `libssl1.1`→`libssl3`→`libssl3t64`),写成硬依赖必然在某个发行版上把包卡死。
  apt 默认会装 Recommends,直接 `dpkg -i` 的用户至少不会被拦下。
  **新发行版出了新的 `libicuNN` / `libsslN`,补进 `Build-Deb.sh` 的那一行。**
* **rpm 干脆不写 `Requires`**,并且 `AutoReqProv: no`。rpm 系各发行版的包名彼此不同
  (Fedora 的 `libX11` 在 openSUSE 叫 `libX11-6`),写死任何一套都会在另一套上炸;
  需要什么库改在 `%description` 里说明。

`AutoReqProv: no` 还有个更硬的理由:开着的话 rpmbuild 会去扫 `/opt/velashell` 里几百个
随运行时带的 `.so`,把它们统统登记成本包的 `Provides`(别的包可能就解析到我们的私有副本上),
同时按精确 soname 生成一堆装不上的 `Requires`。

同理,spec 里把 rpm 默认的构建后处理全关了(`__os_install_post`、`debug_package`、
`_build_id_links`):我们打的是**已经编译好的**产物,让它去 strip 二进制、抽 debuginfo,
轻则白费时间重则弄坏产物。

## 自更新

deb / rpm **不进 `latest.json`**,和 AppImage、dmg 一样:装进 `/opt/velashell` 后目录归 root,
`UpdateApplier.IsApplicationDirectoryWritable()` 的写探测必然失败,
`UpdateService.CanSelfUpdate` 随之为 false,关于页自动降级成"发现新版本,请手动下载"。
系统包本就该由包管理器更新 —— 这是对的行为,不需要为它们加任何判定。
