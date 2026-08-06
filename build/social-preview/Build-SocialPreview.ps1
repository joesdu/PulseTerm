<#
.SYNOPSIS
    把 social-preview.html 渲染成 GitHub 社交预览图(Settings → Social preview)。

.DESCRIPTION
    用 Edge(或 Chrome)的 headless 截图渲染,产出同目录的 social-preview.png。
    GitHub 建议 1280×640、上限 1MB;这里默认按 2 倍设备像素渲染成 2560×1280,
    在 GitHub 卡片与社交平台的高分屏预览下更锐利,体积仍远小于上限。
    需要 1 倍尺寸就传 -Scale 1。

.EXAMPLE
    pwsh build/social-preview/Build-SocialPreview.ps1
#>
[CmdletBinding()]
param(
    # 设备像素比:2 → 2560×1280(默认),1 → 1280×640。
    [ValidateSet(1, 2)]
    [int]$Scale = 2,

    # 浏览器可执行文件;默认自动探测 Edge、其次 Chrome。
    [string]$Browser
)

$ErrorActionPreference = 'Stop'

if (-not $Browser) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
    )
    $Browser = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Browser -or -not (Test-Path $Browser)) {
    throw '找不到 Edge/Chrome,请用 -Browser 指定浏览器可执行文件路径。'
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$html = Join-Path $here 'social-preview.html'
$out = Join-Path $here 'social-preview.png'

# --headless=new 才支持整窗截图;--hide-scrollbars 避免右侧多出一条滚动条。
& $Browser --headless=new --disable-gpu --hide-scrollbars `
    --force-device-scale-factor=$Scale --window-size=1280,640 `
    --screenshot="$out" "file:///$($html -replace '\\', '/')" | Out-Null

if (-not (Test-Path $out)) {
    throw "渲染失败,未生成 $out"
}

$size = (Get-Item $out).Length
Add-Type -AssemblyName System.Drawing
$img = [System.Drawing.Image]::FromFile($out)
try {
    "已生成 $out — $($img.Width)×$($img.Height),$([math]::Round($size / 1KB, 1)) KB"
    if ($size -gt 1MB) {
        Write-Warning 'GitHub 社交预览图上限 1MB,当前已超出,请改用 -Scale 1 重新生成。'
    }
}
finally {
    $img.Dispose()
}
