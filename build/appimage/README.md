# Linux AppImage 打包

AppImage 是 Linux 上的"下载即用"单文件格式:一个可执行文件里塞着完整的 squashfs 镜像,
`chmod +x` 后双击就跑,不装、不解压、不碰系统目录。它与 tar.gz 是**同一份发布产物的两种容器**,
定位等同 macOS 的 `.dmg` —— 给人手动安装用的那份。

## 打包

```bash
# 先按发布流水线的方式产出 self-contained 目录
dotnet publish src/VelaShell/VelaShell.csproj -c Release -r linux-x64 \
  -o out/VelaShell-1.2.3-linux-x64 -p:Version=1.2.3 -p:SelfContained=true
# 再把它装进 AppImage
build/appimage/Build-AppImage.sh out/VelaShell-1.2.3-linux-x64 linux-x64 1.2.3 dist
```

产出 `dist/VelaShell-<版本>-<RID>.AppImage`。CI 里由 `release.yml` 的 `build-linux`
任务在打完 tar.gz 之后顺手跑一遍,两个 RID 各出一份,随 Release 一起发布。

**只需要 x64 runner。** mksquashfs 与"runtime + squashfs 拼接"都与目标架构无关,
按 `$arch` 下对应的 runtime 就能在 x64 上产出 aarch64 的 AppImage,无需 arm64 runner。

宿主机上只额外要一个系统自带的 `file` 命令;`mksquashfs` 与 `desktop-file-validate`
由 appimagetool 自带(见下)。

## AppDir 布局

```
AppDir/
├── AppRun                     # 入口:按自身位置定位 usr/bin/VelaShell 并 exec
├── VelaShell.desktop          # 由 src/VelaShell/VelaShell.desktop 改写 Exec 生成
├── velashell.png              # Icon= 指向的同名图标,必须在根目录
├── .DirIcon -> velashell.png  # 桌面集成读缩略图的固定入口
└── usr/
    ├── bin/                   # tar.gz 那份扁平发布目录,原样整个搬进来
    └── share/applications/    # 桌面集成安装时读的那份 .desktop
```

`usr/bin` 与 tar.gz 的布局逐字节一致,所以应用侧对 `AppContext.BaseDirectory` 的一切假设
—— `plugins/` 目录、随包的 `VelaShell.PluginHost` —— 在 AppImage 里照旧成立。

## 三个不能想当然的选择

**appimagetool 取 `AppImage/appimagetool` 的 1.9.1,不是 AppImageKit 13。**
AppImageKit 仓库已归档,上游把 13 的发布资产统一改名成了 `obsolete-*`,
网上到处能搜到的 `.../AppImageKit/releases/download/13/appimagetool-x86_64.AppImage` 现在是 404。

**runtime 必须是 type2-runtime 的 continuous 版。** AppImageKit 时代的旧 runtime 依赖
`libfuse.so.2`,而 Ubuntu 22.04 起默认不装 libfuse2,用户双击只会得到一句
`dlopen(): error loading libfuse.so.2`。新 runtime 静态链接 fuse3,裸系统上也能跑。
脚本用 `--runtime-file` 显式下载指定 —— 不给这个参数 appimagetool 也会自己去下同一个文件,
但显式下载才有重试、才看得见失败原因。

**appimagetool 解包后再跑,且跑的是 `squashfs-root/AppRun`。** 它自己也是个 AppImage,
`--appimage-extract` 这条路不碰 FUSE(既不要 `/dev/fuse` 也不要 libfuse2),在容器和精简
runner 上行为确定;而必须走 AppRun 而不是里面的 `usr/bin/appimagetool`,是因为前者会把自带的
`mksquashfs` 与 `desktop-file-validate` 加进 `PATH`,后者找不到它们会直接 die。

## 自更新

AppImage **不进 `latest.json`**,理由和 dmg 一样、但更硬:

* 应用内更新器是"原地换版"(按相对路径把新版解进应用目录),只认识 zip/tar.gz;
* AppImage 运行时应用目录是**只读的 squashfs 挂载点**,想换也换不动。

`UpdateService.CanSelfUpdate` 会通过写探测发现这一点(`UpdateApplier.IsApplicationDirectoryWritable`),
自动把关于页降级成"发现新版本,请手动下载",不会走到必然失败的换版流程 —— 无需为 AppImage 加任何判定。
