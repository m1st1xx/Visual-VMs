param([string]$Out)
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($s / 256.0, $s / 256.0)
    # rounded blue tile
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 56
    $p.AddArc(8, 8, $r, $r, 180, 90); $p.AddArc(248 - $r, 8, $r, $r, 270, 90)
    $p.AddArc(248 - $r, 248 - $r, $r, $r, 0, 90); $p.AddArc(8, 248 - $r, $r, $r, 90, 90); $p.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(21, 101, 192))), $p)
    # cables
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 14
    $pts = @(@(128, 70), @(66, 182), @(190, 182))
    $g.DrawLine($pen, 128, 70, 66, 182); $g.DrawLine($pen, 128, 70, 190, 182); $g.DrawLine($pen, 66, 182, 190, 182)
    # nodes
    $node = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $core = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(239, 108, 0))
    foreach ($pt in $pts) {
        $g.FillEllipse($node, $pt[0] - 34, $pt[1] - 34, 68, 68)
        $g.FillEllipse($core, $pt[0] - 18, $pt[1] - 18, 36, 36)
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
# ICO container with PNG-encoded entries
$fs = [System.IO.File]::Create($Out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($b in $pngs) { $w.Write($b) }
$w.Close()
