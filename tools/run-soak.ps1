<#
.SYNOPSIS
  Stability soak (roadmap P6/P9 gate) against an isolated scratch server. Never touches Build\ or a live server.

.DESCRIPTION
  Starts a fresh scratch server with -Maps synthetic maps (new characters pick a random start point, so bots spread
  across maps), runs -Bots StressClient bots for -Minutes, samples the server process every -SampleSeconds (CPU over
  the interval, working set, private bytes, handles, threads) into a CSV, then writes a markdown report:
  memory trend after warm-up, first vs last 10 minutes of the [Diag] Loop profile, isolated errors, FATAL lines,
  crash files and the bot summary.

  Leak / stability signals (hold on a busy machine): memory slope after warm-up, handle/thread growth, isolated
  errors, crash files, bot errors. CPU numbers are only comparable on a quiet machine.

.PARAMETER Bots           Default 300 (the roadmap's peak target).
.PARAMETER Maps           Default 4.
.PARAMETER Minutes        Default 60.
.PARAMETER SampleSeconds  Default 60.
.PARAMETER Port           Default 17000.
.PARAMETER Out            Report path. Default Knowledgebase\Future Optimisation\Soak-<date>.md
#>
param(
    [int]$Bots = 300,
    [ValidateRange(1, 20)][int]$Maps = 4,
    [int]$Minutes = 60,
    [int]$SampleSeconds = 60,
    [int]$Port = 17000,
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$start = Join-Path $PSScriptRoot 'start-scratch-server.ps1'
$stop = Join-Path $PSScriptRoot 'stop-scratch-server.ps1'
$scratch = Join-Path $env:TEMP 'mir2-p6-server'
$runDir = Join-Path $scratch 'run'
$pidFile = Join-Path $scratch 'server.pid'
if (-not $Out) { $Out = Join-Path $repo ("Knowledgebase\Future Optimisation\Soak-" + (Get-Date -Format 'yyyy-MM-dd') + ".md") }
$csv = [IO.Path]::ChangeExtension($Out, '.csv')

& dotnet build (Join-Path $repo 'StressClient\StressClient.csproj') -nologo -v:q | Out-Null
$stress = Join-Path $repo 'StressClient\bin\Debug\net10.0\StressClient.exe'

& powershell -NoProfile -ExecutionPolicy Bypass -File $stop 2>$null | Out-Null
& $start -Port $Port -Reset -Maps $Maps | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'scratch server failed to start' }

$id = [int](Get-Content $pidFile | Select-Object -First 1)
$p = Get-Process -Id $id
if ($p.Path -notlike "$scratch*") { throw "PID $id is not the scratch server" }

$botOut = Join-Path $scratch "soak-bots.txt"
$botArgs = "--port $Port --bots $Bots --ramp 25 --duration $($Minutes * 60 + 30) --prefix soak"
$botProc = Start-Process -FilePath $stress -ArgumentList $botArgs -PassThru -WindowStyle Hidden -RedirectStandardOutput $botOut
$t0 = Get-Date

$samples = @()
"minute,cpu_pct_one_core,ws_mb,private_mb,handles,threads" | Set-Content -Path $csv -Encoding ASCII
try {
    $p.Refresh(); $cpuPrev = $p.TotalProcessorTime; $tPrev = Get-Date
    while (((Get-Date) - $t0).TotalMinutes -lt $Minutes) {
        Start-Sleep -Seconds $SampleSeconds
        $p.Refresh()
        if ($p.HasExited) { throw "scratch server exited during the soak (code $($p.ExitCode))" }
        $now = Get-Date
        $cpu = [math]::Round((($p.TotalProcessorTime - $cpuPrev).TotalMilliseconds / ($now - $tPrev).TotalMilliseconds) * 100, 1)
        $cpuPrev = $p.TotalProcessorTime; $tPrev = $now
        $s = [pscustomobject]@{
            Minute = [math]::Round(($now - $t0).TotalMinutes, 1); Cpu = $cpu
            Ws = [math]::Round($p.WorkingSet64 / 1MB, 1); Private = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
            Handles = $p.HandleCount; Threads = $p.Threads.Count
        }
        $samples += $s
        ("{0},{1},{2},{3},{4},{5}" -f $s.Minute, $s.Cpu, $s.Ws, $s.Private, $s.Handles, $s.Threads) | Add-Content -Path $csv -Encoding ASCII
    }
    $botProc.WaitForExit(180000) | Out-Null
}
finally {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $stop 2>$null | Out-Null
}

# ---- loop profile: first vs last 10 minutes ----
function Get-Diag([datetime]$from, [datetime]$to) {
    $dbg = Get-ChildItem (Join-Path $runDir 'Logs\Debug') -Filter *.log -ErrorAction SilentlyContinue
    $keys = 'busy', 'conn', 'send', 'obj', 'mob', 'alloc', 'pout'
    $sum = @{}; $n = 0; $max = 0.0
    foreach ($f in $dbg) {
        foreach ($l in (Get-Content $f.FullName | Where-Object { $_ -match '\[Diag\] Loop' })) {
            $t = [datetime]::MinValue
            if (-not ([datetime]::TryParse($l.Substring(0, 19), [ref]$t)) -or $t -lt $from -or $t -gt $to) { continue }
            $n++
            foreach ($k in $keys) { if ($l -match "\b$k=([0-9.,]+)") { $sum[$k] = $sum[$k] + [double]($Matches[1] -replace ',', '') } }
            if ($l -match 'max=([0-9.]+)ms') { $max = [math]::Max($max, [double]$Matches[1]) }
        }
    }
    if ($n -eq 0) { return $null }
    $r = @{ samples = $n; max = $max }
    foreach ($k in $keys) { $r[$k] = [math]::Round($sum[$k] / $n, 2) }
    return $r
}
$rampEnd = $t0.AddSeconds([math]::Ceiling($Bots / 25) + 30)
$early = Get-Diag $rampEnd ($rampEnd.AddMinutes(10))
$late = Get-Diag ($t0.AddMinutes($Minutes - 10)) ($t0.AddMinutes($Minutes))

# ---- memory trend after warm-up (least-squares slope, MB per hour) ----
function Get-Slope($xs, $ys) {
    $n = $xs.Count; if ($n -lt 3) { return $null }
    $mx = ($xs | Measure-Object -Average).Average; $my = ($ys | Measure-Object -Average).Average
    $num = 0.0; $den = 0.0
    for ($i = 0; $i -lt $n; $i++) { $num += ($xs[$i] - $mx) * ($ys[$i] - $my); $den += ($xs[$i] - $mx) * ($xs[$i] - $mx) }
    if ($den -eq 0) { return $null }
    return [math]::Round(($num / $den) * 60, 1)
}
$warm = @($samples | Where-Object { $_.Minute -ge 10 })
$wsSlope = Get-Slope @($warm | ForEach-Object { $_.Minute }) @($warm | ForEach-Object { $_.Ws })
$pvSlope = Get-Slope @($warm | ForEach-Object { $_.Minute }) @($warm | ForEach-Object { $_.Private })

$isolated = (Select-String -Path (Join-Path $runDir 'Logs\Server\*.log') -Pattern '\[World\] Isolated error' -ErrorAction SilentlyContinue | Measure-Object).Count
$fatal = (Select-String -Path (Join-Path $runDir 'Logs\Server\*.log') -Pattern 'FATAL' -ErrorAction SilentlyContinue | Measure-Object).Count
$encodeWarn = (Select-String -Path (Join-Path $runDir 'Logs\Server\*.log') -Pattern 'EncodeOnce:' -ErrorAction SilentlyContinue | Measure-Object).Count
$crash = (Get-ChildItem (Join-Path $runDir 'Logs\Crash') -ErrorAction SilentlyContinue | Measure-Object).Count
$botTail = (Get-Content $botOut -ErrorAction SilentlyContinue | Select-Object -Last 5) -join "`n"

$first = $samples | Select-Object -First 1; $last = $samples | Select-Object -Last 1
$atWarm = $warm | Select-Object -First 1
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Soak - $(Get-Date -Format 'yyyy-MM-dd HH:mm') - $Bots bots, $Maps maps, $Minutes min")
[void]$sb.AppendLine('')
[void]$sb.AppendLine("> Scratch server (synthetic ${Maps}x 200x200 maps), net10, default [Optimisation] settings. Bots share the machine. Produced by ``tools/run-soak.ps1``; per-minute samples in ``$([IO.Path]::GetFileName($csv))``.")
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| Check | Result |')
[void]$sb.AppendLine('|---|---|')
[void]$sb.AppendLine("| Isolated errors / FATAL lines / crash files | $isolated / $fatal / $crash |")
[void]$sb.AppendLine("| EncodeOnce mutation warnings | $encodeWarn |")
if ($first -and $last) {
    [void]$sb.AppendLine("| Working set MB (first / at 10 min / last) | $($first.Ws) / $($atWarm.Ws) / $($last.Ws) |")
    [void]$sb.AppendLine("| Private MB (first / at 10 min / last) | $($first.Private) / $($atWarm.Private) / $($last.Private) |")
    [void]$sb.AppendLine("| Memory slope after 10 min (MB/h, working set / private) | $wsSlope / $pvSlope |")
    [void]$sb.AppendLine("| Handles (first / last) | $($first.Handles) / $($last.Handles) |")
    [void]$sb.AppendLine("| Threads (first / last) | $($first.Threads) / $($last.Threads) |")
    $cpuAvg = [math]::Round((($samples | Where-Object { $_.Minute -ge 2 }) | Measure-Object -Property Cpu -Average).Average, 1)
    [void]$sb.AppendLine("| CPU % of one core (average after 2 min; busy machine = indicative) | $cpuAvg |")
}
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Loop profile averages ([Diag] Loop lines):')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| Window | samples | busy % | ms/s conn + send | ms/s obj | ms/s mob | alloc MB/s | pkts out/s | max iter ms |')
[void]$sb.AppendLine('|---|---|---|---|---|---|---|---|---|')
foreach ($w in @(@('first 10 min after ramp', $early), @('last 10 min', $late))) {
    $d = $w[1]
    if ($d) { [void]$sb.AppendLine("| $($w[0]) | $($d.samples) | $($d.busy) | $([math]::Round($d.conn + $d.send, 2)) | $($d.obj) | $($d.mob) | $([math]::Round($d.alloc / 1MB, 2)) | $($d.pout) | $($d.max) |") }
    else { [void]$sb.AppendLine("| $($w[0]) | 0 | (no [Diag] lines) | | | | | | |") }
}
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Bot summary:')
[void]$sb.AppendLine('```')
[void]$sb.AppendLine($botTail)
[void]$sb.AppendLine('```')
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
Set-Content -Path $Out -Value $sb.ToString() -Encoding UTF8
Write-Host "Report: $Out"
