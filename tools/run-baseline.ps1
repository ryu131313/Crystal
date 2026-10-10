<#
.SYNOPSIS
  P6 baseline matrix / regression run against an isolated scratch server (roadmap P6). Never touches Build\ or a live server.

.DESCRIPTION
  For each scenario: (re)start the scratch server via start-scratch-server.ps1, let it settle, optionally run N StressClient
  bots, measure the server process CPU over the window, and collect the "[Diag] Loop" lines the server writes to its
  Debug log (scratch profile sets [Diag] LogLoopProfile=True). Writes a markdown report.

  Scenarios (default): idle with Multithreaded on, idle with Multithreaded off, then 50 / 200 / 500 bots (Multithreaded on).

.PARAMETER Out        Report path. Default Knowledgebase\Future Optimisation\Baseline-<date>.md
.PARAMETER Seconds    Measurement window per scenario. Default 60.
.PARAMETER Bots       Bot counts to run. Default 50,200,500.
.PARAMETER Port       Scratch port. Default 17000.
.PARAMETER ExtraIni   Setup.ini lines added to every scenario (A/B runs), e.g. '[Optimisation]','EncodeOnce=False'.
.PARAMETER NoIdle     Skip the two idle scenarios.
.PARAMETER Label      Text added to the report heading.
#>
param(
    [string]$Out = '',
    [int]$Seconds = 60,
    [int[]]$Bots = @(50, 200, 500),
    [int]$Port = 17000,
    [string[]]$ExtraIni = @(),
    [switch]$NoIdle,
    [string]$Label = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$start = Join-Path $PSScriptRoot 'start-scratch-server.ps1'
$stop = Join-Path $PSScriptRoot 'stop-scratch-server.ps1'
$scratch = Join-Path $env:TEMP 'mir2-p6-server'
$runDir = Join-Path $scratch 'run'
$pidFile = Join-Path $scratch 'server.pid'
if (-not $Out) { $Out = Join-Path $repo ("Knowledgebase\Future Optimisation\Baseline-" + (Get-Date -Format 'yyyy-MM-dd') + ".md") }

& dotnet build (Join-Path $repo 'StressClient\StressClient.csproj') -nologo -v:q | Out-Null
$stress = Join-Path $repo 'StressClient\bin\Debug\net10.0\StressClient.exe'

function Get-ServerProcess {
    $id = [int](Get-Content $pidFile | Select-Object -First 1)
    $p = Get-Process -Id $id -ErrorAction Stop
    if ($p.Path -notlike "$scratch*") { throw "PID $id is not the scratch server" }
    return $p
}

function Get-DiagLines([datetime]$since) {
    $dbg = Get-ChildItem (Join-Path $runDir 'Logs\Debug') -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $dbg) { return @() }
    Get-Content $dbg.FullName | Where-Object { $_ -match '\[Diag\] Loop' } | Where-Object {
        $t = [datetime]::MinValue
        [datetime]::TryParse($_.Substring(0, 19), [ref]$t) -and $t -ge $since
    }
}

function Summarize($lines) {
    # average numeric fields across the window's lines
    $keys = 'it','busy','attributed','conn','mob','obj','maps','envir','admin','saves','park','other','pin','pout','bout','alloc'
    $sum = @{}; $n = 0; $maxIt = 0.0
    foreach ($l in $lines) {
        $n++
        foreach ($k in $keys) {
            if ($l -match "\b$k=([0-9.,]+)") { $v = [double]($Matches[1] -replace ',', ''); $sum[$k] = ($sum[$k] + $v) }
        }
        if ($l -match 'max=([0-9.]+)ms') { $maxIt = [math]::Max($maxIt, [double]$Matches[1]) }
    }
    if ($n -eq 0) { return $null }
    $avg = @{}; foreach ($k in $keys) { $avg[$k] = [math]::Round(($sum[$k] / $n), 2) }
    $avg['maxIterationMs'] = $maxIt; $avg['samples'] = $n
    return $avg
}

$results = @()
function Run-Scenario([string]$name, [bool]$mt, [int]$bots) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $stop 2>$null | Out-Null
    $reset = $script:results.Count -eq 0
    $startParams = @{ Port = $Port; Multithreaded = $mt; ExtraIni = $ExtraIni }
    if ($reset) { $startParams.Reset = $true } else { $startParams.NoBuild = $true }
    & $start @startParams | Out-Null   # same session: "exit" in the script only ends that script
    if ($LASTEXITCODE -ne 0) { throw "scratch server failed to start for $name" }
    Start-Sleep -Seconds 5

    $p = Get-ServerProcess
    $botProc = $null
    if ($bots -gt 0) {
        $botArgs = "--port $Port --bots $bots --ramp 25 --duration $($Seconds + 20) --prefix bl$bots"
        $botProc = Start-Process -FilePath $stress -ArgumentList $botArgs -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $scratch "bots-$bots.txt")
        Start-Sleep -Seconds ([math]::Ceiling($bots / 25) + 10) # ramp + settle
    }

    $p.Refresh(); $cpu0 = $p.TotalProcessorTime; $t0 = Get-Date
    Start-Sleep -Seconds $Seconds
    $p.Refresh(); $cpu1 = $p.TotalProcessorTime; $t1 = Get-Date
    $cpuPct = [math]::Round((($cpu1 - $cpu0).TotalMilliseconds / ($t1 - $t0).TotalMilliseconds) * 100, 1) # % of ONE core
    $ws = [math]::Round($p.WorkingSet64 / 1MB, 0)

    if ($botProc) { $botProc.WaitForExit(120000) | Out-Null }
    $diag = Summarize (Get-DiagLines $t0)
    $world = (Select-String -Path (Join-Path $runDir 'Logs\Server\*.log') -Pattern '\[World\] Isolated error' -ErrorAction SilentlyContinue | Measure-Object).Count
    $crash = (Get-ChildItem (Join-Path $runDir 'Logs\Crash') -ErrorAction SilentlyContinue | Measure-Object).Count
    $botTail = if ($bots -gt 0) { (Get-Content (Join-Path $scratch "bots-$bots.txt") -ErrorAction SilentlyContinue | Select-Object -Last 2) -join ' / ' } else { '' }
    $script:results += [pscustomobject]@{ Name = $name; MT = $mt; Bots = $bots; CpuPctOneCore = $cpuPct; WorkingSetMB = $ws; Diag = $diag; IsolatedErrors = $world; CrashFiles = $crash; BotTail = $botTail }
    Write-Host ("{0}: cpu {1}% of one core, it/s {2}, busy {3}" -f $name, $cpuPct, $diag.it, $diag.busy)
}

try {
    if (-not $NoIdle) {
        Run-Scenario 'idle, Multithreaded on' $true 0
        Run-Scenario 'idle, Multithreaded off' $false 0
    }
    foreach ($b in $Bots) { Run-Scenario "$b bots, Multithreaded on" $true $b }
}
finally {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $stop 2>$null | Out-Null
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Server baseline (roadmap P6) - $(Get-Date -Format 'yyyy-MM-dd HH:mm')$(if ($Label) { ' - ' + $Label })")
if ($ExtraIni.Count -gt 0) { [void]$sb.AppendLine(''); [void]$sb.AppendLine("Extra Setup.ini: ``$($ExtraIni -join ' ')``") }
[void]$sb.AppendLine('')
[void]$sb.AppendLine("> Scratch server (synthetic 200x200 map, tools/scratch-world), net10, idle park on, MobThreadOnePass on, ThreadLimit 2. Window $Seconds s per scenario. CPU is % of ONE core over the window. Bots and server share this machine. Produced by ``tools/run-baseline.ps1``.")
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| Scenario | CPU % (1 core) | WS MB | it/s | busy | attributed | ms/s conn | mob | obj | maps | envir | saves | park | pkts in/s | pkts out/s | KB out/s | alloc MB/s | max iter ms | isolated errors | crash files |')
[void]$sb.AppendLine('|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|')
foreach ($r in $results) {
    $d = $r.Diag
    if ($d) {
        [void]$sb.AppendLine(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} | {15} | {16} | {17} | {18} | {19} |" -f `
            $r.Name, $r.CpuPctOneCore, $r.WorkingSetMB, $d.it, $d.busy, $d.attributed, $d.conn, $d.mob, $d.obj, $d.maps, $d.envir, $d.saves, $d.park, $d.pin, $d.pout, [math]::Round($d.bout / 1KB, 1), [math]::Round($d.alloc / 1MB, 2), $d.maxIterationMs, $r.IsolatedErrors, $r.CrashFiles))
    } else {
        [void]$sb.AppendLine("| $($r.Name) | $($r.CpuPctOneCore) | $($r.WorkingSetMB) | (no [Diag] lines) | | | | | | | | | | | | | | | $($r.IsolatedErrors) | $($r.CrashFiles) |")
    }
}
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Bot client tail per run:')
foreach ($r in $results | Where-Object { $_.Bots -gt 0 }) { [void]$sb.AppendLine("- $($r.Bots) bots: $($r.BotTail)") }
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
Set-Content -Path $Out -Value $sb.ToString() -Encoding UTF8
Write-Host "Report: $Out"
