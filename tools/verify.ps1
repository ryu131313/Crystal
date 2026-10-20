<#
.SYNOPSIS
  Safe compile + test gate for Crystal (roadmap P0). Never writes into Build\.

.DESCRIPTION
  Builds each project individually with `-o <scratch>\<name>` (never the .sln, never the
  project's own output path, which for Server/Client/editors is the LIVE Build\ folder),
  then runs every test project. Refuses to run if the scratch root is inside Build\.

.PARAMETER Scratch
  Root folder for build outputs. Default: %TEMP%\mir2-verify. Must not be under <repo>\Build.

.PARAMETER Only
  Optional list of project keys to build (see $Projects below). Default: all.

.PARAMETER NoTests
  Skip `dotnet test`.

.EXAMPLE
  powershell -File tools\verify.ps1
  powershell -File tools\verify.ps1 -Only Server,Client -NoTests
#>
param(
    [string]$Scratch = (Join-Path $env:TEMP 'mir2-verify'),
    [string[]]$Only = @(),
    [switch]$NoTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repo 'Build')).TrimEnd('\') + '\'
$scratchFull = [IO.Path]::GetFullPath($Scratch).TrimEnd('\') + '\'

if ($scratchFull.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Error "Refusing to build into '$Scratch': it is inside the live Build\ folder."
    exit 2
}

# key = project path (repo-relative). Order matters only for readability; each build is independent.
$Projects = [ordered]@{
    'Shared'             = 'Shared\Shared.csproj'
    'ServerLibrary'      = 'Server\Server.Library.csproj'
    'Server'             = 'Server.MirForms\Server.csproj'
    'Client'             = 'Client\Client.csproj'
    'LibraryEditor'      = 'LibraryEditor\LibraryEditor.csproj'
    'LibraryViewer'      = 'LibraryViewer\LibraryViewer.csproj'
    'MirDbExport'        = 'tools\MirDbExport\MirDbExport.csproj'
    'StressClient'       = 'StressClient\StressClient.csproj'
    'ScratchWorld'       = 'tools\scratch-world\ScratchWorld.csproj'
    'WebRelay'           = 'WebRelay\WebRelay.csproj'
}

# Test projects build to their own bin\ (safe) and are run with `dotnet test`.
$TestProjects = @(
    'Server.WebAdmin.Tests\Server.WebAdmin.Tests.csproj',
    'Shared.Tests\Shared.Tests.csproj',
    'Server.Persistence.Tests\Server.Persistence.Tests.csproj',
    'tools\MirDbExport.Tests\MirDbExport.Tests.csproj'
)

# `powershell -File verify.ps1 -Only A,B` passes one string "A,B": split it, and refuse unknown keys.
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknown = @($Only | Where-Object { -not $Projects.Contains($_) })
if ($unknown.Count -gt 0) {
    Write-Error "Unknown -Only key(s): $($unknown -join ', '). Valid: $($Projects.Keys -join ', ')"
    exit 2
}

$failed = @()
foreach ($key in $Projects.Keys) {
    if ($Only.Count -gt 0 -and $Only -notcontains $key) { continue }
    $proj = Join-Path $repo $Projects[$key]
    $out = Join-Path $scratchFull $key
    Write-Host "=== build $key -> $out" -ForegroundColor Cyan
    & dotnet build $proj -o $out -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { $failed += "build:$key" }
    # Server.exe is AnyCPU: the SQLite store (roadmap P7) needs the native e_sqlite3.dll beside it, under runtimes\win-x64.
    if ($key -eq 'Server' -and $LASTEXITCODE -eq 0 -and -not (Test-Path (Join-Path $out 'runtimes\win-x64\native\e_sqlite3.dll'))) {
        Write-Host "Server build has no runtimes\win-x64\native\e_sqlite3.dll" -ForegroundColor Red
        $failed += "native:e_sqlite3"
    }
}

if (-not $NoTests) {
    foreach ($t in $TestProjects) {
        $tp = Join-Path $repo $t
        if (-not (Test-Path $tp)) { continue }
        Write-Host "=== test $t" -ForegroundColor Cyan
        & dotnet test $tp -nologo -v:minimal
        if ($LASTEXITCODE -ne 0) { $failed += "test:$t" }
    }
}

if ($failed.Count -gt 0) {
    Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host 'verify: all green' -ForegroundColor Green
exit 0
