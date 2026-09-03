#!/usr/bin/env bash
# 把一份已发布的 Linux self-contained 目录打成 .rpm(Fedora / RHEL / openSUSE 系)。
#
#   用法:build/linux-packages/Build-Rpm.sh <发布目录> <RID> <版本号> <输出目录>
#   例:  build/linux-packages/Build-Rpm.sh out/VelaShell-1.2.3-linux-x64 linux-x64 1.2.3 dist
#
# 安装树由 Stage-Payload.sh 铺(与 deb 共用),这里只负责填 spec 再交给 rpmbuild。
# 需要 rpmbuild(Debian 系 apt install rpm 即可,不必真的换到 rpm 发行版上)。
#
# 交叉打包没问题:--target 只是把包的架构标记改掉,而我们本来就不编译任何东西,
# x64 机器上照样产出 aarch64 的 rpm。
#
# **不进 latest.json**:装进 /opt/velashell 后目录归 root,应用内更新器的写探测必然失败,
# 关于页会自动降级成"发现新版本,请手动下载" —— 系统包本就该由包管理器更新,这是对的行为。
set -euo pipefail

publish_dir=${1:?用法: Build-Rpm.sh <发布目录> <RID> <版本号> <输出目录>}
rid=${2:?缺少 RID}
version=${3:?缺少版本号}
out_dir=${4:?缺少输出目录}

# rpm 的架构名(= uname -m),不是 .NET 的 RID,也不是 Debian 那套 amd64/arm64。
case "$rid" in
  linux-x64) arch=x86_64 ;;
  linux-arm64) arch=aarch64 ;;
  *)
    echo "Build-Rpm: 不支持的 RID '$rid'(只接受 linux-x64 / linux-arm64)" >&2
    exit 1
    ;;
esac

command -v rpmbuild >/dev/null 2>&1 || { echo "Build-Rpm: 缺少 rpmbuild(apt install rpm)" >&2; exit 1; }

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
publish_dir="$(cd "$publish_dir" && pwd)"
mkdir -p "$out_dir"
out_dir="$(cd "$out_dir" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# rpm 的 Version 字段里根本不允许出现 '-'(那是 Version-Release 的分隔符),预发布号必须换成 '~';
# '~' 同时带来正确的排序:1.2.0~preview.1 < 1.2.0。与 deb 侧的处理一致。
# 反斜杠不能省:替换串会先做波浪号展开,写成 ${version/-/~} 的话 ~ 会变成 $HOME。
pkgversion="${version/-/\~}"

"$script_dir/Stage-Payload.sh" "$publish_dir" "$work/root"

spec="$work/velashell.spec"
sed "s|@VERSION@|$pkgversion|g" "$script_dir/velashell.spec.in" > "$spec"

topdir="$work/rpmbuild"
mkdir -p "$topdir"
rpmbuild -bb \
  --target "$arch" \
  --define "_topdir $topdir" \
  --define "_sourcedir $work" \
  --quiet \
  "$spec"

built="$topdir/RPMS/$arch/velashell-$pkgversion-1.$arch.rpm"
[ -f "$built" ] || { echo "Build-Rpm: 没找到预期的产物 $built" >&2; exit 1; }
mv "$built" "$out_dir/"
echo "Build-Rpm: 已生成 $out_dir/$(basename "$built")"
