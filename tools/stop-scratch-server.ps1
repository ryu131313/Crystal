<#
.SYNOPSIS
  Stops the scratch Server.exe started by tools\start-scratch-server.ps1 (and nothing else).

.DESCRIPTION
  Reads <Scratch>\server.pid, checks that the process with that PID is Server.exe AND that its
  executable lives inside <Scratch>, then asks it to close (CloseMainWindow -> the normal
  FormClosing -> Envir.Stop path, which saves). If it has not exited after -GraceSeconds it is
  killed. Any other process (for example a live Server.exe on this machine) is never touched.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\stop-scratch-server.ps1
#>
param(
    [string]$Scratch = (Join-Path $env:TEMP 'mir2-p6-server'),
    [int]$GraceSeconds = 20
)

$ErrorActionPreference = 'Stop'
$scratchFull = [IO.Path]::GetFullPath($Scratch).TrimEnd('\') + '\'
$pidFile = Join-Path $scratchFull 'server.pid'

if (-not (Test-Path $pidFile)) { Write-Host "No $pidFile - nothing to stop."; exit 0 }
$lines = @(Get-Content $pidFile)
$procId = [int]$lines[0]

$proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
if (-not $proc) {
    Write-Host "PID $procId is not running; removing $pidFile."
    Remove-Item $pidFile -Force
    exit 0
}

$path = $null
try { $path = $proc.MainModule.FileName } catch { }
if (-not $path) {
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $procId" -ErrorAction SilentlyContinue
    if ($cim) { $path = $cim.ExecutablePath }
}
if (-not $path -or -not ([IO.Path]::GetFullPath($path)).StartsWith($scratchFull, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFileName($path) -ieq 'Server.exe')) {
    Write-Error "PID $procId is '$path', not a Server.exe inside $scratchFull. Refusing to stop it (the PID was probably reused). Delete $pidFile if it is stale."
    exit 2
}

Write-Host "Stopping scratch Server.exe PID $procId ($path)"
[void]$proc.CloseMainWindow()
if (-not $proc.WaitForExit($GraceSeconds * 1000)) {
    Write-Host "Did not exit within $GraceSeconds s; killing PID $procId."
    $proc.Kill()
    [void]$proc.WaitForExit(10000)
}
Remove-Item $pidFile -Force
Write-Host 'Stopped.'
exit 0
