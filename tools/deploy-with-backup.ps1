<#
.SYNOPSIS
  Backup-first deploy of a scratch build into a run folder (roadmap P0). USER-RUN ONLY.
  Agents must never run this against Build\ (AGENTS.md: never touch Build\).

.DESCRIPTION
  1. Refuses to run while Server.exe / Client.exe is running from the target folder.
  2. Snapshots the target's DATA (the protected paths below) into -BackupRoot\<timestamp>\.
  3. Copies every file from -Source into -Target, skipping protected paths.
  4. Deletes ONLY files that the PREVIOUS deploy's manifest listed and the new build no
     longer contains (e.g. net10 runtimeconfig/deps files on a rollback to net8).
     It never mirrors folders and never deletes anything not in the old manifest.
  5. Writes the new manifest to <Target>\.deploy-manifest.txt.

  Protected (never copied over, never deleted):
    Server: Server.MirDB*, Server.MirADB* (incl. n/o temp files and Server.MirADB.imported), SystemData.db*, UserData.db*
            (incl. UserData.db-journal and UserData.db.tmp), Guilds\, Guilds.imported\, Conquests\, Conquests.imported\, Archive\,
            Back Up\, WebAdmin\admins.dat, Exports\, Configs\, Envir\ (incl. Envir\Goods.imported\), Maps\, Logs\
    Client: Data\, Sound\, Map\, Screenshots\, Mir2Config.ini, Mir2Test.ini, KeyBinds.ini, Error.txt

.PARAMETER Source      Scratch build folder (e.g. the -o folder used by tools\verify.ps1).
.PARAMETER Target      Run folder, e.g. C:\Mir2\Build\Server\Release (live since 2026-10-11).
.PARAMETER Kind        Server or Client (selects the protected list and the process check).
.PARAMETER BackupRoot  Where snapshots go. Should be OUTSIDE the target (ideally another disk).
.PARAMETER DryRun      Print what would happen; change nothing.

.EXAMPLE
  # 1. build:   powershell -File tools\verify.ps1 -Only Server -NoTests -Scratch D:\mir2-out
  # 2. preview: powershell -File tools\deploy-with-backup.ps1 -Kind Server -Source D:\mir2-out\Server -Target C:\Mir2\Build\Server\Release -BackupRoot D:\Mir2Backups -DryRun
  # 3. deploy:  (same without -DryRun), with the world stopped after a manual save.
#>
param(
    [Parameter(Mandatory)] [string]$Source,
    [Parameter(Mandatory)] [string]$Target,
    [Parameter(Mandatory)] [ValidateSet('Server', 'Client')] [string]$Kind,
    [Parameter(Mandatory)] [string]$BackupRoot,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$Source = [IO.Path]::GetFullPath($Source).TrimEnd('\')
$Target = [IO.Path]::GetFullPath($Target).TrimEnd('\')
$BackupRoot = [IO.Path]::GetFullPath($BackupRoot).TrimEnd('\')

if (-not (Test-Path $Source -PathType Container)) { throw "Source '$Source' not found." }
if (-not (Test-Path $Target -PathType Container)) { throw "Target '$Target' not found." }
if ($BackupRoot.StartsWith($Target + '\', [StringComparison]::OrdinalIgnoreCase) -or $BackupRoot -ieq $Target) {
    throw "BackupRoot must be outside the target folder."
}

$protectedDirs = @{
    'Server' = @('Guilds', 'Guilds.imported', 'Conquests', 'Conquests.imported', 'Archive', 'Back Up', 'Exports', 'Configs', 'Envir', 'Maps', 'Logs')
    'Client' = @('Data', 'Sound', 'Map', 'Screenshots')
}[$Kind]
$protectedFiles = @{
    'Server' = @('WebAdmin\admins.dat')
    'Client' = @('Mir2Config.ini', 'Mir2Test.ini', 'KeyBinds.ini', 'Error.txt')
}[$Kind]
$protectedPrefixes = @{
    'Server' = @('Server.MirDB', 'Server.MirADB', 'SystemData.db', 'UserData.db')   # root files incl. Server.MirADBn / Server.MirADBo / Server.MirADB.imported, SystemData.db.tmp (roadmap P7), UserData.db-journal / UserData.db.tmp (UserData-and-Quests-Plan.md Part A)
    'Client' = @()
}[$Kind]

function Test-Protected([string]$rel) {
    foreach ($d in $protectedDirs) {
        if ($rel -ieq $d -or $rel.StartsWith($d + '\', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    foreach ($f in $protectedFiles) { if ($rel -ieq $f) { return $true } }
    if ($rel -notmatch '\\') {
        foreach ($p in $protectedPrefixes) { if ($rel.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
    }
    if ($rel -ieq '.deploy-manifest.txt') { return $true }
    return $false
}

function Get-RelFiles([string]$root) {
    Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { $_.FullName.Substring($root.Length + 1) }
}

# 1. Process check
$exe = if ($Kind -eq 'Server') { 'Server' } else { 'Client' }
$running = Get-Process -Name $exe -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and ([IO.Path]::GetDirectoryName($_.Path) -ieq $Target) } catch { $false }
}
if ($running) { throw "$exe.exe is running from '$Target'. Stop it (after a manual save) first." }

$stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
$snap = Join-Path $BackupRoot "$Kind-$stamp"

# 2. Snapshot data
$targetFiles = @(Get-RelFiles $Target)
$dataFiles = @($targetFiles | Where-Object { Test-Protected $_ })
Write-Host "Snapshot: $($dataFiles.Count) data files -> $snap"
if (-not $DryRun) {
    foreach ($rel in $dataFiles) {
        $dst = Join-Path $snap $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Target $rel) -Destination $dst
    }
    # also keep the outgoing binaries, so a binary rollback is a copy back
    $binSnap = Join-Path $snap '_binaries'
    foreach ($rel in ($targetFiles | Where-Object { -not (Test-Protected $_) })) {
        $dst = Join-Path $binSnap $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Target $rel) -Destination $dst
    }
}

# 3. Copy new build
$newFiles = @(Get-RelFiles $Source | Where-Object { -not (Test-Protected $_) })
$skipped = @(Get-RelFiles $Source | Where-Object { Test-Protected $_ })
if ($skipped.Count -gt 0) { Write-Host "Not copied (protected): $($skipped -join ', ')" -ForegroundColor Yellow }
Write-Host "Copy: $($newFiles.Count) files"
if (-not $DryRun) {
    foreach ($rel in $newFiles) {
        $dst = Join-Path $Target $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
        Copy-Item -LiteralPath (Join-Path $Source $rel) -Destination $dst -Force
    }
}

# 4. Remove files only the previous manifest listed
$manifestPath = Join-Path $Target '.deploy-manifest.txt'
$oldManifest = @()
if (Test-Path $manifestPath) { $oldManifest = @(Get-Content $manifestPath | Where-Object { $_ }) }
else { Write-Host 'No previous manifest: nothing will be deleted (first managed deploy).' -ForegroundColor Yellow }
$newSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($f in $newFiles) { [void]$newSet.Add($f) }
$toDelete = @($oldManifest | Where-Object { -not $newSet.Contains($_) -and -not (Test-Protected $_) })
foreach ($rel in $toDelete) {
    try {
        if ($rel.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0 -or [IO.Path]::IsPathRooted($rel) -or $rel -match '(^|\\)\.\.(\\|$)') {
            Write-Host "Skipping suspicious manifest entry: '$rel'" -ForegroundColor Yellow; continue
        }
        $p = Join-Path $Target $rel
        if (Test-Path -LiteralPath $p) {
            Write-Host "Delete (no longer in build): $rel"
            if (-not $DryRun) { Remove-Item -LiteralPath $p -Force }
        }
    }
    catch { Write-Host "Could not process manifest entry '$rel': $($_.Exception.Message)" -ForegroundColor Yellow }
}

# 5. Manifest
if (-not $DryRun) { $newFiles | Sort-Object | Set-Content -Path $manifestPath -Encoding UTF8 }
Write-Host ($(if ($DryRun) { 'DRY RUN complete: nothing changed.' } else { "Deployed. Snapshot: $snap" })) -ForegroundColor Green
