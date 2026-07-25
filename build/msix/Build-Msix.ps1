<#
.SYNOPSIS
    把 VelaShell 打成上架 Microsoft Store 用的 MSIX 包(双架构 .msixbundle)。

.DESCRIPTION
    商店版与便携版共用同一份源码和同一条发布命令,差异全在运行时判定(见 Services/AppPackaging.cs):
    装成 MSIX 就自动关掉应用内自更新,改由商店接管。因此这里不需要任何特殊的编译配置。

    与便携版发布的唯一区别是 -p:PublishSingleFile=false —— 单文件在 MSIX 里没有意义
    (包本身就是一个容器),摊开发布反而让商店的差量更新只传改动过的文件。

    产物不签名:提交到商店的 MSIX 由微软在认证通过后用商店证书签名,自己签反而会被替换掉。
    需要本地安装自测时,用 -SelfSignForLocalTest 生成一张自签证书就地签一份(仅供本机安装)。

.NOTES
    需要 Windows PowerShell 5.1 或 PowerShell 7+(图标缩放用 System.Drawing,故仅限 Windows),
    以及 Windows SDK 里的 makeappx.exe(GitHub windows-latest runner 自带)。

.EXAMPLE
    pwsh build/msix/Build-Msix.ps1 -Version 1.2.3
    pwsh build/msix/Build-Msix.ps1 -Version 1.2.3 -IdentityName 12345Publisher.VelaShell -Publisher "CN=ABCD1234-..."
#>
[CmdletBinding()]
param(
    # 语义版本(可带预发布后缀,会被剥掉:MSIX 版本号只能是纯数字)。
    [Parameter(Mandatory = $true)]
    [string]$Version,

    # 要打包的运行时标识;每个出一个 .msix,最后合成一个 .msixbundle。
    [string[]]$Runtimes = @('win-x64', 'win-arm64'),

    # 以下三项必须与 Partner Center → 产品管理 → 产品标识 逐字一致(含大小写与非 ASCII 字符),
    # 否则上传会被验证拒绝。默认值即本产品的真实标识,可直接出可提交的包。
    # 这些不是机密:任何人从商店下载的包里都能读到,提交到公开仓库无妨。
    # 包系列名(PFN)由 Name + Publisher 哈希而来,故这两项一旦写错,PFN 也跟着错。
    [string]$IdentityName = 'B0B5EDED.VelaShell',
    [string]$Publisher = 'CN=3CDE21EE-6AB1-414B-BD2D-EDE8A225854B',
    [string]$PublisherDisplayName = '鹿宝丶',

    [string]$OutputDirectory = 'dist',
    [string]$IntermediateDirectory = 'out/msix',

    # 仅供本机安装自测:用自签证书签名(商店提交请勿使用)。
    [switch]$SelfSignForLocalTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$projectPath = Join-Path $repoRoot 'src\VelaShell\VelaShell.csproj'
$templatePath = Join-Path $PSScriptRoot 'AppxManifest.template.xml'
$sourceIcon = Join-Path $repoRoot 'src\VelaShell\Assets\velashell.png'

# ———————————————————— 工具函数 ————————————————————

function ConvertTo-MsixVersion {
    <#  1.2.3-beta.1 → 1.2.3.0。商店规定第四段(修订号)保留给商店,必须为 0,
        因此同一基础版本的多个预发布无法并存 —— 上架用的标签请使用纯 semver。 #>
    param([string]$Semver)
    $core = ($Semver -split '[-+]')[0]
    $parts = @($core -split '\.')
    while ($parts.Count -lt 3) { $parts += '0' }
    if ($Semver -ne $core) {
        Write-Warning "MSIX 版本号不支持预发布后缀,'$Semver' 已截断为 $($parts[0]).$($parts[1]).$($parts[2]).0"
    }
    return '{0}.{1}.{2}.0' -f $parts[0], $parts[1], $parts[2]
}

function Get-PublisherIdHash {
    <#  按 MSIX 的公开算法推导包系列名后半段:对 UTF-16LE 编码的 Publisher 串取 SHA-256,
        截前 8 字节(64 位),末尾补一个 0 位凑成 65 位,再按 13 组 5 位做 base32 编码。
        字母表刻意去掉了 i/l/o/u(避免与 1/0 混淆,也避免拼出脏词)。 #>
    param([string]$PublisherString)

    $bytes = [System.Text.Encoding]::Unicode.GetBytes($PublisherString)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash($bytes) } finally { $sha.Dispose() }

    $alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
    $bits = ''
    foreach ($b in $hash[0..7]) { $bits += [Convert]::ToString($b, 2).PadLeft(8, '0') }
    $bits += '0'
    $result = ''
    for ($i = 0; $i -lt 65; $i += 5) {
        $result += $alphabet[[Convert]::ToInt32($bits.Substring($i, 5), 2)]
    }
    return $result
}

function Get-MakeAppxPath {
    $sdkBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (-not (Test-Path $sdkBin)) {
        throw "找不到 Windows SDK($sdkBin)。请安装 Windows 10/11 SDK 后重试。"
    }
    $versions = Get-ChildItem $sdkBin -Directory |
        Where-Object { $_.Name -like '10.*' } |
        Sort-Object { [version]($_.Name) } -Descending
    foreach ($dir in $versions) {
        $candidate = Join-Path $dir.FullName 'x64\makeappx.exe'
        if (Test-Path $candidate) { return $candidate }
    }
    throw "在 $sdkBin 下找不到 makeappx.exe。"
}

function New-ScaledPng {
    <# 等比缩放并居中放到透明画布上(宽磁贴 310x150 与方形图标共用这一套)。 #>
    param([string]$Source, [string]$Destination, [int]$Width, [int]$Height)

    Add-Type -AssemblyName System.Drawing
    $image = [System.Drawing.Image]::FromFile($Source)
    try {
        $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $scale = [Math]::Min($Width / $image.Width, $Height / $image.Height)
                $w = [int][Math]::Round($image.Width * $scale)
                $h = [int][Math]::Round($image.Height * $scale)
                $graphics.DrawImage($image, [int](($Width - $w) / 2), [int](($Height - $h) / 2), $w, $h)
            }
            finally { $graphics.Dispose() }
            $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $bitmap.Dispose() }
    }
    finally { $image.Dispose() }
}

function New-MsixAssets {
    <# 生成 manifest 引用到的全部图标。targetsize-* 是任务栏/开始菜单用的无边距变体。 #>
    param([string]$AssetsDirectory)

    New-Item -ItemType Directory -Force $AssetsDirectory | Out-Null
    $square = @{
        'StoreLogo.png'          = 50
        'Square44x44Logo.png'    = 44
        'Square71x71Logo.png'    = 71
        'Square150x150Logo.png'  = 150
        'Square310x310Logo.png'  = 310
    }
    foreach ($name in $square.Keys) {
        New-ScaledPng -Source $sourceIcon -Destination (Join-Path $AssetsDirectory $name) `
                      -Width $square[$name] -Height $square[$name]
    }
    foreach ($size in @(16, 24, 32, 48, 256)) {
        New-ScaledPng -Source $sourceIcon `
                      -Destination (Join-Path $AssetsDirectory "Square44x44Logo.targetsize-$size`_altform-unplated.png") `
                      -Width $size -Height $size
    }
    New-ScaledPng -Source $sourceIcon -Destination (Join-Path $AssetsDirectory 'Wide310x150Logo.png') `
                  -Width 310 -Height 150
}

function New-AppxManifest {
    param([string]$Destination, [string]$MsixVersion, [string]$Architecture)

    $xml = Get-Content $templatePath -Raw -Encoding UTF8
    $xml = $xml.Replace('{IDENTITY_NAME}', $IdentityName)
    $xml = $xml.Replace('{PUBLISHER}', $Publisher)
    $xml = $xml.Replace('{PUBLISHER_DISPLAY_NAME}', $PublisherDisplayName)
    $xml = $xml.Replace('{VERSION}', $MsixVersion)
    $xml = $xml.Replace('{ARCH}', $Architecture)
    # 无 BOM 的 UTF-8:makeappx 对带 BOM 的 manifest 会报 XML 解析错误。
    [System.IO.File]::WriteAllText($Destination, $xml, (New-Object System.Text.UTF8Encoding($false)))
}

# ———————————————————— 主流程 ————————————————————

$msixVersion = ConvertTo-MsixVersion $Version
$makeappx = Get-MakeAppxPath
Write-Host "MSIX 版本号 : $msixVersion"
Write-Host "包标识      : $IdentityName / $Publisher"
Write-Host "发布者显示名: $PublisherDisplayName"
Write-Host "makeappx    : $makeappx"

# 包系列名由 Name + Publisher 推导,是"标识填对了没有"的唯一可验证凭据。就地算出来打印,
# 与 Partner Center 产品标识页上的 Package Family Name 一比即知,不必靠上传失败才发现填错。
$publisherHash = Get-PublisherIdHash $Publisher
Write-Host "包系列名    : ${IdentityName}_$publisherHash"
if ($publisherHash -ne 'xj3aay9s28z3c') {
    Write-Warning ("包系列名与 Partner Center 登记的 B0B5EDED.VelaShell_xj3aay9s28z3c 不符," +
        "Publisher 多半填错了。上传会被拒,请核对 Partner Center → 产品管理 → 产品标识。")
}

$outDir = Join-Path $repoRoot $OutputDirectory
$intermediateRoot = Join-Path $repoRoot $IntermediateDirectory
$bundleInput = Join-Path $intermediateRoot 'bundle-input'
New-Item -ItemType Directory -Force $outDir | Out-Null
if (Test-Path $bundleInput) { Remove-Item -Recurse -Force $bundleInput }
New-Item -ItemType Directory -Force $bundleInput | Out-Null

foreach ($rid in $Runtimes) {
    $arch = $rid -replace '^win-', ''
    $layout = Join-Path $intermediateRoot $rid
    Write-Host "`n=== 发布 $rid ==="
    if (Test-Path $layout) { Remove-Item -Recurse -Force $layout }

    # PublishSingleFile=false:MSIX 本身就是容器,摊开发布才能让商店做差量更新。
    & dotnet publish $projectPath -c Release -r $rid -o $layout `
        -p:Version=$Version -p:SelfContained=true -p:PublishSingleFile=false `
        -p:DebugType=None --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败:$rid" }

    Get-ChildItem $layout -Recurse -File -Filter '*.pdb' | Remove-Item -Force

    New-MsixAssets -AssetsDirectory (Join-Path $layout 'Assets')
    New-AppxManifest -Destination (Join-Path $layout 'AppxManifest.xml') `
                     -MsixVersion $msixVersion -Architecture $arch

    $msixPath = Join-Path $bundleInput "VelaShell-$Version-$rid.msix"
    Write-Host "=== 打包 $rid ==="
    & $makeappx pack /o /d $layout /p $msixPath
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack 失败:$rid" }
}

$bundlePath = Join-Path $outDir "VelaShell-$Version.msixbundle"
Write-Host "`n=== 合成 msixbundle ==="
& $makeappx bundle /o /d $bundleInput /p $bundlePath /bv $msixVersion
if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle 失败' }

if ($SelfSignForLocalTest) {
    # 仅供本机安装自测:证书的主题必须与 manifest 的 Publisher 逐字一致,否则安装被拒。
    # 装之前需先把这张证书导入"受信任的人"或"受信任的根证书颁发机构"。
    Write-Host "`n=== 自签(仅本地测试)==="
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher `
        -KeyUsage DigitalSignature -FriendlyName 'VelaShell local test' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $signtool = (Get-MakeAppxPath) -replace 'makeappx\.exe$', 'signtool.exe'
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $bundlePath
    if ($LASTEXITCODE -ne 0) { throw 'signtool 签名失败' }
    Write-Warning '已用自签证书签名,该包只能本机安装自测,请勿提交到商店。'
}

Write-Host "`n完成:$bundlePath"
