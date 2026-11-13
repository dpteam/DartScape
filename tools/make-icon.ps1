<#
    DartScape icon/logo generator.
    Produces a Windows-XP-friendly .ico (BMP/DIB entries, no PNG compression)
    plus PNG assets used by the launcher and the web front-end.

    Usage:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$OutDir
)

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrEmpty($OutDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $OutDir = Join-Path $scriptDir '..\assets\icon'
}
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

function New-RuneArt([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$size

    # --- background: dark slate rounded square with a vertical gradient -------
    $pad = $s * 0.03
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad))
    $radius = $s * 0.20
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF(0, $s)),
        [System.Drawing.Color]::FromArgb(255, 14, 26, 43),
        [System.Drawing.Color]::FromArgb(255, 30, 58, 92))
    $g.FillPath($bg, $path)

    # inner rim
    $rim = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(190, 214, 170, 74), [float]([math]::Max(1.0, $s * 0.018)))
    $inner = New-Object System.Drawing.RectangleF(($pad + $s * 0.055), ($pad + $s * 0.055), ($s - 2 * ($pad + $s * 0.055)), ($s - 2 * ($pad + $s * 0.055)))
    $r2 = $radius * 0.82
    $p2 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d2 = $r2 * 2
    $p2.AddArc($inner.X, $inner.Y, $d2, $d2, 180, 90)
    $p2.AddArc($inner.Right - $d2, $inner.Y, $d2, $d2, 270, 90)
    $p2.AddArc($inner.Right - $d2, $inner.Bottom - $d2, $d2, $d2, 0, 90)
    $p2.AddArc($inner.X, $inner.Bottom - $d2, $d2, $d2, 90, 90)
    $p2.CloseFigure()
    $g.DrawPath($rim, $p2)

    # --- dragon-ish "D" mark --------------------------------------------------
    # A heavy gold "D" with a sword-blade counter-stroke: reads at 16px too.
    $gold = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($s, $s)),
        [System.Drawing.Color]::FromArgb(255, 246, 214, 120),
        [System.Drawing.Color]::FromArgb(255, 196, 138, 38))
    $edge = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(220, 90, 60, 16), [float]([math]::Max(0.8, $s * 0.012)))

    # Geometry of the "D": a straight vertical stem plus a half-ellipse bowl.
    #   stem : x = left .. left+stemW, full height
    #   bowl : half-ellipse of horizontal radius rh starting at x = left+stemW
    $stemW = $s * 0.125
    $left = $s * 0.265
    $top = $s * 0.235
    $bottom = $s * 0.765
    $rh = $s * 0.175                       # horizontal radius of the bowl
    $bowH = $bottom - $top                 # bowl height (vertical diameter)
    $xb = $left + $stemW                   # bowl starts here

    $outer = New-Object System.Drawing.Drawing2D.GraphicsPath
    $outer.AddArc($xb, $top, ($rh * 2), $bowH, 270, 180)
    $outer.AddLine($left, $bottom, $left, $top)
    $outer.CloseFigure()

    # counter (the hole inside the D)
    $ih = $stemW * 0.95                    # stroke thickness of the bowl
    $rh2 = $rh - $ih
    $holePath = New-Object System.Drawing.Drawing2D.GraphicsPath
    if ($rh2 -gt 0.5) {
        $holePath.AddArc(($xb + $ih), ($top + $ih), ($rh2 * 2), ($bowH - 2 * $ih), 270, 180)
        $holePath.AddLine(($left + $stemW), ($bottom - $ih), ($left + $stemW), ($top + $ih))
        $holePath.CloseFigure()
    }

    $outer.FillMode = [System.Drawing.Drawing2D.FillMode]::Alternate
    $region = New-Object System.Drawing.Region($outer)
    if ($rh2 -gt 0.5) { $region.Exclude($holePath) }
    $g.FillRegion($gold, $region)
    $g.DrawPath($edge, $outer)
    if ($rh2 -gt 0.5) { $g.DrawPath($edge, $holePath) }

    # --- glint on the stem (a short bevel highlight, reads even at 16px) -----
    if ($s -ge 32) {
        $gl = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 255, 246, 214), [float]([math]::Max(1.0, $s * 0.016)))
        $g.DrawLine($gl, [float]($left + $stemW * 0.28), [float]($top + $s * 0.045), [float]($left + $stemW * 0.28), [float]($bottom - $s * 0.045))
        $gl.Dispose()
    }

    # --- tiny star sparkle at the bowl's top-right ---------------------------
    if ($s -ge 48) {
        $cx = $left + $stemW + $rh * 1.28
        $cy = $top + $bowH * 0.22
        $r = $s * 0.052
        $sp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $sp.AddPolygon(@(
            (New-Object System.Drawing.PointF([float]$cx, [float]($cy - $r))),
            (New-Object System.Drawing.PointF([float]($cx + $r * 0.28), [float]($cy - $r * 0.28))),
            (New-Object System.Drawing.PointF([float]($cx + $r), [float]$cy)),
            (New-Object System.Drawing.PointF([float]($cx + $r * 0.28), [float]($cy + $r * 0.28))),
            (New-Object System.Drawing.PointF([float]$cx, [float]($cy + $r))),
            (New-Object System.Drawing.PointF([float]($cx - $r * 0.28), [float]($cy + $r * 0.28))),
            (New-Object System.Drawing.PointF([float]($cx - $r), [float]$cy)),
            (New-Object System.Drawing.PointF([float]($cx - $r * 0.28), [float]($cy - $r * 0.28)))
        ))
        $spFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(240, 255, 252, 235))
        $g.FillPath($spFill, $sp)
        $spFill.Dispose(); $sp.Dispose()
    }

    $g.Dispose()
    return $bmp
}

function Get-IcoBytes([System.Drawing.Bitmap[]]$images) {
    # ICO container with uncompressed 32bpp BMP (DIB) payloads => Windows XP safe.
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $count = $images.Count
    $bw.Write([uint16]0)          # reserved
    $bw.Write([uint16]1)          # type = icon
    $bw.Write([uint16]$count)

    $payloads = @()
    foreach ($img in $images) {
        $w = $img.Width; $h = $img.Height
        $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
        $data = $img.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stride = $data.Stride
        $buf = New-Object byte[] ($stride * $h)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
        $img.UnlockBits($data)

        $p = New-Object System.IO.MemoryStream
        $pb = New-Object System.IO.BinaryWriter($p)
        # BITMAPINFOHEADER (height doubled: XOR + AND masks)
        $pb.Write([uint32]40)
        $pb.Write([int32]$w)
        $pb.Write([int32]($h * 2))
        $pb.Write([uint16]1)
        $pb.Write([uint16]32)
        $pb.Write([uint32]0)
        $pb.Write([uint32]($w * $h * 4))
        $pb.Write([int32]0); $pb.Write([int32]0)
        $pb.Write([uint32]0); $pb.Write([uint32]0)
        # bottom-up BGRA rows
        for ($y = $h - 1; $y -ge 0; $y--) {
            $row = $y * $stride
            for ($x = 0; $x -lt $w; $x++) {
                $i = $row + $x * 4
                $pb.Write($buf[$i + 2]) # B
                $pb.Write($buf[$i + 1]) # G
                $pb.Write($buf[$i + 0]) # R
                $pb.Write($buf[$i + 3]) # A
            }
        }
        # AND mask: all zero (alpha channel does the work)
        $maskStride = [int]([math]::Ceiling($w / 32.0) * 4)
        $pb.Write((New-Object byte[] ($maskStride * $h)))
        $pb.Flush()
        $payloads += , $p.ToArray()
        $pb.Dispose(); $p.Dispose()
    }

    $offset = 6 + 16 * $count
    for ($i = 0; $i -lt $count; $i++) {
        $img = $images[$i]
        $w = $img.Width; $h = $img.Height
        $bw.Write([byte]$(if ($w -ge 256) { 0 } else { $w }))
        $bw.Write([byte]$(if ($h -ge 256) { 0 } else { $h }))
        $bw.Write([byte]0)   # palette
        $bw.Write([byte]0)   # reserved
        $bw.Write([uint16]1) # planes
        $bw.Write([uint16]32)
        $bw.Write([uint32]$payloads[$i].Length)
        $bw.Write([uint32]$offset)
        $offset += $payloads[$i].Length
    }
    foreach ($pl in $payloads) { $bw.Write($pl) }
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return $bytes
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($sz in $sizes) {
    $bmp = New-RuneArt $sz
    $images += , $bmp
    $bmp.Save((Join-Path $OutDir ("dartscape_{0}.png" -f $sz)), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host ("  rendered {0}x{0}" -f $sz)
}

$ico = Get-IcoBytes $images
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'dartscape.ico'), $ico)

# Big logo for README / web header
$logo = New-RuneArt 512
$logo.Save((Join-Path $OutDir 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)

foreach ($b in $images) { $b.Dispose() }
$logo.Dispose()

Write-Host ("ICO  : {0} ({1:N0} bytes)" -f (Join-Path $OutDir 'dartscape.ico'), $ico.Length)
Write-Host ("LOGO : {0}" -f (Join-Path $OutDir 'logo.png'))
