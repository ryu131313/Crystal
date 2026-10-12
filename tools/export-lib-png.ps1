# Exports every image in Crystal .Lib files (v2/v3) to PNG, one folder per library.
# Read-only on the source libraries. Writes <OutDir>\<Lib>\<index>.png (+ <index>_mask.png
# for two-layer images) and <OutDir>\<Lib>\placements.csv (index, size, draw offsets).
#
#   powershell -ExecutionPolicy Bypass -File tools\export-lib-png.ps1 -OutDir C:\Mir2-Export\UI
param(
    [string]$DataDir = "C:\Mir2\Build\Client\Release\Data",
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string[]]$Libs = @("Prguse", "Prguse2", "Prguse3", "UI_32bit", "ChrSel", "Title", "BuffIcon",
                        "MagIcon", "MagIcon2", "Help", "MapLinkIcon", "GuildSkill")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if ([IO.Path]::GetFullPath($OutDir).StartsWith("C:\Mir2\Build", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write under C:\Mir2\Build"
}

function Expand-Gzip([byte[]]$bytes) {
    $in = New-Object IO.MemoryStream(, $bytes)
    $gz = New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress)
    $out = New-Object IO.MemoryStream
    $gz.CopyTo($out)
    $gz.Dispose()
    return $out.ToArray()
}

function Save-Png([int]$w, [int]$h, [byte[]]$compressed, [string]$path) {
    if ($w -lt 1 -or $h -lt 1 -or $compressed.Length -eq 0) { return $false }
    $raw = Expand-Gzip $compressed
    $bmp = New-Object Drawing.Bitmap($w, $h, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $data = $bmp.LockBits((New-Object Drawing.Rectangle(0, 0, $w, $h)),
        [Drawing.Imaging.ImageLockMode]::WriteOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    [Runtime.InteropServices.Marshal]::Copy($raw, 0, $data.Scan0, [Math]::Min($raw.Length, $w * $h * 4))
    $bmp.UnlockBits($data)
    $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $true
}

foreach ($lib in $Libs) {
    $file = Join-Path $DataDir "$lib.Lib"
    if (-not (Test-Path $file)) { Write-Warning "Missing $file"; continue }

    $dest = Join-Path $OutDir $lib
    New-Item -ItemType Directory -Force $dest | Out-Null

    $fs = [IO.File]::OpenRead($file)
    $r = New-Object IO.BinaryReader($fs)
    $version = $r.ReadInt32()
    if ($version -lt 2) { Write-Warning "$lib.Lib: unsupported version $version"; $r.Dispose(); continue }
    $count = $r.ReadInt32()
    if ($version -ge 3) { [void]$r.ReadInt32() }   # frame table seek
    $index = New-Object int[] $count
    for ($i = 0; $i -lt $count; $i++) { $index[$i] = $r.ReadInt32() }

    $csv = New-Object Collections.Generic.List[string]
    $csv.Add("Index,Width,Height,X,Y,ShadowX,ShadowY,HasMask")
    $saved = 0

    for ($i = 0; $i -lt $count; $i++) {
        if ($index[$i] -le 0) { continue }
        $fs.Position = $index[$i]
        $w = $r.ReadInt16(); $h = $r.ReadInt16(); $x = $r.ReadInt16(); $y = $r.ReadInt16()
        $sx = $r.ReadInt16(); $sy = $r.ReadInt16(); $shadow = $r.ReadByte()
        $len = $r.ReadInt32(); $bytes = $r.ReadBytes($len)
        $hasMask = ($shadow -shr 7) -eq 1

        if (Save-Png $w $h $bytes (Join-Path $dest ("{0:D5}.png" -f $i))) {
            $saved++
            $csv.Add("$i,$w,$h,$x,$y,$sx,$sy,$hasMask")
        }

        if ($hasMask) {
            $mw = $r.ReadInt16(); $mh = $r.ReadInt16(); [void]$r.ReadInt16(); [void]$r.ReadInt16()
            $mlen = $r.ReadInt32(); $mbytes = $r.ReadBytes($mlen)
            [void](Save-Png $mw $mh $mbytes (Join-Path $dest ("{0:D5}_mask.png" -f $i)))
        }
    }

    $r.Dispose()
    [IO.File]::WriteAllLines((Join-Path $dest "placements.csv"), $csv)
    Write-Host ("{0,-12} v{1}  {2,6} slots  {3,6} png" -f $lib, $version, $count, $saved)
}
