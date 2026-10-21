<#
.SYNOPSIS
  Builds and starts a SCRATCH Server.exe on a synthetic world for load tests (roadmap P6).

.DESCRIPTION
  1. Builds Server.exe into <Scratch>\build via tools\verify.ps1 (never Build\), and the
     tools\scratch-world generator into its own bin\.
  2. Assembles <Scratch>\run: the build output, a generated Server.MirDB + Maps\scratch0.map
     (only when missing, or with -Reset), and Configs\Setup.ini with the load-test profile.
  3. Starts Server.exe minimized with the working directory = <Scratch>\run, writes the PID to
     <Scratch>\server.pid, and waits for "Network Started." in Logs\Server\*.log.

  SAFETY: scratch only. The profile turns on [LoadTest] ExemptLoopback (no IP blocks, MaxIP or
  per-IP account/character limits for 127.x), turns CheckVersion and EnforceDBChecks off, and
  allows StartGame for everyone. Never copy this Setup.ini to a real server. The script refuses a
  scratch folder inside the repo's Build\ folder and a port that is already listening.

  The status listener is turned off ([Network] StatusPortEnabled=False), so a scratch server never
  competes with a live server for the status port (3100 by default).

.PARAMETER Scratch   Root scratch folder. Default %TEMP%\mir2-p6-server.
.PARAMETER Port      Game port (use 17000-17999 for scratch). Default 17000.
.PARAMETER MaxUser   [Network] MaxUser. Default 600.
.PARAMETER LoopbackMaxPacket  [LoadTest] LoopbackMaxPacket (receives per 5 s per connection). Default 400.
.PARAMETER MapSize   Synthetic map width/height. Default 200.
.PARAMETER Maps      Number of synthetic maps (1-20), each with a start point. Default 1. Takes effect with -Reset.
.PARAMETER MapsFrom  Copy -Maps real map files from this folder (read-only, e.g. a server's Maps) instead of
                     generating synthetic ones (ScratchWorld --maps-from). Takes effect when the world is generated.
.PARAMETER Reset     Regenerate the world and delete accounts, logs and backups in <Scratch>\run.
.PARAMETER NoBuild   Reuse the existing <Scratch>\build output.
.PARAMETER Multithreaded  [General] Multithreaded (default True, as on a real server).
.PARAMETER ExtraIni  Lines appended to Setup.ini (e.g. '[Optimisation]','EncodeOnce=False') for A/B runs.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\start-scratch-server.ps1 -Port 17000 -Reset
#>
param(
    [string]$Scratch = (Join-Path $env:TEMP 'mir2-p6-server'),
    [ValidateRange(1024, 65535)][int]$Port = 17000,
    [int]$MaxUser = 600,
    [int]$LoopbackMaxPacket = 400,
    [int]$MapSize = 200,
    [ValidateRange(1, 20)][int]$Maps = 1,
    [string]$MapsFrom = '',
    [switch]$Reset,
    [switch]$NoBuild,
    [bool]$Multithreaded = $true,
    [int]$StartTimeoutSeconds = 90,
    [string[]]$ExtraIni = @()
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repo 'Build')).TrimEnd('\') + '\'
$scratchFull = [IO.Path]::GetFullPath($Scratch).TrimEnd('\')
if (($scratchFull + '\').StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Error "Refusing to use '$scratchFull': it is inside the live Build\ folder."
    exit 2
}

$buildDir = Join-Path $scratchFull 'build'
$runDir = Join-Path $scratchFull 'run'
$pidFile = Join-Path $scratchFull 'server.pid'

# ---- refuse to start twice / on a busy port ----
if (Test-Path $pidFile) {
    $old = Get-Content $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($old -and (Get-Process -Id ([int]$old) -ErrorAction SilentlyContinue)) {
        Write-Error "A scratch server from this folder is already running (PID $old). Run tools\stop-scratch-server.ps1 first."
        exit 3
    }
    Remove-Item $pidFile -Force
}
$busy = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
if ($busy) {
    Write-Error "Port $Port is already listening (PID $($busy[0].OwningProcess)). Pick another port in 17000-17999."
    exit 3
}

# ---- build ----
if (-not $NoBuild) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify.ps1') -Only Server -NoTests -Scratch $buildDir
    if ($LASTEXITCODE -ne 0) { Write-Error 'Server build failed.'; exit 1 }
    & dotnet build (Join-Path $PSScriptRoot 'scratch-world\ScratchWorld.csproj') -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { Write-Error 'ScratchWorld build failed.'; exit 1 }
}
$serverBuild = Join-Path $buildDir 'Server'
if (-not (Test-Path (Join-Path $serverBuild 'Server.exe'))) { Write-Error "No Server.exe in $serverBuild (run without -NoBuild)."; exit 1 }
$generator = Join-Path $PSScriptRoot 'scratch-world\bin\Debug\net10.0\ScratchWorld.exe'
if (-not (Test-Path $generator)) { Write-Error "Generator not built: $generator"; exit 1 }

# ---- assemble the run folder ----
New-Item -ItemType Directory -Force $runDir | Out-Null
if ($Reset) {
    foreach ($p in 'Server.MirDB', 'Server.MirDBn', 'SystemData.db', 'SystemData.db.tmp', 'Server.MirADB', 'Server.MirADBn', 'Logs', 'Back Up', 'Maps', 'Guilds', 'Conquests', 'Archive') {
        $full = Join-Path $runDir $p
        if (Test-Path $full) { Remove-Item $full -Recurse -Force }
    }
}
# Copy (never mirror) the build over the run folder; robocopy exit codes < 8 are success.
& robocopy $serverBuild $runDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Error "robocopy failed ($LASTEXITCODE)"; exit 1 }

if (-not (Test-Path (Join-Path $runDir 'Server.MirDB'))) {
    if ($MapsFrom) {
        if (-not (Test-Path $MapsFrom)) { Write-Error "MapsFrom folder not found: $MapsFrom"; exit 1 }
        & $generator --out $runDir --maps-from $MapsFrom --maps $Maps --force
    } else {
        & $generator --out $runDir --size $MapSize --maps $Maps --force
    }
    if ($LASTEXITCODE -ne 0) { Write-Error 'World generation failed.'; exit 1 }
}

$configs = Join-Path $runDir 'Configs'
New-Item -ItemType Directory -Force $configs | Out-Null
$mt = if ($Multithreaded) { 'True' } else { 'False' }
$ini = @"
; SCRATCH / LOAD-TEST PROFILE written by tools\start-scratch-server.ps1. NEVER use on a real server.
[General]
CheckVersion=False
EnforceDBChecks=False
Multithreaded=$mt
ThreadLimit=2
[Network]
StatusPortEnabled=False
IPAddress=127.0.0.1
Port=$Port
MaxUser=$MaxUser
MaxIP=5
MaxPacket=50
IPBlockSeconds=5
StartHTTPService=False
[LoadTest]
ExemptLoopback=True
LoopbackMaxPacket=$LoopbackMaxPacket
[Permission]
AllowNewAccount=True
AllowLogin=True
AllowNewCharacter=True
AllowStartGame=True
[WebAdmin]
Enabled=False
[Diag]
LogLoopProfile=True
"@
if ($ExtraIni.Count -gt 0) { $ini = $ini + "`r`n" + ($ExtraIni -join "`r`n") }
Set-Content -Path (Join-Path $configs 'Setup.ini') -Value $ini -Encoding ASCII

# ---- start ----
$exe = Join-Path $runDir 'Server.exe'
$proc = Start-Process -FilePath $exe -WorkingDirectory $runDir -WindowStyle Minimized -PassThru
Set-Content -Path $pidFile -Value @($proc.Id, $exe) -Encoding ASCII
Write-Host "Started scratch Server.exe PID $($proc.Id) on 127.0.0.1:$Port (folder $runDir)"

$logDir = Join-Path $runDir 'Logs\Server'
$deadline = (Get-Date).AddSeconds($StartTimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { Write-Error "Server.exe exited early (code $($proc.ExitCode)). See $logDir"; exit 1 }
    $log = Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $log) { continue }
    $text = Get-Content $log.FullName -Raw -ErrorAction SilentlyContinue
    if ($text -match 'Cannot start server') { Write-Error "World refused to start: see $($log.FullName)"; exit 1 }
    if ($text -match 'Network Started\.') {
        Write-Host "World and network started ($($log.FullName))." -ForegroundColor Green
        exit 0
    }
}
Write-Error "Timed out waiting for 'Network Started.' in $logDir (server still running, PID $($proc.Id))."
exit 1
