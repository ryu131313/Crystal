<#
.SYNOPSIS
  Removes the legacy [QUESTS] section from NPC scripts (ignored since quest V2 wave 4).

.DESCRIPTION
  Copies the whole NPC folder to -BackupDir first, then rewrites every *.txt that has a line starting with [QUESTS]
  (any case, as NPCScript.ParseQuests matched it). The section runs to the next line starting with '['; blank lines at its
  end are kept so the following section stays separated. Files are processed as raw bytes (Latin-1 round trip), so the
  encoding, BOM and line endings of each file are left exactly as they were.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\strip-npc-quest-sections.ps1 -NpcPath "Build\Server\Release\Envir\NPCs" -BackupDir "Backup\npcs-2026-10-29"
#>
param(
    [Parameter(Mandatory = $true)][string]$NpcPath,
    [Parameter(Mandatory = $true)][string]$BackupDir,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$NpcPath = (Resolve-Path $NpcPath).Path
if (Test-Path $BackupDir) { throw "Backup folder already exists: $BackupDir" }

$latin1 = [System.Text.Encoding]::GetEncoding(28591)
$header = New-Object regex '^\[QUESTS\]', 'IgnoreCase'
$sectionStart = [regex]'^\['

if (-not $WhatIf) {
    Copy-Item -Path $NpcPath -Destination $BackupDir -Recurse
    Write-Host "Backed up $NpcPath to $((Resolve-Path $BackupDir).Path)"
}

$changed = 0
foreach ($file in Get-ChildItem -Path $NpcPath -Filter *.txt -Recurse -File) {
    $text = $latin1.GetString([System.IO.File]::ReadAllBytes($file.FullName))
    $lines = [regex]::Split($text, '(?<=\n)')
    $content = { param($l) $l.TrimEnd("`r", "`n") }

    $out = New-Object System.Collections.Generic.List[string]
    $removed = $false
    $i = 0
    while ($i -lt $lines.Count) {
        if (-not $header.IsMatch((& $content $lines[$i]))) { $out.Add($lines[$i]); $i++; continue }

        $end = $i + 1
        while ($end -lt $lines.Count -and -not $sectionStart.IsMatch((& $content $lines[$end]))) { $end++ }
        $keep = $end
        while ($keep -gt $i + 1 -and (& $content $lines[$keep - 1]).Trim() -eq '') { $keep-- }
        for ($k = $keep; $k -lt $end; $k++) { $out.Add($lines[$k]) }
        $removed = $true
        $i = $end
    }

    if (-not $removed) { continue }
    $changed++
    $relative = $file.FullName.Substring($NpcPath.Length).TrimStart('\')
    if ($WhatIf) { Write-Host "Would strip $relative"; continue }
    [System.IO.File]::WriteAllBytes($file.FullName, $latin1.GetBytes(($out -join '')))
    Write-Host "Stripped $relative"
}

Write-Host "$changed file(s) $(if ($WhatIf) { 'would be ' })changed."
