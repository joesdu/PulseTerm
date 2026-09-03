#!/usr/bin/env bash
# 把一份已发布的 Linux self-contained 目录铺成 deb / rpm 共用的 FHS 安装树。
#
#   用法:build/linux-packages/Stage-Payload.sh <发布目录> <目标根目录>
#
# deb 与 rpm 的差别只在"外壳"(控制信息 + 打包命令),装进系统的文件是**逐字节相同**的一棵树,
# 所以这一步抽出来共用 —— 两边各铺一遍迟早会长歪。
#
#   /opt/velashell/                     整个自包含发布目录(与 tar.gz 内容一致)
#   /usr/bin/velashell                  → /opt/velashell/VelaShell 的软链,让命令行直接可用
#   /usr/share/applications/velashell.desktop
#   /usr/share/icons/hicolor/<N>x<N>/apps/velashell.png
#   /usr/share/doc/velashell/copyright  AGPL 全文(Debian 策略要求,rpm 侧一并带上)
#
# 装进 /opt 而不是 /usr/lib/velashell:这是 FHS 给"发行版仓库之外的整包软件"留的位置,
# 而 src/VelaShell/VelaShell.desktop 里的 Exec 从一开始写的就是 /opt/velashell/VelaShell ——
# 那份 .desktop 正是为这条路径准备的,这里原样装进去,不像 AppImage 那样需要改写 Exec。
set -euo pipefail

publish_dir=${1:?用法: Stage-Payload.sh <发布目录> <目标根目录>}
root=${2:?缺少目标根目录}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"

# ImageMagick 7 是 magick,6 才是 convert;两个都没有就直说,别让包里少个图标还打得出来。
if command -v magick >/dev/null 2>&1; then
  im=(magick)
elif command -v convert >/dev/null 2>&1; then
  im=(convert)
else
  echo "Stage-Payload: 需要 ImageMagick 生成各尺寸图标(apt install imagemagick)" >&2
  exit 1
fi

mkdir -p "$root/opt/velashell" "$root/usr/bin" \
  "$root/usr/share/applications" "$root/usr/share/doc/velashell"
cp -a "$publish_dir/." "$root/opt/velashell/"
# 两个可执行体:主程序,以及隔离插件的宿主进程(由主程序按需拉起)。
chmod +x "$root/opt/velashell/VelaShell" "$root/opt/velashell/VelaShell.PluginHost"

# 绝对路径的软链:deb 与 rpm 都原样保留软链,装好后 `velashell` 直接可在终端里敲。
ln -sfn /opt/velashell/VelaShell "$root/usr/bin/velashell"

# .desktop 原样装(Exec 已是 /opt/velashell/VelaShell);文件名按包名小写,与 Icon=velashell 对应。
cp "$repo_root/src/VelaShell/VelaShell.desktop" "$root/usr/share/applications/velashell.desktop"

# 图标只有一张 1024×1024 的源图,而 hicolor 主题的 index.theme 里根本没有 1024 这一档 ——
# 直接装进去等于没装(图标查找会跳过未声明的尺寸),必须降采样成标准档位。
for size in 16 32 48 64 128 256 512; do
  dir="$root/usr/share/icons/hicolor/${size}x${size}/apps"
  mkdir -p "$dir"
  "${im[@]}" "$repo_root/src/VelaShell/Assets/velashell.png" \
    -resize "${size}x${size}" "$dir/velashell.png"
done

cp "$repo_root/LICENSE" "$root/usr/share/doc/velashell/copyright"

# 目录 755、普通文件 644,可执行位只留给真正要跑的那些 —— dpkg-deb --root-owner-group 只管
# 属主不管权限位,而 dotnet publish 出来的产物权限并不统一(有 600 的,装进系统就成了 root 专属)。
find "$root" -type d -exec chmod 0755 {} +
find "$root" -type f -exec chmod 0644 {} +
chmod 0755 "$root/opt/velashell/VelaShell" "$root/opt/velashell/VelaShell.PluginHost"
# createdump 之类随运行时带的辅助可执行体也要保住可执行位。
find "$root/opt/velashell" -maxdepth 1 -type f -name 'createdump' -exec chmod 0755 {} +
