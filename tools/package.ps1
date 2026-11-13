<#
    DartScape packaging.

    Produces three things under artifacts/dist:

      server/   the game server with its web assets and the 317 cache
      client/   the launcher and the client
      dartscape-<version>.zip   everything, ready to hand to a player

    The split matters: a player needs the client package, an operator needs the
    server package, and nobody should have to guess which file is which.

    Debug symbols, logs and any local runtime state are stripped. The cache is
    included in the server package because the server cannot start a useful world
    without it, and in the client package because the client reads it too.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))

if ([string]::IsNullOrEmpty($Version)) {
    # Prefer the git tag, then the commit, then a fixed fallback so CI never breaks
    # because a tag is missing.
    $Version = '0.1.0'

    try {
        $tag = (git -C $repoRoot describe --tags --exact-match 2>$null)
        if ($LASTEXITCODE -eq 0 -and $tag) {
            $Version = $tag.TrimStart('v')
        }
        else {
            $sha = (git -C $repoRoot rev-parse --short HEAD 2>$null)
            if ($LASTEXITCODE -eq 0 -and $sha) { $Version = "$Version-$($sha.Trim())" }
        }
    }
    catch {
        # No git available; the fallback stands.
    }
}

$dist = Join-Path $repoRoot 'artifacts\dist'
$serverDir = Join-Path $dist 'server'
$clientDir = Join-Path $dist 'client'

Write-Host 'DartScape packaging'
Write-Host ("  configuration : {0}" -f $Configuration)
Write-Host ("  version       : {0}" -f $Version)
Write-Host ("  output        : {0}" -f $dist)
Write-Host ''

# --- clean -----------------------------------------------------------------

if (Test-Path $dist) { Remove-Item -Path $dist -Recurse -Force }
New-Item -ItemType Directory -Path $serverDir -Force | Out-Null
New-Item -ItemType Directory -Path $clientDir -Force | Out-Null

# --- helpers ---------------------------------------------------------------

function Copy-ProjectOutput([string]$project, [string]$destination) {
    # Built with Join-Path rather than a format string: a literal "\n" in a
    # format string is easy to misread and has bitten this script before.
    $source = Join-Path $repoRoot 'src'
    $source = Join-Path $source $project
    $source = Join-Path $source 'bin'
    $source = Join-Path $source $Configuration
    $source = Join-Path $source 'net40'

    if (-not (Test-Path $source)) {
        throw ("Build output not found for {0}: {1}" -f $project, $source)
    }

    Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force

    # Strip development artifacts.
    Get-ChildItem -Path $destination -Recurse -Include *.pdb, *.xml, *.log -File |
        Remove-Item -Force -ErrorAction SilentlyContinue

    # Strip any local runtime state that a test run may have created.
    foreach ($state in @('logs', 'data', 'launcher-data', 'config.ini', 'keys')) {
        $path = Join-Path $destination $state
        if (Test-Path $path) { Remove-Item -Path $path -Recurse -Force }
    }

    Write-Host ("  staged {0}" -f $project)
}

function Copy-Cache([string]$destination) {
    $cacheSource = Join-Path $repoRoot 'assets\cache317'

    if (-not (Test-Path $cacheSource)) {
        Write-Host '  WARNING: no cache in assets/cache317; the package will not run out of the box' -ForegroundColor Yellow
        return
    }

    $cacheTarget = Join-Path $destination 'cache'
    New-Item -ItemType Directory -Path $cacheTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $cacheSource '*') -Destination $cacheTarget -Force

    $size = (Get-ChildItem -Path $cacheTarget -File | Measure-Object -Property Length -Sum).Sum
    Write-Host ("  staged cache ({0:N1} MB)" -f ($size / 1MB))
}

# --- server package --------------------------------------------------------

Write-Host ''
Write-Host 'Server package'

Copy-ProjectOutput 'DartScape.Server' $serverDir
Copy-Cache $serverDir

# A first-run configuration the operator can edit rather than invent.
$serverConfig = @'
# DartScape server configuration
#
# Every value here has a built-in default; deleting a line restores that default.
# Ports are the ones the firewall script in tools\firewall.ps1 opens.

[server]
port = 43594
bind = 0.0.0.0
name = DartScape
region = EU
members = false
maxPlayers = 2000
tickMs = 600
publicHost = rune.dartpower.xyz
cacheDir = cache
dataDir = data
keyFile = keys/server.key
revision = 317
allowRegistration = true
startX = 3222
startY = 3218
startPlane = 0
experienceRate = 1.0

[web]
port = 43595
trustProxyHeaders = true
trustedProxies = 127.0.0.1, ::1, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16

[admin]
# The admin port is bound to all interfaces but must never be forwarded by your
# router. privateOnly additionally refuses anything outside the local network.
port = 43596
privateOnly = true
# token is generated on first start and printed to the console

[relay]
port = 43597
enabled = true
maxTunnels = 64

[logging]
level = Information
dir = logs
console = true
'@

Set-Content -Path (Join-Path $serverDir 'config.ini') -Value $serverConfig -Encoding UTF8
Write-Host '  staged config.ini'

# --- client package --------------------------------------------------------

Write-Host ''
Write-Host 'Client package'

Copy-ProjectOutput 'DartScape.Client' $clientDir
Copy-ProjectOutput 'DartScape.Launcher' $clientDir
Copy-Cache $clientDir

$clientConfig = @'
# DartScape client configuration
#
# The launcher passes these on the command line, so this file only matters when
# the client is started directly.

[client]
host = rune.dartpower.xyz
port = 43594
cacheDir = cache
width = 765
height = 503
music = false
sound = true
theme = light
revision = 317
'@

Set-Content -Path (Join-Path $clientDir 'config.ini') -Value $clientConfig -Encoding UTF8
Write-Host '  staged config.ini'

# --- read-me ---------------------------------------------------------------

$readme = @"
DartScape $Version
====================

Two things are in this archive.

  server\   The game server. Run DartScape.Server.exe.
  client\   The launcher and the game client. Run DartScape.Launcher.exe.

PLAYING
-------
1. Open client\DartScape.Launcher.exe
2. Enter a username and a password. The account is created on first login.
3. Leave the server address as it is, or point it at your own server.
4. Press Play.

HOSTING
-------
1. Open server\DartScape.Server.exe and let it start once. It writes
   server\config.ini and prints an admin token to the console.
2. Forward these TCP ports on your router:

     43594   game
     43595   status site  (put Nginx Proxy Manager in front of it)
     43597   relay        (only if players behind the same router need it)

   Do NOT forward 43596. That is the admin panel, and it is meant to be reachable
   only from your own network.
3. tools\firewall.ps1 opens exactly those ports on Windows Firewall.

REQUIREMENTS
------------
Windows XP SP3 or newer, .NET Framework 4.0.
No installer, no runtime downloads, no third-party libraries.

NOT AFFILIATED WITH JAGEX
-------------------------
RuneScape is a trademark of Jagex Ltd. This is an independent hobby project.
"@

Set-Content -Path (Join-Path $dist 'READ-ME-FIRST.txt') -Value $readme -Encoding UTF8

# --- zip -------------------------------------------------------------------

Write-Host ''
Write-Host 'Archiving'

$zipName = "dartscape-$Version.zip"
$zipPath = Join-Path $dist $zipName

# Compress-Archive is PowerShell 5.0+. Fall back to the .NET API on older hosts.
$useCmdlet = $null -ne (Get-Command Compress-Archive -ErrorAction SilentlyContinue)

if ($useCmdlet) {
    Compress-Archive -Path (Join-Path $dist 'server'), (Join-Path $dist 'client'),
        (Join-Path $dist 'READ-ME-FIRST.txt') -DestinationPath $zipPath -CompressionLevel Optimal
}
else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($dist, $zipPath)
}

$zipSize = (Get-Item $zipPath).Length
Write-Host ("  {0} ({1:N1} MB)" -f $zipName, ($zipSize / 1MB))

# --- summary ---------------------------------------------------------------

Write-Host ''
Write-Host 'Summary'

foreach ($directory in @($serverDir, $clientDir)) {
    $files = Get-ChildItem -Path $directory -Recurse -File
    $size = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Host ("  {0,-8} {1,4} files  {2,8:N1} MB" -f
        (Split-Path $directory -Leaf), $files.Count, ($size / 1MB))
}

Write-Host ''
Write-Host 'Packaging complete.' -ForegroundColor Green
