param(
    [string]$OutIco = (Join-Path $PSScriptRoot "assets\app.ico"),
    [string]$OutPreviewPng = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path (Split-Path $OutIco -Parent) | Out-Null

function Render-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size / 256.0
    $pad = 14.0 * $s
    $w = $size - ($pad * 2.0)
    $rad = 54.0 * $s

    # Helper rounded rect path
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $rad * 2.0
    $path.AddArc($pad, $pad, $d, $d, 180, 90)
    $path.AddArc($pad + $w - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $w - $d, $pad + $w - $d, $d, $d, 0, 90)
    $path.AddArc($pad, $pad + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    # Soft drop shadow underneath
    $shPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shY = $pad + 6.0 * $s
    $shPath.AddArc($pad, $shY, $d, $d, 180, 90)
    $shPath.AddArc($pad + $w - $d, $shY, $d, $d, 270, 90)
    $shPath.AddArc($pad + $w - $d, $shY + $w - $d, $d, $d, 0, 90)
    $shPath.AddArc($pad, $shY + $w - $d, $d, $d, 90, 90)
    $shPath.CloseFigure()
    $shBr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(65, 18, 12, 10))
    $g.FillPath($shBr, $shPath)

    # Rich Warm Terracotta -> Deep Coral-Espresso Gradient (Claude-inspired flagship look)
    $rectF = New-Object System.Drawing.RectangleF([float]$pad, [float]$pad, [float]$w, [float]$w)
    $bgGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rectF,
        [System.Drawing.Color]::FromArgb(255, 232, 130, 94),
        [System.Drawing.Color]::FromArgb(255, 174, 71, 42),
        [float]45.0
    )
    $g.FillPath($bgGrad, $path)

    # Subtle radial top-left sheen
    $sheenPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $sheenPath.AddEllipse([float]($pad - 20.0 * $s), [float]($pad - 30.0 * $s), [float](200.0 * $s), [float](160.0 * $s))
    $g.SetClip($path)
    $sheenBr = New-Object System.Drawing.Drawing2D.PathGradientBrush($sheenPath)
    $sheenBr.CenterColor = [System.Drawing.Color]::FromArgb(60, 255, 245, 235)
    $sheenBr.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 245, 235))
    $g.FillPath($sheenBr, $sheenPath)

    # Bottom subtle dark inner vignette
    $botRect = New-Object System.Drawing.RectangleF([float]$pad, [float]($pad + $w * 0.55), [float]$w, [float]($w * 0.45))
    $botGrad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $botRect,
        [System.Drawing.Color]::FromArgb(0, 35, 14, 8),
        [System.Drawing.Color]::FromArgb(70, 35, 14, 8),
        [float]90.0
    )
    $g.FillRectangle($botGrad, $botRect)
    $g.ResetClip()

    # Crisp glass rim border
    $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(95, 255, 242, 232), [float]([Math]::Max(1.0, 3.0 * $s)))
    $g.DrawPath($rimPen, $path)

    # Foreground Emblem: Modern Stylized Layered Translation Cards + Sparkle + Clean "A" Glyph
    $whiteBr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(250, 255, 252, 248))
    $softCardBr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(62, 255, 248, 242))
    $cardStroke = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(215, 255, 248, 242), [float]([Math]::Max(1.2, 6.0 * $s)))
    $cardStroke.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    # Back translucent overlay card (top-right)
    $c2 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $c2x = 110.0 * $s; $c2y = 58.0 * $s; $c2w = 92.0 * $s; $c2h = 84.0 * $s; $c2r = 18.0 * $s
    $c2.AddArc($c2x, $c2y, $c2r * 2, $c2r * 2, 180, 90)
    $c2.AddArc($c2x + $c2w - $c2r * 2, $c2y, $c2r * 2, $c2r * 2, 270, 90)
    $c2.AddArc($c2x + $c2w - $c2r * 2, $c2y + $c2h - $c2r * 2, $c2r * 2, $c2r * 2, 0, 90)
    $c2.AddArc($c2x, $c2y + $c2h - $c2r * 2, $c2r * 2, $c2r * 2, 90, 90)
    $c2.CloseFigure()
    $g.FillPath($softCardBr, $c2)
    $g.DrawPath($cardStroke, $c2)

    # Two sleek text lines inside back card (representing translated subtitle)
    $linePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(245, 255, 252, 248), [float]([Math]::Max(1.4, 9.0 * $s)))
    $linePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $linePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($linePen, [float](132 * $s), [float](90 * $s), [float](182 * $s), [float](90 * $s))
    $g.DrawLine($linePen, [float](132 * $s), [float](114 * $s), [float](166 * $s), [float](114 * $s))

    # Front solid white rounded card (bottom-left)
    $c1 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $c1x = 54.0 * $s; $c1y = 104.0 * $s; $c1w = 104.0 * $s; $c1h = 96.0 * $s; $c1r = 20.0 * $s
    $c1.AddArc($c1x, $c1y, $c1r * 2, $c1r * 2, 180, 90)
    $c1.AddArc($c1x + $c1w - $c1r * 2, $c1y, $c1r * 2, $c1r * 2, 270, 90)
    $c1.AddArc($c1x + $c1w - $c1r * 2, $c1y + $c1h - $c1r * 2, $c1r * 2, $c1r * 2, 0, 90)
    $c1.AddArc($c1x, $c1y + $c1h - $c1r * 2, $c1r * 2, $c1r * 2, 90, 90)
    $c1.CloseFigure()
    $c1ShBr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(55, 60, 20, 8))
    $g.TranslateTransform([float](4 * $s), [float](6 * $s))
    $g.FillPath($c1ShBr, $c1)
    $g.ResetTransform()
    $g.FillPath($whiteBr, $c1)

    # Geometric Terracotta "A" glyph inside front white card
    $glyphPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 186, 78, 46), [float]([Math]::Max(1.6, 12.0 * $s)))
    $glyphPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $glyphPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $glyphPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLine($glyphPen, [float](82 * $s), [float](176 * $s), [float](106 * $s), [float](124 * $s))
    $g.DrawLine($glyphPen, [float](106 * $s), [float](124 * $s), [float](130 * $s), [float](176 * $s))
    $g.DrawLine($glyphPen, [float](90 * $s), [float](160 * $s), [float](122 * $s), [float](160 * $s))

    # Crisp 4-point Claude-style star sparkle at bottom-right
    $spX = 186.0 * $s; $spY = 176.0 * $s; $spR = 20.0 * $s; $spIn = 6.0 * $s
    $starPts = @(
        (New-Object System.Drawing.PointF($spX, ($spY - $spR))),
        (New-Object System.Drawing.PointF(($spX + $spIn), ($spY - $spIn))),
        (New-Object System.Drawing.PointF(($spX + $spR), $spY)),
        (New-Object System.Drawing.PointF(($spX + $spIn), ($spY + $spIn))),
        (New-Object System.Drawing.PointF($spX, ($spY + $spR))),
        (New-Object System.Drawing.PointF(($spX - $spIn), ($spY + $spIn))),
        (New-Object System.Drawing.PointF(($spX - $spR), $spY)),
        (New-Object System.Drawing.PointF(($spX - $spIn), ($spY - $spIn)))
    )
    $g.FillPolygon($whiteBr, $starPts)

    $g.Dispose()
    return $bmp
}

$sizes = @(256, 64, 48, 32, 16)
$pngBuffers = @()
foreach ($sz in $sizes) {
    $bmp = Render-IconBitmap $sz
    if ($sz -eq 256 -and $OutPreviewPng -ne "") {
        $bmp.Save($OutPreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBuffers += ,@($sz, $ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}

# Write multi-resolution Vista+ PNG-encoded ICO
$fs = [System.IO.File]::Open($OutIco, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$pngBuffers.Count)

$offset = 6 + (16 * $pngBuffers.Count)
foreach ($entry in $pngBuffers) {
    $sz = $entry[0]
    $bytes = $entry[1]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach ($entry in $pngBuffers) {
    $bw.Write($entry[1])
}
$bw.Close()
$fs.Close()
Write-Host "Generated multi-res icon: $OutIco"
