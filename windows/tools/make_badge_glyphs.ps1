# Prints the digit outlines used by the unread badge in Web/feed.html (const BADGE_GLYPHS).
# The badge draws digits as vector paths instead of text, so they can be centered by their exact geometry
# at any screen scaling. Font: Segoe UI Semibold (seguisb.ttf), 1000 units per em, baseline at y = 0 (y grows downward).
# Each entry: [advance, inkLeft, inkTop, inkRight, inkBottom, pathData]
Add-Type -AssemblyName PresentationCore
$font = New-Object System.Windows.Media.GlyphTypeface ([Uri]"$env:WINDIR\Fonts\seguisb.ttf")
$inv = [Globalization.CultureInfo]::InvariantCulture
$lines = foreach ($ch in '0123456789+'.ToCharArray()) {
  $gi = $font.CharacterToGlyphMap[[int]$ch]
  $geo = $font.GetGlyphOutline($gi, 1000, 1000)
  $path = [System.Windows.Media.PathGeometry]::CreateFromGeometry($geo)
  $b = $path.Bounds
  $d = $path.ToString($inv) -replace '^F[01]', ''
  # round coordinates to 0.1 unit (0.0011 px at 11 px font size)
  $d = [regex]::Replace($d, '-?\d+\.\d+(E-?\d+)?', { param($m) [math]::Round([double]::Parse($m.Value, $inv), 1).ToString($inv) })
  $f = { param($v) [math]::Round($v, 1).ToString($inv) }
  "  '$ch': [$(& $f ($font.AdvanceWidths[$gi] * 1000)), $(& $f $b.Left), $(& $f $b.Top), $(& $f $b.Right), $(& $f $b.Bottom), '$d'],"
}
"const BADGE_GLYPHS = {"
$lines
"};"
