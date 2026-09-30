# 生成程序图标 app.ico（多尺寸 PNG 封装为 ICO 容器）。
# 一次性工具：图标已提交后无需再运行。
Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
if (-not ('System.Drawing.Color' -as [type])) {
    [void][System.Reflection.Assembly]::LoadWithPartialName('System.Drawing')
}

$root = $PSScriptRoot
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = New-Object System.Collections.ArrayList

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)

    # 圆角方块底：深蓝 -> 亮蓝渐变
    $radius = [int][math]::Max(2, ($s * 0.22))
    $d = 2 * $radius
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc(($s - $d), 0, $d, $d, 270, 90)
    $path.AddArc(($s - $d), ($s - $d), $d, $d, 0, 90)
    $path.AddArc(0, ($s - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $p1 = New-Object System.Drawing.Point(0, 0)
    $p2 = New-Object System.Drawing.Point($s, $s)
    $c1 = [System.Drawing.Color]::FromArgb(255, 40, 84, 178)
    $c2 = [System.Drawing.Color]::FromArgb(255, 96, 150, 255)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($p1, $p2, $c1, $c2)
    $g.FillPath($brush, $path)

    # 文字 AE
    $font = New-Object System.Drawing.Font('Segoe UI', ($s * 0.46), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = [System.Drawing.StringAlignment]::Center
    $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $g.DrawString('AE', $font, $white, $rect, $fmt)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    [void]$pngs.Add(@{ Size = $s; Bytes = $ms.ToArray() })
    $ms.Dispose()
    $bmp.Dispose()
    $brush.Dispose(); $path.Dispose(); $font.Dispose(); $fmt.Dispose(); $white.Dispose()
}

# 组装 ICO 容器：ICONDIR + ICONDIRENTRY*n + PNG 数据
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = $p.Size
    if ($dim -ge 256) { $dim = 0 }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$p.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }
$bw.Flush()
$ico = Join-Path $root 'app.ico'
[System.IO.File]::WriteAllBytes($ico, $out.ToArray())
$bw.Dispose()
$out.Dispose()

Write-Output ('app.ico 已生成: {0:N1} KB' -f ((Get-Item $ico).Length / 1KB))
