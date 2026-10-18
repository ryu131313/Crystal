<#
.SYNOPSIS
  Modern UI seam gate (Desktop-Modern-UI-Plan, phase 1a). Read-only.

.DESCRIPTION
  Greps Client\**\*.cs for every name in tools\modern-ui-gate.txt (whole word, code only: text after // is ignored), outside the
  classic-only folders Client\MirScenes\Dialogs\ and Client\MirScenes\Classic\ (and the test-only
  Client\RenderTest\UiTestRunner.cs). In modern mode the classic dialogs
  are never constructed, so every hit is classic UI reached from code that also runs in modern mode.
  Exit code 0 = gate clean, 1 = hits (listed).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\modern-ui-gate.ps1
#>
param(
    [switch]$Summary
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$client = Join-Path $repo 'Client'
$listPath = Join-Path $PSScriptRoot 'modern-ui-gate.txt'

$classicDirs = @(
    (Join-Path $client 'MirScenes\Dialogs\'),
    (Join-Path $client 'MirScenes\Classic\'),
    # Test only: the offline UI test (tools\uitest.ps1) inspects the classic dialogs in Classic runs and skips them in Modern.
    (Join-Path $client 'RenderTest\UiTestRunner.cs')
)

$names = @{}
foreach ($line in Get-Content -LiteralPath $listPath) {
    $t = $line.Trim()
    if ($t.Length -eq 0 -or $t.StartsWith('#')) { continue }
    $allowed = @()
    $idx = $t.IndexOf(':')
    if ($idx -ge 0) {
        $allowed = $t.Substring($idx + 1).Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
        $t = $t.Substring(0, $idx).Trim()
    }
    $names[$t] = @($allowed | ForEach-Object { Join-Path $client $_ })
}

$pattern = '\b(' + (($names.Keys | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'
$regex = [regex]::new($pattern)

$files = Get-ChildItem -LiteralPath $client -Recurse -Filter *.cs -File | Where-Object {
    $full = $_.FullName
    ($full -notmatch '\\(bin|obj)\\') -and -not ($classicDirs | Where-Object { $full.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })
}

$hits = New-Object System.Collections.Generic.List[string]
$perFile = @{}
foreach ($f in $files) {
    $n = 0
    foreach ($m in Select-String -LiteralPath $f.FullName -Pattern $pattern -CaseSensitive) {
        $code = $m.Line
        $cut = $code.IndexOf('//')
        if ($cut -ge 0) { $code = $code.Substring(0, $cut) }
        foreach ($match in $regex.Matches($code)) {
            $name = $match.Groups[1].Value
            if ($names[$name] -contains $f.FullName) { continue }
            $rel = $f.FullName.Substring($repo.Length + 1)
            $hits.Add(('{0}:{1}: {2}  | {3}' -f $rel, $m.LineNumber, $name, $m.Line.Trim()))
            $n++
        }
    }
    if ($n -gt 0) { $perFile[$f.FullName.Substring($repo.Length + 1)] = $n }
}

if ($hits.Count -eq 0) {
    Write-Host 'modern-ui-gate: clean'
    exit 0
}

if ($Summary) {
    $perFile.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { Write-Host ('{0,6}  {1}' -f $_.Value, $_.Key) }
} else {
    $hits | ForEach-Object { Write-Host $_ }
}
Write-Host ('modern-ui-gate: {0} hit(s)' -f $hits.Count)
exit 1
