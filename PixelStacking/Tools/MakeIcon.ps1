param([string]$OutIco, [string]$PreviewPng)
Add-Type -AssemblyName System.Drawing

# 等角的一疊切片：由下往上畫，每片有頂面（亮）、左側面（中）、右側面（暗），頂面再用 4x4 / 2x2 的像素格子
function New-IconBitmap([int]$size) {
    $small = $size -le 32
    $ss = if ($size -ge 128) { 4 } else { 16 }
    $S = $size * $ss
    $bmp = New-Object System.Drawing.Bitmap $S, $S, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $S / 256.0
    if ($small) { $cnt = 3; $rw = 208.0; $rh = 104.0; $t = 36.0; $pitch = 56.0; $y0 = 4.0; $lw = 9.0 }
    else        { $cnt = 4; $rw = 200.0; $rh = 100.0; $t = 22.0; $pitch = 36.0; $y0 = 12.0; $lw = 5.0 }
    $cx = 128.0
    # 每片的顏色（上 -> 下）：綠 -> 藍
    if ($small) { $hues = @(@(70, 224, 110), @(52, 190, 190), @(66, 128, 232)) }
    else { $hues = @(@(70, 224, 110), @(56, 204, 160), @(56, 164, 214), @(70, 120, 232)) }
    $outline = [System.Drawing.Color]::FromArgb(255, 14, 22, 36)
    $pen = New-Object System.Drawing.Pen $outline, ([single]($lw * $k))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    function Pt([double]$x, [double]$y) { return New-Object System.Drawing.PointF ([single]($x * $k)), ([single]($y * $k)) }
    function Shade($c, [double]$f) { return [System.Drawing.Color]::FromArgb(255, [int][Math]::Min(255, $c[0] * $f), [int][Math]::Min(255, $c[1] * $f), [int][Math]::Min(255, $c[2] * $f)) }

    for ($i = $cnt - 1; $i -ge 0; $i--) {
        $y = $y0 + $i * $pitch
        $pN = @($cx, $y); $pE = @(($cx + $rw / 2), ($y + $rh / 2)); $pS = @($cx, ($y + $rh)); $pW = @(($cx - $rw / 2), ($y + $rh / 2))
        $c = $hues[$i]
        $top = New-Object System.Drawing.SolidBrush (Shade $c 1.0)
        $left = New-Object System.Drawing.SolidBrush (Shade $c 0.72)
        $right = New-Object System.Drawing.SolidBrush (Shade $c 0.52)
        $lf = [System.Drawing.PointF[]]@((Pt $pW[0] $pW[1]), (Pt $pS[0] $pS[1]), (Pt $pS[0] ($pS[1] + $t)), (Pt $pW[0] ($pW[1] + $t)))
        $rt = [System.Drawing.PointF[]]@((Pt $pS[0] $pS[1]), (Pt $pE[0] $pE[1]), (Pt $pE[0] ($pE[1] + $t)), (Pt $pS[0] ($pS[1] + $t)))
        $tp = [System.Drawing.PointF[]]@((Pt $pN[0] $pN[1]), (Pt $pE[0] $pE[1]), (Pt $pS[0] $pS[1]), (Pt $pW[0] $pW[1]))
        $g.FillPolygon($left, $lf); $g.FillPolygon($right, $rt); $g.FillPolygon($top, $tp)

        if ($i -eq 0) {
            # 頂面的像素格子（只畫在最上面那片）
            $cells = if ($small) { 2 } else { 4 }
            for ($u = 0; $u -lt $cells; $u++) { for ($v = 0; $v -lt $cells; $v++) {
                $shadeF = if ((($u + $v) % 2) -eq 0) { 1.0 } else { 0.86 }
                $col = Shade $c $shadeF
                if (-not $small -and $u -eq 1 -and $v -eq 2) { $col = [System.Drawing.Color]::FromArgb(255, 255, 214, 64) }
                if (-not $small -and $u -eq 2 -and $v -eq 1) { $col = [System.Drawing.Color]::FromArgb(255, 240, 250, 255) }
                if ($small -and $u -eq 1 -and $v -eq 0) { $col = [System.Drawing.Color]::FromArgb(255, 255, 214, 64) }
                $u0 = $u / [double]$cells; $u1 = ($u + 1) / [double]$cells; $v0 = $v / [double]$cells; $v1 = ($v + 1) / [double]$cells
                function P2($uu, $vv) { return Pt ($pN[0] + $uu * ($pE[0] - $pN[0]) + $vv * ($pW[0] - $pN[0])) ($pN[1] + $uu * ($pE[1] - $pN[1]) + $vv * ($pW[1] - $pN[1])) }
                $quad = [System.Drawing.PointF[]]@((P2 $u0 $v0), (P2 $u1 $v0), (P2 $u1 $v1), (P2 $u0 $v1))
                $g.FillPolygon((New-Object System.Drawing.SolidBrush $col), $quad)
            } }
        }
        $g.DrawPolygon($pen, $tp); $g.DrawPolygon($pen, $lf); $g.DrawPolygon($pen, $rt)
    }
    $g.Dispose()
    if ($ss -eq 1) { return $bmp }
    $out = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.DrawImage($bmp, 0, 0, $size, $size); $g2.Dispose(); $bmp.Dispose()
    return $out
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = @{}
foreach ($s in $sizes) { $images[$s] = New-IconBitmap $s }

# ICO：256 用 PNG，其餘用 32 位元 BMP（DIB）
$entries = @()
foreach ($s in $sizes) {
    $bmp = $images[$s]
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $data = $ms.ToArray()
    } else {
        $ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0); $bw.Write([int]($s * $s * 4)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
        for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $px = $bmp.GetPixel($x, $y); $bw.Write([byte]$px.B); $bw.Write([byte]$px.G); $bw.Write([byte]$px.R); $bw.Write([byte]$px.A) } }
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        for ($y = 0; $y -lt $s; $y++) { for ($b = 0; $b -lt $maskRow; $b++) { $bw.Write([byte]0) } }
        $bw.Flush(); $data = $ms.ToArray()
    }
    $entries += , @($s, $data)
}
$fs = [System.IO.File]::Create($OutIco); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $s = $e[0]; $d = $e[1]
    $w.Write([byte]$(if ($s -ge 256) { 0 } else { $s })); $w.Write([byte]$(if ($s -ge 256) { 0 } else { $s })); $w.Write([byte]0); $w.Write([byte]0); $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$d.Length); $w.Write([int]$offset)
    $offset += $d.Length
}
foreach ($e in $entries) { $w.Write([byte[]]$e[1]) }
$w.Flush(); $fs.Close()

# 預覽圖：各尺寸並排（放大小的方便看）
$sheet = New-Object System.Drawing.Bitmap 760, 300
$gs = [System.Drawing.Graphics]::FromImage($sheet); $gs.Clear([System.Drawing.Color]::FromArgb(255, 40, 44, 52))
$gs.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$gs.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$gs.DrawImage($images[256], 10, 10, 256, 256)
$x = 280
foreach ($s in 128, 64, 48, 32, 24, 16) { $gs.DrawImage($images[$s], $x, 10, $s, $s); $x += $s + 10 }
$x = 280
foreach ($s in 32, 24, 16) { $gs.DrawImage($images[$s], $x, 160, $s * 4, $s * 4); $x += $s * 4 + 12 }
$gs.Dispose(); $sheet.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
"ok $((Get-Item $OutIco).Length) bytes"
