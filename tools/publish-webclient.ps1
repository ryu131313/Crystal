<#
.SYNOPSIS
  Publishes the web client's PUBLIC snapshot, served by the public relay (tools\start-public-webrelay.ps1) at
  https://worldofwooolcraft.uk/portal/mir2webclient/.

.DESCRIPTION
  The public relay serves THIS copy, never the working tree, so dev edits and half-finished work never reach players;
  re-run to publish (Knowledgebase/WebClient/webclient.md §5).
    1. Builds the web client (npm run build = tsc) into WebClient\js\.
    2. Copies WebClient\play\ (no .ts) and WebClient\js\**\*.js into WebClient\publish\public.next\ (no .ts, no source maps).
    3. Writes publish-stamp.json beside them (git HEAD, dirty flag, time, file count).
    4. Swaps public.next into WebClient\publish\public (the previous copy is kept as public.old). The relay reads files per
       request, so no restart is needed; players get the new client on their next page load.
  Writes only under WebClient\ (publish\ is git-ignored). Never touches Build\.

.PARAMETER SkipBuild  Publish the existing WebClient\js\ without running tsc.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\publish-webclient.ps1
#>
param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$web = Join-Path $repo 'WebClient'
$dest = Join-Path $web 'publish\public'
$next = "$dest.next"
$old = "$dest.old"

if (-not $SkipBuild) {
    Push-Location $web
    try {
        if (-not (Test-Path 'node_modules')) { & npm install --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { throw 'npm install failed' } }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw 'tsc failed: nothing published' }
    } finally { Pop-Location }
}
if (-not (Test-Path (Join-Path $web 'js\play\main.js'))) { throw 'WebClient\js\play\main.js is missing: build first' }

if (Test-Path $next) { Remove-Item $next -Recurse -Force }
New-Item -ItemType Directory -Path $next | Out-Null

# play\: index.html, play.css, lights\ (the .ts entry is compiled into js\play\). Not lab.html: the scene lab is a dev tool.
$playSrc = Join-Path $web 'play'
Get-ChildItem $playSrc -Recurse -File | Where-Object { $_.Extension -ne '.ts' -and $_.Name -ne 'lab.html' } | ForEach-Object {
    $target = Join-Path (Join-Path $next 'play') $_.FullName.Substring($playSrc.Length + 1)
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Copy-Item $_.FullName $target
}
# js\: the compiled ES modules only.
$jsSrc = Join-Path $web 'js'
$jsCount = 0
Get-ChildItem $jsSrc -Recurse -File -Filter '*.js' | ForEach-Object {
    $target = Join-Path (Join-Path $next 'js') $_.FullName.Substring($jsSrc.Length + 1)
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Copy-Item $_.FullName $target
    $jsCount++
}

$head = (& git -C $repo rev-parse --short HEAD) 2>$null
$dirty = [bool](& git -C $repo status --porcelain -- WebClient/src WebClient/play 2>$null)
[ordered]@{
    commit    = "$head"
    dirty     = $dirty
    published = (Get-Date).ToString('o')
    jsModules = $jsCount
} | ConvertTo-Json | Set-Content -Path (Join-Path $next 'publish-stamp.json') -Encoding utf8

# Swap. A request may hold a file open for a moment: retry.
if (Test-Path $old) { Remove-Item $old -Recurse -Force }
for ($try = 1; ; $try++) {
    try {
        if (Test-Path $dest) { Rename-Item $dest (Split-Path $old -Leaf) }
        Rename-Item $next (Split-Path $dest -Leaf)
        break
    } catch {
        if ($try -ge 10) { throw "Could not swap $next into place: $($_.Exception.Message)" }
        Start-Sleep -Milliseconds 300
    }
}

$note = if ($dirty) { ' (uncommitted web client changes included)' } else { '' }
Write-Host "Published $jsCount modules from $head$note to $dest" -ForegroundColor Green
Write-Host 'Players get it on their next page load; the public relay needs no restart.'
