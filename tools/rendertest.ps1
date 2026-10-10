<#
.SYNOPSIS
  Golden render tests for the client (roadmap C1/C3/C4). Builds Client.exe to a scratch folder, runs
  "Client.exe --rendertest" from a scratch run folder and records or compares the captured frames.

.DESCRIPTION
  The run folder (%TEMP%\mir2-rendertest\run) holds the scratch build plus directory junctions Data, Map and Sound that
  point at the client data folder (default C:\Mir2\Build\Client\Debug, read only), and copies of its Mir2Test.ini and
  KeyBinds.ini. The client in test mode never saves settings, never connects and plays no sound.

  SAFETY: the junctions point into the real client data. This script never deletes the run folder recursively and
  removes a junction only with "cmd /c rmdir" (which removes the link, not the target). Do not "Remove-Item -Recurse"
  the run folder by hand while the junctions exist.

  Goldens and runs live outside the repo (default %USERPROFILE%\Mir2RenderGoldens), never in git.

.PARAMETER Mode       Record (write goldens), Compare (run and compare with goldens, exact), Parity (compare with the
                      D3D11 parity tolerance), Run (just render to -Out).
.PARAMETER Backend    D3D9 or D3D11 (passed as [Graphics] Renderer for the run; ignored by builds without the toggle).
.PARAMETER Goldens    Golden folder. Default %USERPROFILE%\Mir2RenderGoldens\D3D9.
.PARAMETER Out        Output folder for Run/Compare/Parity. Default %TEMP%\mir2-rendertest\out-<Backend>.
.PARAMETER Bench      Frames per scenario for the frame-time benchmark (0 = off).
.PARAMETER DataRoot   Client data folder (read only). Default C:\Mir2\Build\Client\Debug.
.PARAMETER NoBuild    Reuse the last scratch build.
.PARAMETER SmokeSeconds  Smoke mode: seconds of the real game loop (login scene) before capture. Default 10.
.PARAMETER FullScreen    Smoke mode: run fullscreen (D3D11: borderless window over the screen; D3D9: exclusive mode).
#>
param(
    [ValidateSet('Record', 'Compare', 'Parity', 'Run', 'Smoke')][string]$Mode = 'Compare',
    [ValidateSet('D3D9', 'D3D11')][string]$Backend = 'D3D9',
    [string]$Goldens = (Join-Path $env:USERPROFILE 'Mir2RenderGoldens\D3D9'),
    [string]$Out = '',
    [int]$Bench = 0,
    [string]$DataRoot = '',
    [switch]$NoBuild,
    [int]$SmokeSeconds = 10,
    [switch]$FullScreen
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $DataRoot) { $DataRoot = Join-Path $repo 'Build\Client\Debug' }
$root = Join-Path $env:TEMP 'mir2-rendertest'
$build = Join-Path $root 'build'
$run = Join-Path $root 'run'
if (-not $Out) { $Out = Join-Path $root ("out-" + $Backend) }
if ($Mode -eq 'Record') { $Out = $Goldens }

foreach ($p in $build, $run, $Out) {
    $full = [IO.Path]::GetFullPath($p)
    if ($full.StartsWith([IO.Path]::GetFullPath((Join-Path $repo 'Build')), [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to write into Build\: $full" }
}

if (-not $NoBuild) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify.ps1') -Only Client -NoTests -Scratch $build
    if ($LASTEXITCODE -ne 0) { throw 'Client build failed' }
}
$clientBuild = Join-Path $build 'Client'
if (-not (Test-Path (Join-Path $clientBuild 'Client.exe'))) { throw "No Client.exe in $clientBuild" }

New-Item -ItemType Directory -Force $run | Out-Null
& robocopy $clientBuild $run /E /NFL /NDL /NJH /NJS /NP | Out-Null
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
# Backend toggle for builds that have it ([Graphics] Renderer). Test mode never saves the ini, so this stays in the scratch copy.
foreach ($ini in 'Mir2Test.ini', 'Mir2Config.ini') {
    $path = Join-Path $run $ini
    if (-not (Test-Path $path)) { continue }
    $text = Get-Content $path -Raw
    if ($text -match '(?m)^Renderer=') { $text = $text -replace '(?m)^Renderer=.*$', "Renderer=$Backend" }
    elseif ($text -match '(?m)^\[Graphics\]') { $text = $text -replace '(?m)^\[Graphics\]\s*$', "[Graphics]`r`nRenderer=$Backend" }
    else { $text += "`r`n[Graphics]`r`nRenderer=$Backend`r`n" }
    Set-Content -Path $path -Value $text -Encoding ASCII -NoNewline
}

New-Item -ItemType Directory -Force $Out | Out-Null
if ($Mode -eq 'Smoke') {
    $args = @('--smoke', "$SmokeSeconds", '--out', $Out)
    if ($FullScreen) { $args += '--fullscreen' }
    $p = Start-Process -FilePath (Join-Path $run 'Client.exe') -ArgumentList $args -WorkingDirectory $run -PassThru -Wait
    Write-Host "smoke exit code $($p.ExitCode) ($Backend) -> $Out"
    if (Test-Path (Join-Path $Out 'smoke.log')) { Get-Content (Join-Path $Out 'smoke.log') }
    exit $p.ExitCode
}
$args = @('--rendertest', '--out', $Out)
if ($Bench -gt 0) { $args += @('--bench', "$Bench") }
$p = Start-Process -FilePath (Join-Path $run 'Client.exe') -ArgumentList $args -WorkingDirectory $run -PassThru -Wait
Write-Host "rendertest exit code $($p.ExitCode) ($Backend) -> $Out"
if (Test-Path (Join-Path $Out 'rendertest.log')) { Get-Content (Join-Path $Out 'rendertest.log') }
if ($p.ExitCode -ne 0) { if (Test-Path (Join-Path $run 'Error.txt')) { Get-Content (Join-Path $run 'Error.txt') -Tail 20 }; exit $p.ExitCode }

if ($Mode -eq 'Compare' -or $Mode -eq 'Parity') {
    $cargs = @('--rendercompare', $Goldens, $Out)
    if ($Mode -eq 'Parity') {
        $cargs += '--parity'
        # Documented waivers (Knowledgebase/Client/D3D11-Parity.md): S14 magnifies render-target content through the
        # D3DX projection-at-Begin quirk (never happens in the game) and 2 pixels on that edge tie under the fill rule.
        $cargs += @('--waive', 'S14_render_targets:4')
    }
    $c = Start-Process -FilePath (Join-Path $run 'Client.exe') -ArgumentList $cargs -WorkingDirectory $run -PassThru -Wait
    Get-Content (Join-Path $Out 'compare.txt')
    exit $c.ExitCode
}
exit 0
