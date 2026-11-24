param(
    [string]$OutIco = (Join-Path $PSScriptRoot "assets\app.ico"),
    [string]$OutPreviewPng = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

New-Item -ItemType Directory -Force -Path (Split-Path $OutIco -Parent) | Out-Null

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$rad) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($rad * 2.0, [Math]::Min($w, $h))
    if ($d -le 1.0) {
        $p.AddRectangle((New-Object System.Drawing.RectangleF($x, $y, $w, $h)))
        return $p
    }
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size / 256.0
    $pad = 12.0 * $s
    $w = $size - ($pad * 2.0)
    $rad = 48.0 * $s

    # 1. Outer drop shadow
    $shPath = New-RoundedRectPath $pad ($pad + 5.0 * $s) $w $w $rad
    $shBr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 0, 0, 0))
    $g.FillPath($shBr, $shPath)

    # 2. Matte Obsidian Tactical Shield Badge (#0E1014) — Zero AI-slop neon gradients!
    $basePath = New-RoundedRectPath $pad $pad $w $w $rad
    $baseBr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 14, 16, 20))
    $g.FillPath($baseBr, $basePath)

    # Subtle architectural diagonal hatch inside the badge
    $g.SetClip($basePath)
    if ($size -ge 32) {
        $hatchPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(24, 255, 255, 255), [float]([Math]::Max(1.0, 2.0 * $s)))
        for ($i = -256; $i -le 512; $i += 22) {
            $g.DrawLine($hatchPen, [float]($i * $s), [float](256.0 * $s), [float](($i + 256) * $s), [float]0.0)
        }
        $hatchPen.Dispose()
    }
    # Subtle dark inner plate ring
    $innerPlate = New-RoundedRectPath ($pad + 14.0 * $s) ($pad + 14.0 * $s) ($w - 28.0 * $s) ($w - 28.0 * $s) (36.0 * $s)
    $innerBr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 20, 23, 29))
    $g.FillPath($innerBr, $innerPlate)
    $g.ResetClip()

    # Crisp metallic slate rim
    $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 58, 66, 78), [float]([Math]::Max(1.2, 4.5 * $s)))
    $g.DrawPath($rimPen, $basePath)

    # 3. Four Cinema-Subtitle-Yellow (#FFD23F) Tactical HUD Viewfinder Corners (Meaning: Screen OCR Lens)
    $yellow = [System.Drawing.Color]::FromArgb(255, 255, 210, 63)
    $teal   = [System.Drawing.Color]::FromArgb(255, 61, 219, 192)
    $ivory  = [System.Drawing.Color]::FromArgb(255, 244, 241, 234)
    $ink    = [System.Drawing.Color]::FromArgb(255, 14, 16, 20)

    $hudPen = New-Object System.Drawing.Pen($yellow, [float]([Math]::Max(1.6, 11.0 * $s)))
    $hudPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hudPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $hudPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $m0 = 42.0 * $s
    $m1 = 214.0 * $s
    $cl = 30.0 * $s
    # Top-Left bracket
    $g.DrawLine($hudPen, $m0, $m0 + $cl, $m0, $m0); $g.DrawLine($hudPen, $m0, $m0, $m0 + $cl, $m0)
    # Top-Right bracket
    $g.DrawLine($hudPen, $m1 - $cl, $m0, $m1, $m0); $g.DrawLine($hudPen, $m1, $m0, $m1, $m0 + $cl)
    # Bottom-Left bracket
    $g.DrawLine($hudPen, $m0, $m1 - $cl, $m0, $m1); $g.DrawLine($hudPen, $m0, $m1, $m0 + $cl, $m1)
    # Bottom-Right bracket
    $g.DrawLine($hudPen, $m1 - $cl, $m1, $m1, $m1); $g.DrawLine($hudPen, $m1, $m1, $m1, $m1 - $cl)

    # 4. Ergonomic Game Controller Silhouette (Meaning: Made for Gaming!)
    $gpPath = New-RoundedRectPath (58.0 * $s) (66.0 * $s) (140.0 * $s) (82.0 * $s) (38.0 * $s)
    $gpFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 32, 37, 46))
    $gpStroke = New-Object System.Drawing.Pen($ivory, [float]([Math]::Max(1.4, 7.0 * $s)))
    $g.FillPath($gpFill, $gpPath)
    $g.DrawPath($gpStroke, $gpPath)

    # Controller D-Pad (Left side, crisp Subtitle Yellow)
    $dpPen = New-Object System.Drawing.Pen($yellow, [float]([Math]::Max(1.8, 11.0 * $s)))
    $dpPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $dpPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $dpx = 96.0 * $s; $dpy = 107.0 * $s; $dpr = 16.0 * $s
    $g.DrawLine($dpPen, $dpx - $dpr, $dpy, $dpx + $dpr, $dpy)
    $g.DrawLine($dpPen, $dpx, $dpy - $dpr, $dpx, $dpy + $dpr)

    # Controller 4 Action Diamond Buttons (Right side: Teal & Yellow)
    $tealBr = New-Object System.Drawing.SolidBrush($teal)
    $yelBr  = New-Object System.Drawing.SolidBrush($yellow)
    $ivBr   = New-Object System.Drawing.SolidBrush($ivory)
    $abx = 160.0 * $s; $aby = 107.0 * $s; $abr = 15.0 * $s; $bd = [Math]::Max(2.2, 13.0 * $s)
    $g.FillEllipse($tealBr, $abx - $bd * 0.5, $aby - $abr - $bd * 0.5, $bd, $bd) # Top
    $g.FillEllipse($yelBr,  $abx + $abr - $bd * 0.5, $aby - $bd * 0.5, $bd, $bd) # Right
    $g.FillEllipse($ivBr,   $abx - $bd * 0.5, $aby + $abr - $bd * 0.5, $bd, $bd) # Bottom
    $g.FillEllipse($tealBr, $abx - $abr - $bd * 0.5, $aby - $bd * 0.5, $bd, $bd) # Left

    # 5. Live Translated Subtitle Bar Underneath Controller (Meaning: Real-Time Game Subtitles!)
    $subBar1 = New-RoundedRectPath (66.0 * $s) (166.0 * $s) (124.0 * $s) (14.0 * $s) (7.0 * $s)
    $g.FillPath($yelBr, $subBar1)

    $subBar2 = New-RoundedRectPath (84.0 * $s) (188.0 * $s) (88.0 * $s) (12.0 * $s) (6.0 * $s)
    $g.FillPath($tealBr, $subBar2)

    # Live Signal Dot at top center
    $sigD = [Math]::Max(2.0, 12.0 * $s)
    $g.FillEllipse($tealBr, (128.0 * $s) - $sigD * 0.5, (42.0 * $s) - $sigD * 0.5, $sigD, $sigD)

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
Write-Host "Generated multi-res gaming icon: $OutIco"
