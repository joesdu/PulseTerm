#Requires -Version 7.0
<#
.SYNOPSIS
    取回随安装包分发的第一方插件,解到 artifacts/plugins/。

.DESCRIPTION
    插件(AI / Redis / S3 / Telnet)已于 2026-08-21 随工具链搬到独立仓库
    https://github.com/joesdu/velashell-plugin-toolchain
    并以 Release 资产 velashell-plugins-<版本>.zip 的形式交付,包内布局就是安装包
    plugins/ 那一层。本脚本按根 Directory.Build.props 里 pin 的 VelaPluginsBundleVersion
    下载那一版,校验 sha256,解到 artifacts/plugins/ —— 构建(F5)与发布都从这个目录取件
    (见 src/VelaShell/VelaShell.csproj 的 VelaPluginsStageDir)。

    干净克隆之后不跑这个脚本一样能构建、能跑,只是启动后一个插件都没有;
    `dotnet publish` 则会直接失败 —— 发行包不接受"插件系统看着在、实则没插件"。

.PARAMETER Version
    要取的插件分发包版本。默认读 Directory.Build.props 的 VelaPluginsBundleVersion。

.PARAMETER FromToolchain
    改为就地构建本机工具链仓库的插件,不走网络。传工具链仓库的路径,
    例如 -FromToolchain G:\velashell-plugin-toolchain。改插件时用这条。

.PARAMETER Force
    即使暂存目录已是目标版本也重新取。

.EXAMPLE
    pwsh scripts/Fetch-Plugins.ps1

.EXAMPLE
    pwsh scripts/Fetch-Plugins.ps1 -FromToolchain G:\velashell-plugin-toolchain
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $FromToolchain,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$stageDir = Join-Path $repoRoot 'artifacts/plugins'
$stampFile = Join-Path $stageDir '.bundle-version'

function Reset-StageDirectory {
    # 整目录换掉而不是叠加:上一版留下的插件目录不会被新版覆盖删除,
    # 留着就成了两份同 id 插件,宿主判重后把后来者标 Invalid —— 表象是"插件莫名其妙用不了"。
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    New-Item -ItemType Directory -Force $stageDir | Out-Null
}

if ($FromToolchain) {
    $toolchain = (Resolve-Path $FromToolchain).Path
    $bundleProj = Join-Path $toolchain 'build/PluginBundle.proj'
    if (-not (Test-Path $bundleProj)) {
        throw "在 '$toolchain' 下找不到 build/PluginBundle.proj —— 这个路径是插件工具链仓库吗?"
    }
    Reset-StageDirectory
    Write-Host "Building first-party plugins from $toolchain ..."
    # 用 Release:发行包里就是 Release 产物,联调时也保持一致,免得"本机好好的、发出去炸了"。
    & dotnet build $bundleProj -c Release -t:Bundle -p:BundleDir=$stageDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "PluginBundle.proj 构建失败。" }
    Set-Content $stampFile "local:$toolchain"
    Write-Host "Plugins staged at $stageDir (local build)."
    return
}

if (-not $Version) {
    $props = Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props')
    $match = [regex]::Match($props, '<VelaPluginsBundleVersion[^>]*>([^<]+)</VelaPluginsBundleVersion>')
    # pin 默认写成 $(VelaSdkVersion),那就再解一层。
    $Version = if ($match.Success -and $match.Groups[1].Value -notmatch '^\$\(') {
        $match.Groups[1].Value
    } else {
        [regex]::Match($props, '<VelaSdkVersion[^>]*>([^<]+)</VelaSdkVersion>').Groups[1].Value
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "读不出 VelaPluginsBundleVersion / VelaSdkVersion,请用 -Version 显式指定。"
    }
}

if (-not $Force -and (Test-Path $stampFile) -and (Get-Content -Raw $stampFile).Trim() -eq $Version) {
    Write-Host "Plugins $Version already staged at $stageDir (use -Force to refetch)."
    return
}

$repo = 'joesdu/velashell-plugin-toolchain'
$asset = "velashell-plugins-$Version.zip"
$base = "https://github.com/$repo/releases/download/v$Version"
$temp = Join-Path ([IO.Path]::GetTempPath()) ("vela-plugins-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $temp | Out-Null

try {
    Write-Host "Downloading $asset ..."
    $zipPath = Join-Path $temp $asset
    Invoke-WebRequest -Uri "$base/$asset" -OutFile $zipPath

    # sha256 核对。SHA256SUMS.txt 与 zip 出自同一次流水线运行,取不到就明说 ——
    # 静默跳过校验等于把"下到半截的包"变成用户机器上的启动崩溃。
    $sumsPath = Join-Path $temp 'SHA256SUMS.txt'
    Invoke-WebRequest -Uri "$base/SHA256SUMS.txt" -OutFile $sumsPath
    $expected = (Get-Content $sumsPath | Where-Object { $_ -match [regex]::Escape($asset) + '$' } |
        Select-Object -First 1) -split '\s+' | Select-Object -First 1
    if (-not $expected) { throw "SHA256SUMS.txt 里没有 $asset 的条目。" }
    $actual = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected.ToLower()) {
        throw "$asset 校验和不符:期望 $expected,实际 $actual。"
    }

    Reset-StageDirectory
    Expand-Archive -Path $zipPath -DestinationPath $stageDir -Force
    Set-Content $stampFile $Version
    $count = (Get-ChildItem $stageDir -Directory).Count
    Write-Host "Plugins $Version staged at $stageDir ($count plugin(s))."
} finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
