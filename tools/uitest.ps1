<#
.SYNOPSIS
  Offline UI test for the desktop client (Desktop-Modern-UI-Plan phase 1a). Builds Client.exe to a scratch folder, runs
  "Client.exe --uitest" from a scratch run folder: a real GameScene with no server, fed synthetic packets, with keybinds and
  item-cell clicks, one screenshot + state dump per step. Optionally diffs two runs.

.DESCRIPTION
  Like tools\rendertest.ps1, the run folder (%TEMP%\mir2-uitest\<Label>\run) holds the scratch build plus directory junctions
  Data, Map and Sound that point at the client data folder (default C:\Mir2\Build\Client\Debug, read only) and copies of its
  inis. Test mode never connects, never saves settings and plays no sound.
  SAFETY: the junctions point into the real client data. This script never deletes the run folder recursively and removes a
  junction only with "cmd /c rmdir". Do not "Remove-Item -Recurse" the run folder by hand while the junctions exist.

.PARAMETER Source     Client project folder to build. Default C:\Mir2\Client. A worktree of an older commit (with the
                      uitest hooks applied) gives the "before" run.
.PARAMETER Label      Run name (folder under %TEMP%\mir2-uitest). Default "current".
.PARAMETER UiStyle    Classic (default) or Modern.
.PARAMETER Backend    D3D9 or D3D11 ([Graphics] Renderer for the run). Default D3D11.
.PARAMETER NoBuild    Reuse the last build for this label.
.PARAMETER DataRoot   Client data folder (read only). Default C:\Mir2\Build\Client\Debug.
.PARAMETER DiffWith   Another label: after the run, diff the two uitest.log files and compare same-named screenshots.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\uitest.ps1
  powershell -ExecutionPolicy Bypass -File tools\uitest.ps1 -UiStyle Modern -Label modern
  powershell -ExecutionPolicy Bypass -File tools\uitest.ps1 -Source D:\mir2-head\Client -Label head
  powershell -ExecutionPolicy Bypass -File tools\uitest.ps1 -NoBuild -DiffWith head
#>
param(
    [string]$Source,
    [string]$Label = 'current',
    [ValidateSet('Classic', 'Modern')][string]$UiStyle = 'Classic',
    [ValidateSet('D3D9', 'D3D11')][string]$Backend = 'D3D11',
    [switch]$NoBuild,
    [string]$DataRoot,
    [string]$DiffWith
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $repo 'Client' }
if (-not $DataRoot) { $DataRoot = Join-Path $repo 'Build\Client\Debug' }
$base = Join-Path $env:TEMP 'mir2-uitest'
$root = Join-Path $base $Label
$build = Join-Path $root 'build'
$run = Join-Path $root 'run'
$out = Join-Path $root 'out'

foreach ($p in $build, $run, $out) {
    $full = [IO.Path]::GetFullPath($p)
    if ($full.StartsWith([IO.Path]::GetFullPath((Join-Path $repo 'Build')), [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to write into Build\: $full" }
}

if (-not $NoBuild) {
    $csproj = Join-Path $Source 'Client.csproj'
    if (-not (Test-Path $csproj)) { throw "No Client.csproj in $Source" }
    & dotnet build $csproj -o $build -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Client build failed' }
}
if (-not (Test-Path (Join-Path $build 'Client.exe'))) { throw "No Client.exe in $build" }

New-Item -ItemType Directory -Force $run | Out-Null
& robocopy $build $run /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

foreach ($d in 'Data', 'Map', 'Sound') {
    $link = Join-Path $run $d
    $target = Join-Path $DataRoot $d
    if (-not (Test-Path $target)) { continue }
    if (Test-Path $link) {
        $item = Get-Item $link -Force
        if (-not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "$link exists and is not a junction; refusing to continue" }
        continue
    }
    & cmd /c mklink /J "$link" "$target" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "mklink /J failed for $link" }
}
foreach ($f in 'Mir2Test.ini', 'Mir2Config.ini', 'KeyBinds.ini') {
    $src = Join-Path $DataRoot $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $run $f) -Force }
}
foreach ($ini in 'Mir2Test.ini', 'Mir2Config.ini') {
    $path = Join-Path $run $ini
    if (-not (Test-Path $path)) { continue }
    $text = Get-Content $path -Raw
    if ($text -match '(?m)^Renderer=') { $text = $text -replace '(?m)^Renderer=.*$', "Renderer=$Backend" }
    elseif ($text -match '(?m)^\[Graphics\]') { $text = $text -replace '(?m)^\[Graphics\]\s*$', "[Graphics]`r`nRenderer=$Backend" }
    else { $text += "`r`n[Graphics]`r`nRenderer=$Backend`r`n" }
    Set-Content -Path $path -Value $text -Encoding ASCII -NoNewline
}

New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -Filter '*.png' | Remove-Item -Force
$errorFile = Join-Path $run 'Error.txt'
if (Test-Path $errorFile) { Remove-Item $errorFile -Force }   # scratch copy only

$p = Start-Process -FilePath (Join-Path $run 'Client.exe') -ArgumentList @('--uitest', '--uistyle', $UiStyle, '--out', $out) -WorkingDirectory $run -PassThru -Wait
Write-Host "uitest exit code $($p.ExitCode) ($Label, $UiStyle, $Backend) -> $out"
$log = Join-Path $out 'uitest.log'
if (Test-Path $log) { Get-Content $log | Select-Object -Last 4 } else { Write-Host 'no uitest.log' }

if ($DiffWith) {
    $other = Join-Path (Join-Path $base $DiffWith) 'out'
    Write-Host "---- diff of uitest.log: $DiffWith (<) vs $Label (>)"
    $a = Get-Content (Join-Path $other 'uitest.log')
    $b = Get-Content $log
    $d = Compare-Object $a $b -SyncWindow 50
    if ($d) { $d | ForEach-Object { '{0} {1}' -f $(if ($_.SideIndicator -eq '<=') { '<' } else { '>' }), $_.InputObject } } else { Write-Host 'logs identical' }

    Write-Host "---- screenshots (differing pixels, channel delta > 8)"
    Add-Type -AssemblyName System.Drawing
    foreach ($png in Get-ChildItem $out -Filter '*.png') {
        $twin = Join-Path $other $png.Name
        if (-not (Test-Path $twin)) { Write-Host "$($png.Name): missing in $DiffWith"; continue }
        $x = [Drawing.Bitmap]::new($png.FullName); $y = [Drawing.Bitmap]::new($twin)
        try {
            if ($x.Width -ne $y.Width -or $x.Height -ne $y.Height) { Write-Host "$($png.Name): size differs"; continue }
            $diff = 0
            for ($j = 0; $j -lt $x.Height; $j += 2) {
                for ($i = 0; $i -lt $x.Width; $i += 2) {
                    $c1 = $x.GetPixel($i, $j); $c2 = $y.GetPixel($i, $j)
                    if ([Math]::Abs($c1.R - $c2.R) -gt 8 -or [Math]::Abs($c1.G - $c2.G) -gt 8 -or [Math]::Abs($c1.B - $c2.B) -gt 8) { $diff++ }
                }
            }
            Write-Host ('{0}: {1} differing samples (of {2})' -f $png.Name, $diff, ($x.Width * $x.Height / 4))
        } finally { $x.Dispose(); $y.Dispose() }
    }
}
exit $p.ExitCode
