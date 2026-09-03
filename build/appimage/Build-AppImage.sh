#!/usr/bin/env bash
# 把一份已经发布好的 Linux self-contained 目录打成 AppImage(免安装、免解压、双击即跑的单文件)。
#
#   用法:build/appimage/Build-AppImage.sh <发布目录> <RID> <版本号> <输出目录>
#   例:  build/appimage/Build-AppImage.sh out/VelaShell-1.2.3-linux-x64 linux-x64 1.2.3 dist
#
# 与 macOS 的 dmg 同属"给人用"的安装资产:布局上就是 tar.gz 那份扁平发布目录整个搬进
# AppDir/usr/bin,再由 AppRun 按挂载点相对定位启动 —— 应用侧看到的 BaseDirectory 结构不变。
# **它不进 latest.json**:应用内更新器只认识 zip/tar.gz 的原地解包换版;何况 AppImage 跑起来时
# 应用目录是只读的 squashfs 挂载点,UpdateService 的可写探测会自动把关于页降级成"请手动下载"。
#
# 三处刻意的选择,改之前先读:
#   · appimagetool 取 AppImage/appimagetool 的 **1.9.1**(打了 tag 的固定版本)。不要回头去用
#     AppImageKit 13 —— 那个仓库已归档,发布资产在上游被统一改名成 obsolete-*,老地址现在是 404。
#   · runtime 用 --runtime-file 显式指定 type2-runtime 的 continuous 版(静态链接 fuse3)。
#     不给这个参数 appimagetool 也会自己去下同一个文件,但显式下载才有重试、才看得见失败原因。
#     绝不能用 AppImageKit 时代的旧 runtime:它依赖 libfuse2,而 Ubuntu 22.04 起默认不装,
#     用户双击只会得到一句 dlopen(): error loading libfuse.so.2。
#   · appimagetool 本身是个 AppImage,这里 --appimage-extract 解开再跑解出来的 AppRun。
#     解包这条路不碰 FUSE(不需要 /dev/fuse,也不需要 libfuse2),在容器/精简 runner 上行为确定;
#     且必须跑 AppRun 而不是里面的 usr/bin/appimagetool —— 前者会把自带的 mksquashfs 与
#     desktop-file-validate 加进 PATH,后者找不到它们会直接 die。
#     (还需要系统自带的 file 命令,GitHub 的 ubuntu 镜像与各主流发行版默认都有。)
#   交叉打包没问题:mksquashfs 与"runtime + squashfs 拼接"都与目标架构无关,x64 机器上照样
#   产出 aarch64 的 AppImage,不需要 arm64 runner —— runtime 按 $arch 下对的那份即可。
set -euo pipefail

publish_dir=${1:?用法: Build-AppImage.sh <发布目录> <RID> <版本号> <输出目录>}
rid=${2:?缺少 RID}
version=${3:?缺少版本号}
out_dir=${4:?缺少输出目录}

# AppImage 用的是 uname -m 的架构名,不是 .NET 的 RID。
case "$rid" in
  linux-x64) arch=x86_64 ;;
  linux-arm64) arch=aarch64 ;;
  *)
    echo "Build-AppImage: 不支持的 RID '$rid'(只接受 linux-x64 / linux-arm64)" >&2
    exit 1
    ;;
esac

appimagetool_version=1.9.1
appimagetool_url="https://github.com/AppImage/appimagetool/releases/download/$appimagetool_version/appimagetool-x86_64.AppImage"
runtime_url="https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-$arch"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
publish_dir="$(cd "$publish_dir" && pwd)"
mkdir -p "$out_dir"
out_dir="$(cd "$out_dir" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# 取工具:appimagetool 解包备用,runtime 按目标架构单独下。
tools="$work/tools"
mkdir -p "$tools"
curl -fsSL --retry 3 --retry-all-errors -o "$tools/appimagetool.AppImage" "$appimagetool_url"
curl -fsSL --retry 3 --retry-all-errors -o "$tools/runtime-$arch" "$runtime_url"
chmod +x "$tools/appimagetool.AppImage"
(cd "$tools" && ./appimagetool.AppImage --appimage-extract >/dev/null)

# 搭 AppDir。
appdir="$work/AppDir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications"
cp -a "$publish_dir/." "$appdir/usr/bin/"
# 两个可执行体:主程序,以及隔离插件的宿主进程(由主程序按需拉起)。
chmod +x "$appdir/usr/bin/VelaShell" "$appdir/usr/bin/VelaShell.PluginHost"
install -m 0755 "$script_dir/AppRun" "$appdir/AppRun"

# 桌面入口沿用仓库里那份唯一的 .desktop(装进 /opt 的版本),只把 Exec 换成包内相对名字 ——
# AppImage 实际由 AppRun 启动,Exec 只是给桌面集成工具看的标识,写死绝对路径反而是错的。
desktop="$appdir/VelaShell.desktop"
sed 's|^Exec=.*|Exec=VelaShell|' "$repo_root/src/VelaShell/VelaShell.desktop" > "$desktop"
cp "$desktop" "$appdir/usr/share/applications/VelaShell.desktop"

# 图标:Icon=velashell,同名文件必须躺在 AppDir 根;.DirIcon 是桌面集成读缩略图的固定入口
# (不建也行,appimagetool 会照着 Icon= 自己补一个软链,这里直接给,少一层不确定)。
# 直接用 1024×1024 的源图不缩放 —— 缩放要额外拉 ImageMagick,而各桌面环境本就会自行降采样。
cp "$repo_root/src/VelaShell/Assets/velashell.png" "$appdir/velashell.png"
ln -sf velashell.png "$appdir/.DirIcon"

output="$out_dir/VelaShell-$version-$rid.AppImage"
# --no-appstream:仓库没有 AppStream 元数据,不该因为没装 appstreamcli 就让打包失败。
# ARCH:AppDir 里全是 .NET 的产物,让 appimagetool 自己猜架构既慢又可能猜错,直接告诉它。
ARCH="$arch" "$tools/squashfs-root/AppRun" \
  --no-appstream \
  --runtime-file "$tools/runtime-$arch" \
  "$appdir" "$output"
chmod +x "$output"
echo "Build-AppImage: 已生成 $output"
