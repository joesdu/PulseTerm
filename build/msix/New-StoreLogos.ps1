<#
.SYNOPSIS
    从应用图标生成 Partner Center 商店listing 用的应用磁贴图标(300x300 / 150x150 / 71x71)。

.DESCRIPTION
    这三张图是**商店listing 资产**,由人工在 Partner Center 的「应用商店列表 → 应用商店徽标」
    上传,与打进 MSIX 包里的磁贴图标(Square150x150Logo 等,由 Build-Msix.ps1 生成)是两码事:
    前者决定商店页面上的展示,后者决定装到本机后开始菜单/任务栏的样子。两边尺寸有重叠,
    但用途不同,不要混用。

    源图带 Alpha 通道(圆角方形四角透明),缩放全程保留透明度。
    默认先裁掉四周完全透明的边,让图形填满画布:源图那圈留白是图标文件的封装边距,
    而磁贴资产本身就该占满画框,71x71 这种小尺寸下留白会明显吃掉可视面积。
    确实需要保留原始留白(例如商店容器会自行裁角)时加 -KeepPadding。

.EXAMPLE
    pwsh build/msix/New-StoreLogos.ps1
    pwsh build/msix/New-StoreLogos.ps1 -KeepPadding
#>
[CmdletBinding()]
param(
    [string]$SourceImage,
    [string]$OutputDirectory,
    [int[]]$Sizes = @(300, 150, 71),

    # 保留源图四周的透明边距(默认裁掉,使图形填满画布)。
    [switch]$KeepPadding
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $SourceImage) { $SourceImage = Join-Path $repoRoot 'src\VelaShell\Assets\velashell.png' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'store-logos' }

function Get-OpaqueBounds {
    <# 求非全透明像素的外接矩形。逐像素 GetPixel 很慢,但 1024x1024 只跑一次,够用。 #>
    param([System.Drawing.Bitmap]$Bitmap)

    $minX = $Bitmap.Width; $minY = $Bitmap.Height; $maxX = -1; $maxY = -1
    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -gt 0) {
                if ($x -lt $minX) { $minX = $x }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($y -gt $maxY) { $maxY = $y }
            }
        }
    }
    if ($maxX -lt 0) { return $null }
    return New-Object System.Drawing.Rectangle($minX, $minY, ($maxX - $minX + 1), ($maxY - $minY + 1))
}

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$source = [System.Drawing.Bitmap]::new((Resolve-Path $SourceImage).Path)
try {
    Write-Host "源图: $($source.Width)x$($source.Height)  →  $OutputDirectory"

    $crop = New-Object System.Drawing.Rectangle(0, 0, $source.Width, $source.Height)
    if (-not $KeepPadding) {
        $bounds = Get-OpaqueBounds -Bitmap $source
        if ($bounds) {
            $crop = $bounds
            Write-Host "裁边: $($crop.Width)x$($crop.Height) @ ($($crop.X),$($crop.Y))"
        }
    }

    foreach ($size in $Sizes) {
        $destination = Join-Path $OutputDirectory "StoreLogo-${size}x${size}.png"
        $bitmap = New-Object System.Drawing.Bitmap($size, $size)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                # 等比缩放居中:源图是正方形时正好铺满,不会变形。
                $scale = [Math]::Min($size / $crop.Width, $size / $crop.Height)
                $w = [int][Math]::Round($crop.Width * $scale)
                $h = [int][Math]::Round($crop.Height * $scale)
                $target = New-Object System.Drawing.Rectangle(
                    [int](($size - $w) / 2), [int](($size - $h) / 2), $w, $h)
                $graphics.DrawImage($source, $target, $crop, [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally { $graphics.Dispose() }
            $bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
            $kb = [math]::Round((Get-Item $destination).Length / 1KB, 1)
            Write-Host ("  {0,-28} {1,6} KB" -f (Split-Path $destination -Leaf), $kb)
        }
        finally { $bitmap.Dispose() }
    }
}
finally { $source.Dispose() }
