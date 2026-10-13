<#
.SYNOPSIS
  Runs the PUBLIC WebRelay in the foreground (Ctrl+C stops it): https://worldofwooolcraft.uk/portal/mir2webclient/.

.DESCRIPTION
  The public relay sits behind the Cloudflare tunnel (route /portal/mir2webclient -> http://127.0.0.1:5695, HTTP Host Header
  127.0.0.1:5695) and bridges browsers to the LIVE Crystal server. It differs from the private (dev) relay, tools\start-webrelay.ps1:
    - serves the published snapshot WebClient\publish\public (tools\publish-webclient.ps1), never the working tree;
    - lives under the mount /portal/mir2webclient (the tunnel forwards paths unstripped);
    - accepts only the Origin https://worldofwooolcraft.uk;
    - keys its limits on CF-Connecting-IP (trusted from loopback peers only, i.e. cloudflared on this machine);
    - --public: re-applies the server's per-IP failed-login / account / character limits per player address.
  Every web player reaches the server from 127.0.0.1, so the server must run with [LoadTest] ExemptLoopback=True; this script
  reads the server's Setup.ini (read only) and refuses to start without it. If the server has CheckVersion=True, the relay
  answers with the MD5 of the server's own VersionPath file.
  Builds the relay to WebRelay\bin\public (not the dev relay's bin\Debug); stop a running public relay before re-running.
  Reads Build\ only: the client folder (Data\, Map\, Sound\) and the server's Setup.ini.

.PARAMETER ServerFolder  The live server's run folder (Configs\Setup.ini). Default C:\Mir2\Build\Server\Release.
.PARAMETER ClientData    Client folder holding Data\, Map\ and Sound\. Default C:\Mir2\Build\Client\Release (read only).
.PARAMETER Upstream      Game server host:port. Default: 127.0.0.1 and the server's [Network] Port.
.PARAMETER Listen        host:port the tunnel points at. Default 127.0.0.1:5695.
.PARAMETER Mount         URL prefix. Default /portal/mir2webclient.
.PARAMETER Origin        The site's origin. Default https://worldofwooolcraft.uk.
.PARAMETER SkipServerCheck  Start even when the server's Setup.ini does not have ExemptLoopback=True (then at most MaxIP web players, and
                         one player's failed logins block every web player).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\publish-webclient.ps1
  powershell -ExecutionPolicy Bypass -File tools\start-public-webrelay.ps1
#>
param(
    [string]$ServerFolder = 'C:\Mir2\Build\Server\Release',
    [string]$ClientData = 'C:\Mir2\Build\Client\Release',
    [string]$Upstream = '',
    [string]$Listen = '127.0.0.1:5695',
    [string]$Mount = '/portal/mir2webclient',
    [string]$Origin = 'https://worldofwooolcraft.uk',
    [switch]$SkipServerCheck
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$webRoot = Join-Path $repo 'WebClient\publish\public'
if (-not (Test-Path (Join-Path $webRoot 'play\index.html'))) { throw "No published web client at ${webRoot}: run tools\publish-webclient.ps1 first" }
if (-not (Test-Path (Join-Path $ClientData 'Data'))) { throw "Client folder $ClientData has no Data\" }

# The server's Setup.ini, read only: [Section] Key=Value.
function Read-Ini([string]$path) {
    $ini = @{}; $section = ''
    foreach ($line in Get-Content -LiteralPath $path) {
        $t = $line.Trim()
        if ($t -match '^\[(.+)\]$') { $section = $Matches[1]; continue }
        if ($t -match '^([^=;#]+)=(.*)$') { $ini["$section.$($Matches[1].Trim())"] = $Matches[2].Trim() }
    }
    return $ini
}
$setupPath = Join-Path $ServerFolder 'Configs\Setup.ini'
if (-not (Test-Path $setupPath)) { throw "Server settings not found: $setupPath (pass -ServerFolder)" }
$setup = Read-Ini $setupPath

if (-not $Upstream) {
    $port = $setup['Network.Port']; if (-not $port) { $port = '7000' }
    $Upstream = "127.0.0.1:$port"
}

if ($setup['LoadTest.ExemptLoopback'] -ne 'True') {
    $msg = "The server ($setupPath) does not have [LoadTest] ExemptLoopback=True. Every web player connects from 127.0.0.1, so MaxIP=$($setup['Network.MaxIP']) would cap ALL web players, and one player's failed logins or 4th account would IP-block every web player. Stop the server, set it, start the server (Knowledgebase/Runbook.md §8)."
    if (-not $SkipServerCheck) { throw $msg }
    Write-Warning $msg
}

$versionPath = ''
if ($setup['General.CheckVersion'] -eq 'True') {
    $vp = $setup['General.VersionPath']
    if (-not $vp) { throw 'The server has CheckVersion=True but no VersionPath' }
    $versionPath = if ([System.IO.Path]::IsPathRooted($vp)) { $vp } else { [System.IO.Path]::GetFullPath((Join-Path $ServerFolder $vp)) }
    if (-not (Test-Path $versionPath)) { throw "The server's VersionPath $versionPath does not exist" }
    Write-Host "Version check: the relay answers with the MD5 of $versionPath"
}

$out = Join-Path $repo 'WebRelay\bin\public'
& dotnet build (Join-Path $repo 'WebRelay\WebRelay.csproj') -o $out -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'WebRelay build failed (a public relay still running locks bin\public: stop it first)' }

$stamp = Join-Path $webRoot 'publish-stamp.json'
if (Test-Path $stamp) { $s = Get-Content $stamp -Raw | ConvertFrom-Json; Write-Host "Serving web client $($s.commit) published $($s.published)" }

$relayArgs = @('--listen', $Listen, '--upstream', $Upstream, '--client-data', $ClientData, '--web-root', $webRoot,
    '--mount', $Mount, '--origin', $Origin, '--trust-proxy-header', 'CF-Connecting-IP', '--public')
if ($versionPath) { $relayArgs += @('--version-path', $versionPath) }
& (Join-Path $out 'WebRelay.exe') @relayArgs
