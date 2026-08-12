# VelaShell 全平台发布脚本(在 Windows 上运行)
# 产物(publish/ 目录),与 CI(.github/workflows/release.yml)同构:
#   Windows x64 / arm64 含运行时(self-contained)→ zip
#   macOS  x64 / arm64 含运行时 → tar.gz(Apple 签名/公证需在 macOS 上完成;
#       dmg(VelaShell.app 拖装包)仅由 CI 的 macOS runner 生成 —— hdiutil/iconutil/codesign
#       都是 macOS 独有工具。tar.gz 是应用内更新器的资产,dmg 只供人工安装,
#       latest.json 永远只指向 tar.gz,详见 release.yml 文件头"macOS 双产物分工")
#   Linux  x64 / arm64 含运行时 → tar.gz
# 每份产物内含 plugins/<id>/:仓库内声明了 <VelaPluginShip>true</VelaPluginShip> 的插件
#   (当前为 velashell.ai)由 MSBuild 在发布期登记入包,脚本无需额外拷贝;示例插件
#   velashell.hello-world 设了 false,只留在仓库与开发构建里,不进任何发行产物。
#   latest.json    — 应用内自更新清单(版本/标签/各 RID 产物名+sha256+大小)
#   SHA256SUMS.txt — 全部产物校验和
# 注意:tar 在 Windows 上不保留 Unix 可执行位,本脚本产出的 tar.gz 解包后需
#       chmod +x VelaShell VelaShell.PluginHost;正式发布以 CI 原生 runner 的产物为准。
# 用法: pwsh scripts/publish-all.ps1 [-Configuration Release]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\VelaShell\VelaShell.csproj'
$outRoot = Join-Path $root 'publish'

# 版本号取自 Directory.Build.props 的 <Version>
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw '未能从 Directory.Build.props 读到 <Version>' }
Write-Host "== VelaShell $version ($Configuration) ==" -ForegroundColor Cyan

$targets = @(
    @{ Rid = 'win-x64';     Archive = 'zip' }
    @{ Rid = 'win-arm64';   Archive = 'zip' }
    @{ Rid = 'osx-x64';     Archive = 'targz' }
    @{ Rid = 'osx-arm64';   Archive = 'targz' }
    @{ Rid = 'linux-x64';   Archive = 'targz' }
    @{ Rid = 'linux-arm64'; Archive = 'targz' }
)

if (Test-Path $outRoot) { Remove-Item $outRoot -Recurse -Force }
New-Item -ItemType Directory -Force $outRoot | Out-Null

$assets = [ordered]@{}
foreach ($t in $targets) {
    $name = "VelaShell-$version-$($t.Rid)"
    $dir = Join-Path $outRoot $name
    Write-Host "-- publish $name" -ForegroundColor Yellow
    # 刻意不加 PublishSingleFile:隔离插件的 VelaShell.PluginHost 必须在磁盘上有真实可执行体
    # (详见 src/VelaShell/VelaShell.csproj 里 Release PropertyGroup 上方的注释)。
    dotnet publish $project -c $Configuration -r $t.Rid -o $dir `
        -p:SelfContained=true -p:DebugType=None --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "publish 失败: $name" }
    Get-ChildItem $dir -Recurse -File -Filter '*.pdb' | Remove-Item -Force

    if ($t.Archive -eq 'zip') {
        $archive = "$name.zip"
        Compress-Archive -Path (Join-Path $dir '*') -DestinationPath (Join-Path $outRoot $archive) -Force
    } else {
        $archive = "$name.tar.gz"
        tar -czf (Join-Path $outRoot $archive) -C $dir .
        if ($LASTEXITCODE -ne 0) { throw "tar 失败: $name" }
    }
    $file = Get-Item (Join-Path $outRoot $archive)
    $assets[$t.Rid] = [ordered]@{
        name   = $archive
        sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        size   = $file.Length
    }
    Remove-Item $dir -Recurse -Force
}

# 应用内自更新清单(与 CI 生成的 latest.json 同构;tag 按约定为 v 前缀版本号)
$manifest = [ordered]@{
    version = $version
    tag     = "v$version"
    assets  = $assets
}
Set-Content (Join-Path $outRoot 'latest.json') ($manifest | ConvertTo-Json -Depth 4) -Encoding utf8NoBOM

# 校验文件(与 CI 同格式)
$sums = Get-ChildItem $outRoot -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content (Join-Path $outRoot 'SHA256SUMS.txt') $sums -Encoding utf8NoBOM

Write-Host "`n== 产物 ==" -ForegroundColor Cyan
Get-ChildItem $outRoot -File | Sort-Object Name | ForEach-Object {
    '{0,-58} {1,10:N1} MB' -f $_.Name, ($_.Length / 1MB)
}
