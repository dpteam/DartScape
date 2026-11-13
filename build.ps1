<#
    DartScape development build.

    Builds the solution and stages a ready-to-run server into the top-level
    bin\ folder, so `run.cmd` (or double-clicking the exe) just works.

    What this does that `dotnet build` alone does not:

      * collects every assembly into one folder instead of one per project
      * copies the web assets and the 317 cache next to the binaries
      * writes config.ini on first run, and never overwrites it afterwards
      * creates data\ and logs\ so the server has somewhere to write

    The whole bin\ folder is gitignored. It is the live world: config, keys,
    accounts and characters all live there and none of it belongs in the
    repository.

    Usage:
        build.cmd                 Release build
        build.cmd -DebugBuild     Debug build
        build.cmd -SkipTests      do not run the test suite
        build.cmd -Clean          wipe bin\ first (destroys the local world)
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$DebugBuild,
    [switch]$SkipTests,
    [switch]$Clean,
    [switch]$StopRunning
)

$ErrorActionPreference = 'Stop'

if ($DebugBuild) { $Configuration = 'Debug' }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = $scriptDir
$binDir = Join-Path $repoRoot 'bin'

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host ("=== {0}" -f $text) -ForegroundColor Cyan
}

function Write-Ok([string]$text) {
    Write-Host ("    {0}" -f $text) -ForegroundColor DarkGray
}

Write-Host 'DartScape build'
Write-Host ("  configuration : {0}" -f $Configuration)
Write-Host ("  output        : {0}" -f $binDir)

# --- guard against a running instance --------------------------------------

# Staging copies over the assemblies in bin\, and a running server holds them open.
# Failing with "file in use" halfway through leaves a half-updated folder, which is
# far worse than refusing to start, so check first and say what to do.
$running = @()

foreach ($name in @('DartScape.Server', 'DartScape.Client', 'DartScape.Launcher')) {
    $process = Get-Process -Name $name -ErrorAction SilentlyContinue
    if ($process) { $running += $process }
}

if ($running.Count -gt 0) {
    Write-Host ''
    Write-Host 'DartScape is running, so bin\ cannot be updated:' -ForegroundColor Yellow

    foreach ($process in $running) {
        Write-Host ("    {0} (pid {1})" -f $process.ProcessName, $process.Id) -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host '  Close it and run build again, or use -StopRunning to let the build do it.'
    Write-Host '  Characters are saved on a clean shutdown; -StopRunning kills the process.'
    Write-Host ''

    if (-not $StopRunning) {
        exit 1
    }

    foreach ($process in $running) {
        Write-Host ("    stopping {0} (pid {1})" -f $process.ProcessName, $process.Id) -ForegroundColor Yellow
        try { $process.CloseMainWindow() | Out-Null } catch { }
    }

    # Give a graceful close a moment, then insist.
    Start-Sleep -Seconds 3

    foreach ($process in $running) {
        if (-not $process.HasExited) {
            try { $process.Kill(); $process.WaitForExit(5000) } catch { }
        }
    }

    Start-Sleep -Seconds 1
}

# --- clean -----------------------------------------------------------------

if ($Clean -and (Test-Path $binDir)) {
    Write-Step 'Cleaning bin'

    # Keep the world: wiping a live server's characters because someone asked for
    # a clean build would be a genuinely awful surprise.
    $preserve = @('data', 'logs', 'config.ini', 'keys')

    foreach ($item in Get-ChildItem -Path $binDir -Force) {
        if ($preserve -contains $item.Name) { continue }
        Remove-Item -Path $item.FullName -Recurse -Force
    }

    Write-Ok 'removed build output, kept data\, logs\, keys\ and config.ini'
}

# --- build -----------------------------------------------------------------

Write-Step 'Building'

& dotnet build (Join-Path $repoRoot 'DartScape.slnx') -c $Configuration --nologo -v quiet

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'Build failed.' -ForegroundColor Red
    exit 1
}

Write-Ok 'solution built'

# --- stage -----------------------------------------------------------------

Write-Step 'Staging the runnable instance'

if (-not (Test-Path $binDir)) { New-Item -ItemType Directory -Path $binDir -Force | Out-Null }

# Every project's output lands in one folder, so the server can find its own
# assemblies without a probing path.
$projects = @(
    'DartScape.Server',
    'DartScape.Client',
    'DartScape.Launcher'
)

foreach ($project in $projects) {
    $source = Join-Path $repoRoot 'src'
    $source = Join-Path $source $project
    $source = Join-Path $source 'bin'
    $source = Join-Path $source $Configuration
    $source = Join-Path $source 'net40'

    if (-not (Test-Path $source)) {
        Write-Host ("    WARNING: no output for {0}" -f $project) -ForegroundColor Yellow
        continue
    }

    Copy-Item -Path (Join-Path $source '*') -Destination $binDir -Recurse -Force
    Write-Ok ("staged {0}" -f $project)
}

# Web assets are content files, not assemblies, so they need an explicit copy.
foreach ($assets in @('public', 'admin')) {
    $source = Join-Path $repoRoot 'src'
    $source = Join-Path $source 'DartScape.Web'
    $source = Join-Path $source $assets

    if (-not (Test-Path $source)) { continue }

    $target = Join-Path $binDir $assets
    if (Test-Path $target) { Remove-Item -Path $target -Recurse -Force }
    Copy-Item -Path $source -Destination $target -Recurse -Force
    Write-Ok ("staged web {0}" -f $assets)
}

# --- cache -----------------------------------------------------------------

Write-Step 'Cache'

$cacheSource = Join-Path $repoRoot 'assets\cache317'
$cacheTarget = Join-Path $binDir 'cache'

if (-not (Test-Path $cacheSource)) {
    Write-Host '    WARNING: assets\cache317 is missing; the server will start without item or NPC data.' -ForegroundColor Yellow
}
elseif (Test-Path (Join-Path $cacheTarget 'main_file_cache.dat')) {
    # 11 MB: copy once, not on every build.
    Write-Ok 'cache already present, left alone'
}
else {
    New-Item -ItemType Directory -Path $cacheTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $cacheSource '*') -Destination $cacheTarget -Force

    $size = (Get-ChildItem -Path $cacheTarget -File | Measure-Object -Property Length -Sum).Sum
    Write-Ok ("copied cache ({0:N1} MB)" -f ($size / 1MB))
}

# --- config ----------------------------------------------------------------

Write-Step 'Configuration'

$configPath = Join-Path $binDir 'config.ini'
$examplePath = Join-Path $repoRoot 'config.example.ini'

if (Test-Path $configPath) {
    # Never overwrite: this file holds the admin token and the operator's ports.
    Write-Ok 'config.ini already exists, left alone'
}
elseif (Test-Path $examplePath) {
    Copy-Item -Path $examplePath -Destination $configPath -Force
    Write-Ok 'wrote config.ini from config.example.ini'
}
else {
    Write-Host '    WARNING: no config.example.ini; the server will write its own defaults.' -ForegroundColor Yellow
}

# --- runtime folders -------------------------------------------------------

foreach ($folder in @('data\accounts', 'data\characters', 'logs', 'keys')) {
    $path = Join-Path $binDir $folder
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

Write-Ok 'data\, logs\ and keys\ ready'

# --- tests -----------------------------------------------------------------

if (-not $SkipTests) {
    Write-Step 'Tests'

    $tests = Join-Path $repoRoot 'tests'
    $tests = Join-Path $tests 'DartScape.Tests'
    $tests = Join-Path $tests 'bin'
    $tests = Join-Path $tests $Configuration
    $tests = Join-Path $tests 'net40'
    $tests = Join-Path $tests 'DartScape.Tests.exe'

    if (-not (Test-Path $tests)) {
        Write-Host '    WARNING: test runner not built, skipping' -ForegroundColor Yellow
    }
    else {
        & $tests --cache (Join-Path $repoRoot 'assets\cache317') | Out-Null
        $exitCode = $LASTEXITCODE

        if ($exitCode -ne 0) {
            Write-Host ''
            Write-Host 'Tests failed. Run them directly to see why:' -ForegroundColor Red
            Write-Host ("  {0} --cache assets\cache317" -f $tests)
            exit 1
        }

        Write-Ok 'all tests passed'
    }
}

# --- summary ---------------------------------------------------------------

Write-Host ''
Write-Host 'Ready.' -ForegroundColor Green
Write-Host ''
Write-Host ("  server : {0}" -f (Join-Path $binDir 'DartScape.Server.exe'))
Write-Host ("  client : {0}" -f (Join-Path $binDir 'DartScape.Client.exe'))
Write-Host ("  launcher: {0}" -f (Join-Path $binDir 'DartScape.Launcher.exe'))
Write-Host ''
Write-Host '  run.cmd          start the server'
Write-Host '  run.cmd client   start the client directly'
Write-Host '  run.cmd launcher start the launcher'
Write-Host ''
Write-Host '  Ports: 43594 game, 43595 status site, 43596 admin (LAN only), 43597 relay'
Write-Host ''
