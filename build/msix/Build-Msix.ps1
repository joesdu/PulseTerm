<#
.SYNOPSIS
    把 VelaShell 打成上架 Microsoft Store 用的 MSIX 包(双架构 .msixbundle)。

.DESCRIPTION
    商店版与便携版共用同一份源码和同一条发布命令,差异全在运行时判定(见 Services/AppPackaging.cs):
    装成 MSIX 就自动关掉应用内自更新,改由商店接管。因此这里不需要任何特殊的编译配置。

    发布命令与便携版已完全一致(2026-08-12 起主程序不再单文件发布,两边都是摊开的
    self-contained 产物);差异只剩容器:这里打进 MSIX,那边压成 zip/tar.gz。
    摊开发布对商店还有个额外好处:差量更新只传改动过的文件。

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

function Get-SdkToolPath {
    <# 在最新一版 Windows SDK 的 x64 目录里找工具(makeappx / makepri / signtool)。 #>
    param([Parameter(Mandatory = $true)][string]$Name)

    $sdkBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (-not (Test-Path $sdkBin)) {
        throw "找不到 Windows SDK($sdkBin)。请安装 Windows 10/11 SDK 后重试。"
    }
    $versions = Get-ChildItem $sdkBin -Directory |
        Where-Object { $_.Name -like '10.*' } |
        Sort-Object { [version]($_.Name) } -Descending
    foreach ($dir in $versions) {
        $candidate = Join-Path $dir.FullName "x64\$Name"
        if (Test-Path $candidate) { return $candidate }
    }
    throw "在 $sdkBin 下找不到 $Name。"
}

function Get-OpaqueBounds {
    <#  求非全透明像素的外接矩形。源图四周有一圈透明封装边距,targetsize-* 资产必须裁掉它
        (见 New-MsixAssets 里的说明)。逐像素扫一次 1024x1024,用 LockBits 直接读字节,
        GetPixel 逐点调用在这个尺寸下慢到不可接受。 #>
    param([System.Drawing.Bitmap]$Bitmap)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $Bitmap.Width, $Bitmap.Height)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $bytes = New-Object byte[] ($data.Stride * $Bitmap.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
    }
    finally { $Bitmap.UnlockBits($data) }

    $minX = $Bitmap.Width; $minY = $Bitmap.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        $row = $y * $data.Stride
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($bytes[$row + $x * 4 + 3] -gt 0) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { return $rect }
    return New-Object System.Drawing.Rectangle($minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1))
}

function New-ScaledPng {
    <#  等比缩放并居中放到透明画布上(宽磁贴 310x150 与方形图标共用这一套)。
        -Trim 先裁掉源图四周的全透明边距,让图形铺满画布。 #>
    param([string]$Source, [string]$Destination, [int]$Width, [int]$Height, [switch]$Trim)

    Add-Type -AssemblyName System.Drawing
    $image = [System.Drawing.Bitmap]::new($Source)
    try {
        $crop = if ($Trim) { Get-OpaqueBounds -Bitmap $image }
                else { New-Object System.Drawing.Rectangle(0, 0, $image.Width, $image.Height) }
        $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $scale = [Math]::Min($Width / $crop.Width, $Height / $crop.Height)
                $w = [int][Math]::Round($crop.Width * $scale)
                $h = [int][Math]::Round($crop.Height * $scale)
                $target = New-Object System.Drawing.Rectangle(
                    [int](($Width - $w) / 2), [int](($Height - $h) / 2), $w, $h)
                $graphics.DrawImage($image, $target, $crop, [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally { $graphics.Dispose() }
            $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally { $bitmap.Dispose() }
    }
    finally { $image.Dispose() }
}

function New-MsixAssets {
    <#  生成 manifest 引用到的全部图标。
        任务栏/开始菜单不用 manifest 里写的那张 Square44x44Logo.png,而是按限定符挑
        Square44x44Logo.targetsize-<N>[_altform-...].png:
          无 altform          —— 带底板(plated)。系统在图标后面垫一块 BackgroundColor 的底,
                                 BackgroundColor="transparent" 时垫的是用户的主题色,
                                 于是任务栏上就是一块纯色方块(issue #135 那张截图里的蓝底)。
          altform-unplated    —— 无底板,深色任务栏用。这才是任务栏想要的那张。
          altform-lightunplated —— 无底板,浅色任务栏用。缺它时浅色主题下会退回 plated。
        三种必须成套给:只给 unplated 而没有对应的 plated targetsize,系统会当这一档不存在。
        另外 targetsize-* 按规范不留边距(底板/圆角由系统加),所以这些用 -Trim 裁掉源图
        那圈封装留白铺满画布;Square*/Wide* 磁贴反过来需要留白,保持原样。 #>
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
        foreach ($form in @('', '_altform-unplated', '_altform-lightunplated')) {
            New-ScaledPng -Source $sourceIcon -Trim `
                          -Destination (Join-Path $AssetsDirectory "Square44x44Logo.targetsize-$size$form.png") `
                          -Width $size -Height $size
        }
    }
    New-ScaledPng -Source $sourceIcon -Destination (Join-Path $AssetsDirectory 'Wide310x150Logo.png') `
                  -Width 310 -Height 150
}

function New-ResourcesPri {
    <#  生成 resources.pri —— 没有它,上面那一整套 targetsize-*/altform-* 全是死文件:
        文件名里的限定符只有通过 PRI 索引才会被解析,包里没有 PRI 时系统只认 manifest 里
        写死的那个路径(Assets\Square44x44Logo.png),于是任务栏拿到的永远是 plated 那张。
        makeappx pack 不会自动生成 PRI,必须显式跑一遍 makepri。

        createconfig 生成的默认配置里带 <packaging>/<autoResourcePackage>,会把按 Language/Scale
        限定的资源拆成独立的 resources.<qualifier>.pri 资源包。我们是单包提交(bundle 里只有
        架构分片,没有资源分片),拆出去的那部分谁也不会打进包,所以把 <packaging> 整段删掉。 #>
    param([string]$LayoutDirectory, [string]$ConfigPath)

    $makepri = Get-SdkToolPath 'makepri.exe'
    & $makepri createconfig /cf $ConfigPath /dq en-US /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'makepri createconfig 失败' }

    $config = [xml](Get-Content $ConfigPath -Raw)
    $packaging = $config.SelectSingleNode('//packaging')
    if ($packaging) { $packaging.ParentNode.RemoveChild($packaging) | Out-Null }
    $config.Save($ConfigPath)

    # /pr 下有 AppxManifest.xml 时 makepri 自动取包标识,不必再传 /in。
    & $makepri new /pr $LayoutDirectory /cf $ConfigPath /of (Join-Path $LayoutDirectory 'resources.pri') /o
    if ($LASTEXITCODE -ne 0) { throw 'makepri new 失败' }
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
$makeappx = Get-SdkToolPath 'makeappx.exe'
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

    # 与便携版同一条命令(摊开的 self-contained 产物);makeappx 稍后递归打包整个 layout,
    # 因此 plugins/<id>/ 与 VelaShell.PluginHost 会一并进包,无需在这里另作处理。
    & dotnet publish $projectPath -c Release -r $rid -o $layout `
        -p:Version=$Version -p:SelfContained=true `
        -p:DebugType=None --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败:$rid" }

    Get-ChildItem $layout -Recurse -File -Filter '*.pdb' | Remove-Item -Force

    New-MsixAssets -AssetsDirectory (Join-Path $layout 'Assets')
    New-AppxManifest -Destination (Join-Path $layout 'AppxManifest.xml') `
                     -MsixVersion $msixVersion -Architecture $arch
    # 配置文件放在 layout 之外:留在里面会被 makeappx 一并打进包。
    New-ResourcesPri -LayoutDirectory $layout `
                     -ConfigPath (Join-Path $intermediateRoot "priconfig-$rid.xml")

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
    $signtool = Get-SdkToolPath 'signtool.exe'
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $bundlePath
    if ($LASTEXITCODE -ne 0) { throw 'signtool 签名失败' }
    Write-Warning '已用自签证书签名,该包只能本机安装自测,请勿提交到商店。'
}

Write-Host "`n完成:$bundlePath"
