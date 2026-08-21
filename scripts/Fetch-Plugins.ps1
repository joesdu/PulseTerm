#Requires -Version 7.0
<#
.SYNOPSIS
    取回随安装包分发的第一方插件,解到 artifacts/plugins/。

.DESCRIPTION
    Redis / S3 / Telnet 插件于 2026-08-21 随工具链搬出主仓库,2026-08-22 又从工具链仓库
    独立出来,现在住在 https://github.com/joesdu/velashell-plugins
    (工具链仓库 velashell-plugin-toolchain 从此只管 SDK 与工具链,不再产出插件分发包)。
    它以 Release 资产 velashell-plugins-<版本>.zip 的形式交付,包内布局就是安装包
    plugins/ 那一层。本脚本按根 Directory.Build.props 里 pin 的 VelaPluginsBundleVersion
    下载那一版,校验 sha256,解到 artifacts/plugins/ —— 构建(F5)与发布都从这个目录取件
    (见 src/VelaShell/VelaShell.csproj 的 VelaPluginsStageDir)。

    **AI 插件不在其列**:它是本仓库自建的第一方插件(源码在 plugins/VelaShell.Plugin.Ai),
    随主程序一起构建、一起发布。分发包里若还带着 velashell-ai,取回后会被本脚本丢掉 ——
    同一个 id 出现两份会被 PluginManager 判重,后来者标 Invalid,表象是"插件莫名其妙用不了"。

    干净克隆之后不跑这个脚本一样能构建、能跑,只是启动后只有自建的那几个插件;
    `dotnet publish` 在两边都空时才失败 —— 发行包不接受"插件系统看着在、实则没插件"。

.PARAMETER Version
    要取的插件分发包版本。默认读 Directory.Build.props 的 VelaPluginsBundleVersion。

.PARAMETER FromPluginsRepo
    改为就地构建本机插件仓库的插件,不走网络。传插件仓库的路径,
    例如 -FromPluginsRepo G:\velashell-plugins。改插件时用这条。
    别名 -FromToolchain 保留,因为插件曾经确实在工具链仓库里 —— 老命令行不至于突然报错。

.PARAMETER Force
    即使暂存目录已是目标版本也重新取。

.EXAMPLE
    pwsh scripts/Fetch-Plugins.ps1

.EXAMPLE
    pwsh scripts/Fetch-Plugins.ps1 -FromPluginsRepo G:\velashell-plugins
#>
[CmdletBinding()]
param(
    [string] $Version,
    [Alias('FromToolchain')]
    [string] $FromPluginsRepo,
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

function Remove-LocallyBuiltPlugins {
    <#
        丢掉分发包里与**本仓库自建插件**同名的目录(当前是 velashell-ai)。
        自建插件的产物由 plugins/Directory.Build.targets 直接铺进宿主输出目录、由
        AddVelaPluginsToPublish 直接登记进发行包,不经过这个暂存目录;分发包里那一份
        只会是过期的重复,留着就是两份同 id。

        目录名 = csproj 里 <VelaPluginId> 把点换成短横(与 plugins/Directory.Build.targets 一致)。
    #>
    $localDirNames = Get-ChildItem (Join-Path $repoRoot 'plugins') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem $_.FullName -Filter *.csproj -File } |
        ForEach-Object {
            $m = [regex]::Match((Get-Content -Raw $_.FullName), '<VelaPluginId>([^<]+)</VelaPluginId>')
            if ($m.Success) { $m.Groups[1].Value.Replace('.', '-') }
        }

    foreach ($name in $localDirNames) {
        $dup = Join-Path $stageDir $name
        if (Test-Path $dup) {
            Write-Host "Dropping '$name' from the bundle — this repository builds it locally."
            Remove-Item $dup -Recurse -Force
        }
    }
}

if ($FromPluginsRepo) {
    $pluginsRepo = (Resolve-Path $FromPluginsRepo).Path
    $bundleProj = Join-Path $pluginsRepo 'build/PluginBundle.proj'
    if (-not (Test-Path $bundleProj)) {
        throw "在 '$pluginsRepo' 下找不到 build/PluginBundle.proj —— 这个路径是第一方插件仓库(joesdu/velashell-plugins)吗?"
    }
    Reset-StageDirectory
    Write-Host "Building first-party plugins from $pluginsRepo ..."
    # 用 Release:发行包里就是 Release 产物,联调时也保持一致,免得"本机好好的、发出去炸了"。
    & dotnet build $bundleProj -c Release -t:Bundle -p:BundleDir=$stageDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "PluginBundle.proj 构建失败。" }
    Remove-LocallyBuiltPlugins
    Set-Content $stampFile "local:$pluginsRepo"
    Write-Host "Plugins staged at $stageDir (local build)."
    return
}

if (-not $Version) {
    $props = Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props')
    $match = [regex]::Match($props, '<VelaPluginsBundleVersion[^>]*>([^<]+)</VelaPluginsBundleVersion>')
    $Version = if ($match.Success) { $match.Groups[1].Value } else { '' }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "读不出 Directory.Build.props 的 VelaPluginsBundleVersion,请用 -Version 显式指定。"
    }
}

if (-not $Force -and (Test-Path $stampFile) -and (Get-Content -Raw $stampFile).Trim() -eq $Version) {
    Write-Host "Plugins $Version already staged at $stageDir (use -Force to refetch)."
    return
}

$repo = 'joesdu/velashell-plugins'
$asset = "velashell-plugins-$Version.zip"
# 插件仓库的 Release 标签**不带前导 v**(1.4.1,不是 v1.4.1),与本仓库的体例正好相反 ——
# 2026-08-22 发 1.3.0 时四个平台的 job 全挂在这一步,就是照本仓库的习惯拼了个 v 出来。
# 两种写法都试一遍:对面的标签体例是对面的事,这边不该因为它换个前缀就整条发布流水线 404。
$tagCandidates = @($Version, "v$Version")
$temp = Join-Path ([IO.Path]::GetTempPath()) ("vela-plugins-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $temp | Out-Null

try {
    Write-Host "Downloading $asset ..."
    $zipPath = Join-Path $temp $asset
    $base = $null
    foreach ($tag in $tagCandidates) {
        $candidate = "https://github.com/$repo/releases/download/$tag"
        try {
            Invoke-WebRequest -Uri "$candidate/$asset" -OutFile $zipPath
            $base = $candidate
            break
        } catch {
            # 失败的尝试可能落下半截文件,清掉再换下一个候选 —— 否则下一轮的
            # -OutFile 覆盖不彻底时,校验和会对着一份混合内容算。
            Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
            Write-Host "  tag '$tag': $($_.Exception.Message)"
        }
    }
    if (-not $base) {
        throw "在 $repo 的 $($tagCandidates -join ' / ') 标签下都找不到 $asset。" +
              "确认插件仓库已发布该版本(当前 Directory.Build.props 的 VelaPluginsBundleVersion=$Version)," +
              "或用 -Version 指定一个已存在的版本。"
    }

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
    Remove-LocallyBuiltPlugins
    Set-Content $stampFile $Version
    $count = (Get-ChildItem $stageDir -Directory).Count
    Write-Host "Plugins $Version staged at $stageDir ($count plugin(s))."
} finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
