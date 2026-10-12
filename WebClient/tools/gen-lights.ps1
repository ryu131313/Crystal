<#
.SYNOPSIS
  Writes WebClient\play\lights\light<N>.png: the classic light textures, drawn with the same GDI+ calls as
  Client/MirGraphics/DXManager.cs CreateLights (PathGradientBrush on an ellipse, same colours and positions).
  DXManager.Lights[k] has the size LightSizes[k + 1] (the loop starts at 1), so light<k>.png does too.
  Re-run only if CreateLights changes. Windows PowerShell (System.Drawing) only.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'play\lights'
New-Item -ItemType Directory -Force $out | Out-Null

$sizes = @(@(125,95),@(205,156),@(285,217),@(365,277),@(445,338),@(525,399),@(605,460),@(685,521),@(765,581),@(845,642),@(925,703))
for ($i = 1; $i -lt $sizes.Count; $i++) {
    $w = $sizes[$i][0]; $h = $sizes[$i][1]
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddEllipse((New-Object System.Drawing.Rectangle(0, 0, $w, $h)))
    $brush = New-Object System.Drawing.Drawing2D.PathGradientBrush($path)
    $colours = [System.Drawing.Color[]]@(
        [System.Drawing.Color]::White,
        [System.Drawing.Color]::FromArgb(255,210,210,210),
        [System.Drawing.Color]::FromArgb(255,160,160,160),
        [System.Drawing.Color]::FromArgb(255,70,70,70),
        [System.Drawing.Color]::FromArgb(255,40,40,40),
        [System.Drawing.Color]::FromArgb(0,0,0,0))
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend
    $blend.Colors = $colours
    $blend.Positions = [single[]]@(0, .2, .4, .6, .8, 1)
    $g.Clear([System.Drawing.Color]::FromArgb(0,0,0,0))
    $brush.InterpolationColors = $blend
    $brush.SurroundColors = $colours
    $brush.CenterColor = [System.Drawing.Color]::White
    $g.FillPath($brush, $path)
    $file = Join-Path $out ("light{0}.png" -f ($i - 1))
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $brush.Dispose(); $path.Dispose(); $g.Dispose(); $bmp.Dispose()
    Write-Host "Wrote $file ($w x $h)"
}
