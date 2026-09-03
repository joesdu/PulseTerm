#!/usr/bin/env bash
# 把一份已发布的 Linux self-contained 目录打成 .deb(Debian / Ubuntu 系)。
#
#   用法:build/linux-packages/Build-Deb.sh <发布目录> <RID> <版本号> <输出目录>
#   例:  build/linux-packages/Build-Deb.sh out/VelaShell-1.2.3-linux-x64 linux-x64 1.2.3 dist
#
# 安装树由 Stage-Payload.sh 铺(与 rpm 共用),这里只负责套上 DEBIAN/control 再压包。
# 需要 dpkg-deb(Debian 系自带;别的发行版 apt/dnf 装 dpkg 即可)。
#
# **不进 latest.json**:装进 /opt/velashell 后目录归 root,应用内更新器的写探测必然失败,
# 关于页会自动降级成"发现新版本,请手动下载" —— 系统包本就该由包管理器更新,这是对的行为。
set -euo pipefail

publish_dir=${1:?用法: Build-Deb.sh <发布目录> <RID> <版本号> <输出目录>}
rid=${2:?缺少 RID}
version=${3:?缺少版本号}
out_dir=${4:?缺少输出目录}

# Debian 的架构名,不是 .NET 的 RID,也不是 uname -m。
case "$rid" in
  linux-x64) arch=amd64 ;;
  linux-arm64) arch=arm64 ;;
  *)
    echo "Build-Deb: 不支持的 RID '$rid'(只接受 linux-x64 / linux-arm64)" >&2
    exit 1
    ;;
esac

command -v dpkg-deb >/dev/null 2>&1 || { echo "Build-Deb: 缺少 dpkg-deb" >&2; exit 1; }

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
publish_dir="$(cd "$publish_dir" && pwd)"
mkdir -p "$out_dir"
out_dir="$(cd "$out_dir" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# 预发布版号里的第一个 '-' 必须换成 '~'。Debian 把 '-' 之后的部分当作 debian_revision,
# 于是 1.2.0-preview.1 会被判定为**比** 1.2.0 更新(有 revision > 无 revision),预发布反而盖过正式版;
# 而 '~' 排在"空"之前,1.2.0~preview.1 < 1.2.0,才是想要的顺序。rpm 侧同理。
# 反斜杠不能省:替换串会先做波浪号展开,写成 ${version/-/~} 的话 ~ 会变成 $HOME。
pkgversion="${version/-/\~}"

root="$work/root"
"$script_dir/Stage-Payload.sh" "$publish_dir" "$root"

installed_size="$(du -sk "$root" | cut -f1)"

mkdir -p "$root/DEBIAN"
# 依赖分两档,刻意的:
#   Depends   —— 只放十几年没改过名、各版本都在的那些。写错一个包名就是"装不上",
#                比"装上了但缺库"更糟。
#   Recommends —— ICU 与 OpenSSL 放这里。.NET 确实要它们(未开 InvariantGlobalization),
#                但 Debian/Ubuntu 每换一代就改一次 soname 包名(libicu67→70→71→72→74→76,
#                libssl1.1→libssl3→libssl3t64),写成硬依赖必然在某个发行版上把包卡死。
#                apt 默认会装 Recommends,直接 dpkg -i 的用户则至少不会被拦下。
#                **新发行版出了新的 libicuNN / libsslN,补在这一行。**
cat > "$root/DEBIAN/control" <<CONTROL
Package: velashell
Version: $pkgversion
Architecture: $arch
Maintainer: joesdu <dannymaximo369@gmail.com>
Installed-Size: $installed_size
Section: net
Priority: optional
Homepage: https://github.com/joesdu/VelaShell
Depends: libc6, libgcc-s1 | libgcc1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6
Recommends: libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67, libssl3t64 | libssl3 | libssl1.1
Description: Cross-platform SSH terminal client
 VelaShell is a self-drawn, plugin-based SSH terminal client built on Avalonia:
 tabbed sessions, split panes with drag-and-drop docking, SFTP, port forwarding
 and an extensible plugin system.
 .
 This package bundles the .NET runtime, so no system-wide .NET is required.
CONTROL

output="$out_dir/velashell_${pkgversion}_${arch}.deb"
# --root-owner-group:产物里的文件一律记为 root:root。不加的话记的是打包机上的 uid/gid
# (CI runner 是 1001),装到用户机器上就成了一堆归属错乱的文件。
# 压缩用默认的 xz:zstd 更快,但只有较新的 dpkg 读得动,对"下载即装"的分发资产不划算。
dpkg-deb --root-owner-group --build "$root" "$output"
echo "Build-Deb: 已生成 $output"
