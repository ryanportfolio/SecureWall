# Load every output with System.Drawing (.NET Framework, Windows PowerShell 5.1) and build a
# contact sheet at actual size on light and dark backgrounds, plus a 4x nearest-neighbor strip
# of the small sizes. Run with powershell.exe, not pwsh, so the .NET Framework loader is tested.
#   powershell -NoProfile -ExecutionPolicy Bypass -File contact.ps1 [-OutRoot <dir>] [-Sheet <png>]
param(
    [string]$OutRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$Sheet = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.tmp\icons-contact.png')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SwIcoNative {
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint load);
}
'@

"CLR $([Environment]::Version), PowerShell $($PSVersionTable.PSVersion)"
$img = Join-Path $OutRoot 'TinyWall\Resources\img'
$failed = 0

# --- load checks -----------------------------------------------------------------------
$icoFiles = @(
    @{ Path = "$img\firewall.ico"; Sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256 },
    @{ Path = "$OutRoot\MsiSetup\Sources\ProgramFiles\SecureWall\SecureWall.ico"; Sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256 },
    @{ Path = "$img\shield_green_small.ico"; Sizes = 16, 20, 24, 32, 40, 48 },
    @{ Path = "$img\shield_yellow_small.ico"; Sizes = 16, 20, 24, 32, 40, 48 },
    @{ Path = "$img\shield_red_small.ico"; Sizes = 16, 20, 24, 32, 40, 48 },
    @{ Path = "$img\shield_grey_small.ico"; Sizes = 16, 20, 24, 32, 40, 48 },
    @{ Path = "$img\shield_unknown_small.ico"; Sizes = 16, 20, 24, 32, 40, 48 }
)
$icoBitmaps = @{}
foreach ($f in $icoFiles) {
    try {
        $default = New-Object System.Drawing.Icon($f.Path)
        $list = @()
        foreach ($s in $f.Sizes) {
            if ($s -ge 256) {
                # .NET Framework's Icon(path, w, h) matches on the directory's byte-sized width, so a
                # 256 px entry (stored as 0) is never selected; it falls back to 128. Explorer, the
                # compiler's Win32 icon resource and ARP read 256 through Win32, so check it that way.
                $h = [SwIcoNative]::LoadImage([IntPtr]::Zero, $f.Path, 1, $s, $s, 0x10)
                if ($h -eq [IntPtr]::Zero) { throw "Win32 LoadImage($s) failed" }
                $ic = [System.Drawing.Icon]::FromHandle($h)
            } else {
                $ic = New-Object System.Drawing.Icon($f.Path, $s, $s)
            }
            if ($ic.Width -ne $s -or $ic.Height -ne $s) { throw "requested $s, got $($ic.Width)x$($ic.Height)" }
            $bm = $ic.ToBitmap()
            if ($bm.Width -ne $s) { throw "ToBitmap($s) returned $($bm.Width)" }
            # alpha must survive ToBitmap: corner pixel transparent, center opaque
            if ($bm.GetPixel(0, 0).A -ne 0) { throw "size ${s}: corner not transparent after ToBitmap" }
            if ($bm.GetPixel([int]($s / 2), [int]($s / 2)).A -ne 255) { throw "size ${s}: center not opaque after ToBitmap" }
            $list += , $bm
        }
        $icoBitmaps[$f.Path] = $list
        $note = if ($f.Sizes -contains 256) { ' (256 via Win32 LoadImage; .NET Icon(path,256,256) returns 128 by design)' } else { '' }
        "OK   Icon  $(Split-Path -Leaf $f.Path): default $($default.Width)px; loaded $($f.Sizes -join ',') with alpha$note"
    } catch { $failed++; "FAIL Icon  $($f.Path): $_" }
}

$pngNames = 'accept', 'add', 'cancel', 'connections', 'copy', 'delete', 'executable', 'exit', 'export',
    'import', 'info', 'manage', 'modify', 'network_drive_small', 'open_folder', 'process', 'remove',
    'search', 'store', 'uninstall', 'web', 'window', 'windows_small', 'lock_small'
$pngs = [ordered]@{}
foreach ($n in $pngNames + 'lock', 'green_banner', 'blue_banner', 'red_banner') {
    try {
        $bm = New-Object System.Drawing.Bitmap("$img\$n.png")
        $pngs[$n] = $bm
        "OK   Bitmap $n.png: $($bm.Width)x$($bm.Height) $($bm.PixelFormat)"
    } catch { $failed++; "FAIL Bitmap $n.png: $_" }
}
$bmps = [ordered]@{}
foreach ($n in 'banner', 'background') {
    try {
        $bm = New-Object System.Drawing.Bitmap("$OutRoot\MsiSetup\$n.bmp")
        $bmps[$n] = $bm
        "OK   Bitmap $n.bmp: $($bm.Width)x$($bm.Height) $($bm.PixelFormat)"
    } catch { $failed++; "FAIL Bitmap $n.bmp: $_" }
}
if ($failed) { "$failed load failure(s)"; exit 1 }

# --- contact sheet ---------------------------------------------------------------------
$W = 1500; $H = 2300
$sheetBmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($sheetBmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$half = [int]($W / 2)
$light = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
$dark = [System.Drawing.Color]::FromArgb(255, 32, 32, 32)
$g.FillRectangle((New-Object System.Drawing.SolidBrush($light)), 0, 0, $half, $H)
$g.FillRectangle((New-Object System.Drawing.SolidBrush($dark)), $half, 0, $W - $half, $H)
$font = New-Object System.Drawing.Font('Segoe UI', 9)
$bold = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Bold)
$inkL = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 40, 40, 40))
$inkD = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 225, 225, 225))

function Put($bm, $x, $y, $scale = 1) {
    foreach ($ox in 0, $half) {
        $g.DrawImage($bm, [int]($x + $ox), [int]$y, [int]($bm.Width * $scale), [int]($bm.Height * $scale))
    }
}
function Label($text, $x, $y, $f = $font) {
    $g.DrawString($text, $f, $inkL, $x, $y)
    $g.DrawString($text, $f, $inkD, $x + $half, $y)
}

$y = 10
Label 'ICO entries at actual size (System.Drawing Icon(path, w, h).ToBitmap())' 10 $y $bold
$y += 26
foreach ($f in $icoFiles) {
    $name = Split-Path -Leaf $f.Path
    if ($name -eq 'SecureWall.ico') { $name = 'MsiSetup SecureWall.ico' }
    Label $name 10 $y
    $x = 10; $rowH = 0
    foreach ($bm in $icoBitmaps[$f.Path]) {
        Put $bm $x ($y + 18)
        $x += $bm.Width + 8
        if ($bm.Height -gt $rowH) { $rowH = $bm.Height }
    }
    $y += 18 + $rowH + 12
}

Label 'Button and list PNGs, 16 px, actual size' 10 $y $bold
$y += 24
$x = 10
foreach ($n in $pngNames) {
    Put $pngs[$n] $x $y
    $x += 30
}
$y += 30
Label 'Same PNGs, 4x nearest-neighbor (inspection only)' 10 $y $bold
$y += 24
$x = 10
foreach ($n in $pngNames) {
    if ($x + 64 -gt $half - 10) { $x = 10; $y += 74 }
    Put $pngs[$n] $x $y 4
    $x += 72
}
$y += 78
Label 'Tray shields 16/20/24/32 px, 4x nearest-neighbor' 10 $y $bold
$y += 24
$x = 10
foreach ($f in $icoFiles[2..6]) {
    $bms = $icoBitmaps[$f.Path]
    foreach ($k in 0, 1, 2) { Put $bms[$k] $x $y 4; $x += $bms[$k].Width * 4 + 6 }
    $x += 14
    if ($x + 260 -gt $half - 10) { $x = 10; $y += 104 }
}
$y += 104
Label 'lock.png 96 px' 10 $y
Put $pngs['lock'] 10 ($y + 18)
$y += 18 + 96 + 12

# Banners and installer bitmaps are opaque; draw each once, full width, below the panels.
$g.FillRectangle((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 240, 240, 240))), 0, $y, $W, $H - $y)
$y += 8
$g.DrawString('Exception dialog banners 540x48 with sample 14.25 pt white text; installer banner.bmp 493x58 and background.bmp 493x312', $bold, $inkL, 10, $y)
$y += 24
$bx = 10
$title = New-Object System.Drawing.Font('Microsoft Sans Serif', 14.25)
foreach ($n in 'green_banner', 'blue_banner', 'red_banner') {
    $g.DrawImage($pngs[$n], $bx, $y, 540, 48)
    $g.DrawString('Example.exe wants to connect', $title, [System.Drawing.Brushes]::White, $bx + 12, $y + 12)
    $y += 56
}
$g.DrawImage($bmps['banner'], 600, ($y - 168), 493, 58)
$g.DrawImage($bmps['background'], 10, $y + 4, 493, 312)
$g.DrawImage($bmps['background'], 520, $y + 4, 493, 312)
# Mimic WiX dialog text on the white area of the second copy to show where text lands.
$g.DrawString('Welcome to the SecureWall Setup Wizard', (New-Object System.Drawing.Font('Tahoma', 12, [System.Drawing.FontStyle]::Bold)), [System.Drawing.Brushes]::Black, 520 + 180, $y + 24)
$y += 320

$g.Dispose()
$dir = Split-Path -Parent $Sheet
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$sheetBmp.Save($Sheet, [System.Drawing.Imaging.ImageFormat]::Png)
"contact sheet: $Sheet ($W x $H, content ends at y=$y)"
