# Generate ImagePeek.ico (multi-size: 16/24/32/48/64/128/256)
# Design: blue gradient rounded square + white photo card (mountains + sun) + magnifier
param([string]$OutPath = "$PSScriptRoot\ImagePeek.ico")

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size)
{
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $u = $size / 16.0

    # rounded background (blue gradient)
    $bgPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $m = 0.6 * $u
    $rad = 3.6 * $u
    $x0 = $m; $y0 = $m; $x1 = $size - $m; $y1 = $size - $m
    $bgPath.AddArc($x0, $y0, 2*$rad, 2*$rad, 180, 90)
    $bgPath.AddArc($x1 - 2*$rad, $y0, 2*$rad, 2*$rad, 270, 90)
    $bgPath.AddArc($x1 - 2*$rad, $y1 - 2*$rad, 2*$rad, 2*$rad, 0, 90)
    $bgPath.AddArc($x0, $y1 - 2*$rad, 2*$rad, 2*$rad, 90, 90)
    $bgPath.CloseFigure()
    $bgBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new(0, $size)),
        [System.Drawing.Color]::FromArgb(255, 79, 141, 249),
        [System.Drawing.Color]::FromArgb(255, 24, 72, 190))
    $g.FillPath($bgBrush, $bgPath)

    # white photo card
    $cardX = 2.4*$u; $cardY = 3.2*$u; $cardW = 11.2*$u; $cardH = 8.6*$u
    $cardRad = 1.2*$u
    $cardPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $cardPath.AddArc($cardX, $cardY, 2*$cardRad, 2*$cardRad, 180, 90)
    $cardPath.AddArc($cardX + $cardW - 2*$cardRad, $cardY, 2*$cardRad, 2*$cardRad, 270, 90)
    $cardPath.AddArc($cardX + $cardW - 2*$cardRad, $cardY + $cardH - 2*$cardRad, 2*$cardRad, 2*$cardRad, 0, 90)
    $cardPath.AddArc($cardX, $cardY + $cardH - 2*$cardRad, 2*$cardRad, 2*$cardRad, 90, 90)
    $cardPath.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $cardPath)

    # card content: sky + sun + mountains (clipped to card)
    $g.SetClip($cardPath)
    $skyBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        ([System.Drawing.Point]::new(0, $cardY)), ([System.Drawing.Point]::new(0, ($cardY + $cardH))),
        [System.Drawing.Color]::FromArgb(255, 205, 232, 255),
        [System.Drawing.Color]::FromArgb(255, 240, 249, 255))
    $g.FillRectangle($skyBrush, $cardX, $cardY, $cardW, $cardH)

    if ($size -ge 32)
    {
        $sunR = 1.15*$u
        $sunBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 201, 60))
        $g.FillEllipse($sunBrush, 10.6*$u - $sunR, 5.2*$u - $sunR, 2*$sunR, 2*$sunR)
    }

    $p1 = @( ([System.Drawing.PointF]::new(2.4*$u, 11.8*$u)),
             ([System.Drawing.PointF]::new(6.6*$u, 6.2*$u)),
             ([System.Drawing.PointF]::new(10.8*$u, 11.8*$u)) )
    $b1 = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 59, 111, 212))
    $g.FillPolygon($b1, $p1)
    $p2 = @( ([System.Drawing.PointF]::new(7.6*$u, 11.8*$u)),
             ([System.Drawing.PointF]::new(10.6*$u, 8.0*$u)),
             ([System.Drawing.PointF]::new(13.6*$u, 11.8*$u)) )
    $b2 = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 111, 161, 232))
    $g.FillPolygon($b2, $p2)
    $g.ResetClip()

    # magnifier (preview motif)
    if ($size -ge 24)
    {
        $cx = 10.9*$u; $cy = 10.1*$u; $lr = 2.5*$u
        $lensPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 1.25*$u)
        $lensBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
        $g.FillEllipse($lensBrush, $cx - $lr, $cy - $lr, 2*$lr, 2*$lr)
        $g.DrawEllipse($lensPen, $cx - $lr, $cy - $lr, 2*$lr, 2*$lr)
        $handlePen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 1.7*$u)
        $handlePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $handlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($handlePen, $cx + $lr*0.72, $cy + $lr*0.72, $cx + $lr*1.75, $cy + $lr*1.75)
    }

    $g.Dispose()
    return $bmp
}

function ConvertTo-IcoDib([System.Drawing.Bitmap]$bmp)
{
    $w = $bmp.Width; $h = $bmp.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = [byte[]]::new($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $xorSize = $stride * $h
    $andStride = [int]([Math]::Ceiling($w / 8.0 / 4.0) * 4)
    $andSize = $andStride * $h
    $out = [byte[]]::new(40 + $xorSize + $andSize)

    $out[0] = 40
    [BitConverter]::GetBytes([int]$w).CopyTo($out, 4)
    [BitConverter]::GetBytes([int]($h * 2)).CopyTo($out, 8)
    $out[12] = 1
    $out[14] = 32
    for ($y = 0; $y -lt $h; $y++)
    {
        $srcY = $h - 1 - $y
        [Array]::Copy($pixels, ($srcY * $stride), $out, (40 + $y * $stride), $stride)
    }
    return ,$out
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
$entries = @()
$offset = 6 + 16 * $sizes.Count

foreach ($s in $sizes)
{
    $bmp = New-IconBitmap $s
    if ($s -ge 256)
    {
        $ms = [System.IO.MemoryStream]::new()
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += ,$ms.ToArray()
        $entries += @{ W = $s; H = $s; Len = $ms.Length; Off = $offset }
        $offset += $ms.Length
    }
    else
    {
        $dib = ConvertTo-IcoDib $bmp
        $images += ,$dib
        $entries += @{ W = $s; H = $s; Len = $dib.Length; Off = $offset }
        $offset += $dib.Length
    }
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($OutPath)
$bw = [System.IO.BinaryWriter]::new($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
foreach ($e in $entries)
{
    $wb = 0; if ($e.W -lt 256) { $wb = $e.W }
    $hb = 0; if ($e.H -lt 256) { $hb = $e.H }
    $bw.Write([byte]$wb); $bw.Write([byte]$hb)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$e.Len); $bw.Write([uint32]$e.Off)
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Dispose(); $fs.Dispose()

Write-Host ("ICO generated: " + $OutPath + " (" + (Get-Item $OutPath).Length + " bytes, " + $sizes.Count + " sizes)")
