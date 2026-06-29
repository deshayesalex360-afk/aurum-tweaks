<#
    generate-app-icon.ps1 - Aurum Tweaks brand icon generator.

    Renders the gold "A" monogram on a dark "marble" tile at every shell size and packs them into a
    single multi-resolution app.ico (PNG-in-ICO, Vista+). Kept in the repo so the icon is reproducible
    and its provenance is honest - no opaque binary blob smuggled into source control.

    Brand: base dark #08080A, gold #F59E0B with a metallic top-to-bottom sheen (#FFE9B0 -> #C9780A).

    Run:  powershell -ExecutionPolicy Bypass -File build\icon\generate-app-icon.ps1
    Out:  src\AurumTweaks\Assets\app.ico  (+ build\icon\preview-256.png for eyeballing)
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Repo-relative paths so the script is location-independent (build\icon -> repo root is two up).
$repoRoot  = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$assetsDir = Join-Path $repoRoot 'src\AurumTweaks\Assets'
$outIco    = Join-Path $assetsDir 'app.ico'
$outPng    = Join-Path $PSScriptRoot 'preview-256.png'
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null }

# --- brand palette ---------------------------------------------------------
$bgTop     = [System.Drawing.Color]::FromArgb(255, 0x12, 0x12, 0x18)  # subtle lift at the top edge
$bgBottom  = [System.Drawing.Color]::FromArgb(255, 0x05, 0x05, 0x07)  # near-black floor
$border    = [System.Drawing.Color]::FromArgb(70,  0xF5, 0x9E, 0x0B)  # faint gold frame
$goldTop   = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xE9, 0xB0)  # bright highlight
$goldMid   = [System.Drawing.Color]::FromArgb(255, 0xF5, 0xA6, 0x23)  # brand gold body
$goldLow   = [System.Drawing.Color]::FromArgb(255, 0xC9, 0x78, 0x0A)  # deep amber base

$sizes = @(16, 24, 32, 48, 64, 128, 256)

# Pick a clean geometric face for the monogram; fall back to the generic sans if Arial is absent.
try   { $family = New-Object System.Drawing.FontFamily('Arial') }
catch { $family = [System.Drawing.FontFamily]::GenericSansSerif }
$boldStyle = [int][System.Drawing.FontStyle]::Bold

function New-RoundedRectPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$radius) {
    $d = $radius * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x,            $y,            $d, $d, 180, 90)
    $p.AddArc($x + $w - $d,  $y,            $d, $d, 270, 90)
    $p.AddArc($x + $w - $d,  $y + $h - $d,  $d, $d,   0, 90)
    $p.AddArc($x,            $y + $h - $d,  $d, $d,  90, 90)
    $p.CloseFigure()
    return $p
}

function Render-Tile([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- dark tile with transparent rounded corners ---
    $pad    = [single]([Math]::Round($size * 0.05))
    $x      = $pad; $y = $pad
    $w      = [single]($size - 2 * $pad); $h = [single]($size - 2 * $pad)
    $radius = [single]($size * 0.21)
    $tile   = New-RoundedRectPath $x $y $w $h $radius
    $bgRect = New-Object System.Drawing.RectangleF($x, $y, $w, $h)
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bgRect, $bgTop, $bgBottom, 90.0)
    $g.FillPath($bgBrush, $tile)
    if ($size -ge 32) {
        $pen = New-Object System.Drawing.Pen($border, [single]([Math]::Max(1.0, $size * 0.012)))
        $g.DrawPath($pen, $tile)
        $pen.Dispose()
    }
    $bgBrush.Dispose()

    # --- gold "A" monogram, box-fitted so it fills consistently regardless of font metrics ---
    $glyph = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fmt   = [System.Drawing.StringFormat]::GenericDefault
    $glyph.AddString('A', $family, $boldStyle, 100.0, (New-Object System.Drawing.PointF(0, 0)), $fmt)
    $gb = $glyph.GetBounds()

    $tx = [single]($size * 0.18); $ty = [single]($size * 0.15)
    $tw = [single]($size * 0.64); $th = [single]($size * 0.70)
    $scale = [single]([Math]::Min($tw / $gb.Width, $th / $gb.Height))

    $m = New-Object System.Drawing.Drawing2D.Matrix
    $m.Translate([single]($tx + $tw / 2), [single]($ty + $th / 2))                 # move to target centre
    $m.Scale($scale, $scale)                                                       # size to fit
    $m.Translate([single](-($gb.X + $gb.Width / 2)), [single](-($gb.Y + $gb.Height / 2)))  # origin = glyph centre
    $glyph.Transform($m)
    $m.Dispose()

    $ab = $glyph.GetBounds()
    $goldBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($ab, $goldTop, $goldLow, 90.0)
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend(3)
    $blend.Colors    = @($goldTop, $goldMid, $goldLow)
    $blend.Positions = @([single]0.0, [single]0.5, [single]1.0)
    $goldBrush.InterpolationColors = $blend
    $g.FillPath($goldBrush, $glyph)
    $goldBrush.Dispose()
    $glyph.Dispose()
    $tile.Dispose()
    $g.Dispose()
    return $bmp
}

# Render every size and capture its PNG encoding.
$frames = @()
foreach ($s in $sizes) {
    $bmp = Render-Tile $s
    $ms  = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save($outPng, [System.Drawing.Imaging.ImageFormat]::Png) }  # eyeball preview
    $frames += [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

# --- pack the ICO: ICONDIR + N x ICONDIRENTRY + concatenated PNG payloads ---
$icoStream = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($icoStream)
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]$frames.Count)     # image count
$offset = [UInt32](6 + 16 * $frames.Count)
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }   # 0 encodes 256 in the 1-byte field
    $bw.Write([Byte]$dim)            # width
    $bw.Write([Byte]$dim)            # height (square)
    $bw.Write([Byte]0)               # palette colour count (0 = none)
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # colour planes
    $bw.Write([UInt16]32)            # bits per pixel
    $bw.Write([UInt32]$f.Bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += [UInt32]$f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($outIco, $icoStream.ToArray())
$bw.Dispose(); $icoStream.Dispose()

$total = (Get-Item $outIco).Length
Write-Host ("app.ico written: {0} sizes [{1}], {2:N0} bytes -> {3}" -f $frames.Count, ($sizes -join ','), $total, $outIco)
Write-Host ("preview: {0}" -f $outPng)
