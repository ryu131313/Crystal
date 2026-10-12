<#
.SYNOPSIS
  Builds the web client (tsc) and runs WebRelay in the foreground (Ctrl+C stops it).

.DESCRIPTION
  WebRelay bridges the browser client (WebClient\) to an unchanged Crystal server and serves the client's Data\ and
  Map\ folders READ-ONLY (Knowledgebase/WebClient/webclient.md). It builds to its own bin\ and never writes to Build\.
  Open http://127.0.0.1:<port>/play/ when it says it is listening.

.PARAMETER Upstream    Game server host:port. Default 127.0.0.1:17000 (a scratch server from start-scratch-server.ps1).
.PARAMETER ClientData  Client folder holding Data\ and Map\. Default C:\Mir2\Build\Client\Release (read only).
.PARAMETER MapPath     Extra Maps folder, e.g. a scratch server's synthetic maps. Default: the P6 scratch run folder when present.
.PARAMETER VersionPath The client exe the server's VersionPath hashes; needed when the server has CheckVersion=True.
.PARAMETER Listen      host:port to listen on. Default 127.0.0.1:5690.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\start-webrelay.ps1
  powershell -ExecutionPolicy Bypass -File tools\start-webrelay.ps1 -Upstream 127.0.0.1:7100 -VersionPath C:\Mir2\Build\Client\Release\Client.exe
#>
param(
    [string]$Upstream = '127.0.0.1:17000',
    [string]$ClientData = 'C:\Mir2\Build\Client\Release',
    [string]$MapPath = (Join-Path $env:TEMP 'mir2-p6-server\run\Maps'),
    [string]$VersionPath = '',
    [string]$Listen = '127.0.0.1:5690'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$web = Join-Path $repo 'WebClient'

Push-Location $web
try {
    if (-not (Test-Path 'node_modules')) { & npm install --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { exit 1 } }
    & npx tsc -p tsconfig.build.json
    if ($LASTEXITCODE -ne 0) { Write-Error 'tsc failed'; exit 1 }
} finally { Pop-Location }

& dotnet build (Join-Path $repo 'WebRelay\WebRelay.csproj') -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { exit 1 }

$relayArgs = @('--listen', $Listen, '--upstream', $Upstream, '--client-data', $ClientData, '--web-root', $web)
if ($MapPath -and (Test-Path $MapPath)) { $relayArgs += @('--map-path', $MapPath) }
if ($VersionPath) { $relayArgs += @('--version-path', $VersionPath) }
& (Join-Path $repo 'WebRelay\bin\Debug\net10.0\WebRelay.exe') @relayArgs
