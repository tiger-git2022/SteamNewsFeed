param([string]$OutDir = (Join-Path $PSScriptRoot '..'), [double]$Dx = 12, [double]$Dy = -12, [double]$Zoom = 1.0, [switch]$PreviewOnly, [string]$PreviewPath = '')
# Generates windows\app.ico (16-256 px) and windows\SteamNewsFeed\icon.png.
# Same shapes as the Android app's launcher icon (Steam信息流, android/app/src/main/res/drawable/ic_launcher_foreground.xml):
# Steam-blue rounded square, white news card with a picture block and two text lines, orange "new" dot.
# Foreground is shifted 12 right / 12 up (256 grid) from the Android shapes so the white card does not sit low-left.
# Every size is drawn from the shapes directly (not scaled down from 256 px); small sizes snap to whole pixels so they stay sharp.
Add-Type -AssemblyName System.Drawing

function Draw([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $s = $size / 256.0
  $snap = $size -le 48
  # design units (256 grid) -> pixels; small sizes round to whole pixels
  function P([double]$v) { if ($snap) { return [float][math]::Round($v * $s) } else { return [float]($v * $s) } }
  # foreground placement: zoom around the centre, then shift (design units)
  function FX([double]$v) { return 128 + ($v - 128) * $Zoom + $Dx }
  function FY([double]$v) { return 128 + ($v - 128) * $Zoom + $Dy }

  function RoundRect([double]$x1, [double]$y1, [double]$x2, [double]$y2, [double]$r, [switch]$Bg) {
    if (-not $Bg) { $x1 = FX $x1; $x2 = FX $x2; $y1 = FY $y1; $y2 = FY $y2; $r = $r * $Zoom }
    $x = P $x1; $y = P $y1; $w = (P $x2) - $x; $h = (P $y2) - $y
    $rr = [math]::Max(0.5, [math]::Min([double]($r * $s), [math]::Min($w, $h) / 2))
    $d = [float]($rr * 2)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
  }
  function Brush([int]$a, [int]$rgb) { New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($a, ($rgb -shr 16) -band 255, ($rgb -shr 8) -band 255, $rgb -band 255)) }

  # background: solid Steam blue, same as the Android icon (#4A8FC6); small sizes fill edge to edge
  $inset = if ($size -le 24) { 0 } else { 8 }
  $g.FillPath((Brush 255 0x4A8FC6), (RoundRect $inset $inset (256 - $inset) (256 - $inset) 52 -Bg))

  # back card (faded) - left out below 32 px where it only adds mush
  if ($size -ge 32) { $g.FillPath((Brush 115 0xFFFFFF), (RoundRect 79 46 209 195 14)) }
  # front card
  $g.FillPath((Brush 255 0xFFFFFF), (RoundRect 47 71 185 219 14))
  # picture block
  $g.FillPath((Brush 255 0x66C0F4), (RoundRect 61 85 171 135 7))
  # text lines (one thicker line at tiny sizes)
  if ($size -ge 32) {
    $g.FillPath((Brush 255 0x2A475E), (RoundRect 61 151 172 164 6.5))
    $g.FillPath((Brush 255 0x2A475E), (RoundRect 61 177 137 190 6.5))
  } else {
    $g.FillPath((Brush 255 0x2A475E), (RoundRect 61 154 172 182 8))
  }
  # "new" dot with a dark ring
  $cx = FX 192; $cy = FY 72
  $ring = $(if ($size -le 24) { 36 } else { 31 }) * $Zoom; $dot = $(if ($size -le 24) { 27 } else { 23 }) * $Zoom
  $g.FillEllipse((Brush 255 0x1B2838), (P ($cx - $ring)), (P ($cy - $ring)), (P ($cx + $ring)) - (P ($cx - $ring)), (P ($cy + $ring)) - (P ($cy - $ring)))
  $g.FillEllipse((Brush 255 0xF0A050), (P ($cx - $dot)), (P ($cy - $dot)), (P ($cx + $dot)) - (P ($cx - $dot)), (P ($cy + $dot)) - (P ($cy - $dot)))

  $g.Dispose()
  return $bmp
}

if ($PreviewOnly) {
  # one strip: 256 / 48 / 32 / 24 / 16 px side by side, for comparing placements
  $sz = 256, 48, 32, 24, 16; $W = [int](($sz | Measure-Object -Sum).Sum + 20 * $sz.Count)
  $strip = New-Object System.Drawing.Bitmap $W, 256; $sg = [System.Drawing.Graphics]::FromImage($strip); $sg.Clear([System.Drawing.Color]::FromArgb(255, 40, 40, 40)); $x = 0
  foreach ($z in $sz) { $b = Draw $z; $sg.DrawImageUnscaled($b, $x, 0); $x += $z + 20; $b.Dispose() }
  $sg.Dispose(); $strip.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png); "ok"; return
}
$OutDir = (Resolve-Path $OutDir).Path
$big = Draw 256
$big.Save((Join-Path $OutDir 'SteamNewsFeed\icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# multi-size .ico with PNG entries
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
  $b = Draw $sz
  $ms = New-Object System.IO.MemoryStream
  $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += , ($ms.ToArray())
  $b.Dispose()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $sz = $sizes[$i]
  $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
  $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
  $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app.ico'), $out.ToArray())
"ok: app.ico ($($sizes -join ', ') px), SteamNewsFeed\icon.png"
