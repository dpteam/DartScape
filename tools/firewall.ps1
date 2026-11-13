<#
    DartScape firewall configuration.

    Opens exactly the ports the server needs and nothing else, scoped as tightly as
    the deployment allows.

    Usage (from an elevated prompt, or it will prompt for elevation):

        tools\firewall.cmd                     standard: game + web + relay, admin LAN-only
        tools\firewall.cmd -WebAny             allow the status site from anywhere
                                               (needed when Nginx Proxy Manager is on
                                                another machine)
        tools\firewall.cmd -Remove             delete the DartScape rules

    Ports are read from config.ini when it exists, so the rules always match the
    server's actual configuration rather than a copy of it that drifts.
#>
[CmdletBinding()]
param(
    [switch]$WebAny,
    [switch]$Remove,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'

# --- self-elevation --------------------------------------------------------

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host 'Requesting administrator rights...' -ForegroundColor Yellow

    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
    if ($WebAny) { $arguments += '-WebAny' }
    if ($Remove) { $arguments += '-Remove' }
    if ($ConfigPath) { $arguments += @('-ConfigPath', ('"{0}"' -f $ConfigPath)) }

    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments | Out-Null
    }
    catch {
        Write-Host 'Elevation was refused. Re-run this script from an administrator prompt.' -ForegroundColor Red
    }
    exit
}

# From here on we are elevated. Mirror everything to a report file, because the
# elevated window closes when the script finishes and its output is otherwise lost.
$reportPath = Join-Path ([System.IO.Path]::GetTempPath()) 'dartscape-firewall.log'

try {
    Start-Transcript -Path $reportPath -Force | Out-Null
}
catch {
    # Transcripts can be blocked by policy; the console output still works.
}

# --- read the server configuration -----------------------------------------

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))

if ([string]::IsNullOrEmpty($ConfigPath)) {
    $candidates = @(
        (Join-Path $repoRoot 'config.ini'),
        (Join-Path $repoRoot 'bin\config.ini'),
        (Join-Path $repoRoot 'artifacts\dist\server\config.ini'),
        (Join-Path $repoRoot 'src\DartScape.Server\bin\Release\net40\config.ini')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { $ConfigPath = $candidate; break }
    }
}

$gamePort = 43594
$webPort = 43595
$adminPort = 43596
$relayPort = 43597

if ($ConfigPath -and (Test-Path $ConfigPath)) {
    Write-Host ("Reading ports from {0}" -f $ConfigPath)

    $section = ''

    foreach ($raw in Get-Content -Path $ConfigPath) {
        $line = $raw.Trim()
        if ($line.Length -eq 0 -or $line[0] -eq '#' -or $line[0] -eq ';') { continue }

        if ($line -match '^\[(.+)\]$') { $section = $Matches[1].ToLowerInvariant(); continue }
        if ($line -notmatch '^([A-Za-z0-9_.]+)\s*=\s*(.*)$') { continue }

        $key = $Matches[1].ToLowerInvariant()
        $value = $Matches[2].Trim()

        # Keys are matched bare, because the file uses [section] headers.
        switch ($key) {
            'port' {
                switch ($section) {
                    'server' { $gamePort = [int]$value }
                    'web' { $webPort = [int]$value }
                    'admin' { $adminPort = [int]$value }
                    'relay' { $relayPort = [int]$value }
                }
            }
        }
    }
}
else {
    Write-Host 'No config.ini found; using the default ports.' -ForegroundColor Yellow
}

# --- rule definitions ------------------------------------------------------

# The admin port is never exposed beyond the local network, whatever else happens.
$lanRemote = @(
    'LocalSubnet',
    '127.0.0.1/32',
    '10.0.0.0/8',
    '172.16.0.0/12',
    '192.168.0.0/16',
    '169.254.0.0/16',
    '100.64.0.0/10'
)

$anyRemote = @('Any')

$rules = @(
    @{ Name = 'DartScape Game'; Port = $gamePort; Remote = $anyRemote;
       Note = 'players connect here' },
    @{ Name = 'DartScape Status Site'; Port = $webPort;
       Remote = $(if ($WebAny) { $anyRemote } else { $lanRemote });
       Note = $(if ($WebAny) { 'public, via the reverse proxy' } else { 'local network only (proxy is on this machine)' }) },
    @{ Name = 'DartScape Admin Panel'; Port = $adminPort; Remote = $lanRemote;
       Note = 'never forwarded by the router' },
    @{ Name = 'DartScape Relay'; Port = $relayPort; Remote = $anyRemote;
       Note = 'NAT hairpin workaround' }
)

# --- helpers ---------------------------------------------------------------

$haveCmdlets = $null -ne (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue)

function Remove-DartScapeRule([string]$name) {
    if ($haveCmdlets) {
        Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue
    }
    else {
        $command = 'netsh advfirewall firewall delete rule name="{0}"' -f $name
        Invoke-Expression $command 2>$null | Out-Null
    }
}

function Add-DartScapeRule([string]$name, [int]$port, [string[]]$remote) {
    if ($haveCmdlets) {
        $parameters = @{
            DisplayName = $name
            Direction   = 'Inbound'
            Action      = 'Allow'
            Protocol    = 'TCP'
            LocalPort   = $port
            RemoteAddress = $remote
            Profile     = 'Any'
            Enabled     = 'True'
        }

        New-NetFirewallRule @parameters | Out-Null
    }
    else {
        # netsh is the only option on XP and on machines without the NetSecurity module.
        $command = 'netsh advfirewall firewall add rule name="{0}" dir=in action=allow protocol=TCP localport={1} profile=any enable=yes' -f $name, $port

        if ($remote -notcontains 'Any') {
            $command += ' remoteip=' + ($remote -join ',')
        }

        Invoke-Expression $command | Out-Null
    }
}

# --- apply -----------------------------------------------------------------

Write-Host ''
Write-Host 'DartScape firewall rules'
Write-Host ''

if ($Remove) {
    foreach ($rule in $rules) {
        Remove-DartScapeRule $rule.Name
        Write-Host ("  removed  {0}" -f $rule.Name)
    }

    Write-Host ''
    Write-Host 'All DartScape rules removed.' -ForegroundColor Green
    exit 0
}

foreach ($rule in $rules) {
    # Delete then recreate, so running the script twice is idempotent.
    Remove-DartScapeRule $rule.Name
    Add-DartScapeRule $rule.Name $rule.Port $rule.Remote

    $scope = if ($rule.Remote -contains 'Any') { 'anywhere' } else { 'local network' }
    Write-Host ("  allowed  {0,-24} port {1,-6} {2}" -f $rule.Name, $rule.Port, $scope)
}

# --- diagnostics -----------------------------------------------------------

Write-Host ''
Write-Host 'Diagnostics'
Write-Host ''

foreach ($rule in $rules) {
    $listening = $false

    try {
        $listening = $null -ne (Get-NetTCPConnection -LocalPort $rule.Port -State Listen -ErrorAction SilentlyContinue)
    }
    catch {
        # Cmdlet unavailable; fall back to netstat.
        $netstat = netstat -an | Select-String (":{0}\s" -f $rule.Port)
        $listening = $null -ne $netstat
    }

    $state = if ($listening) { 'LISTENING' } else { 'not running' }
    Write-Host ("  port {0,-6} {1,-12} {2}" -f $rule.Port, $state, $rule.Note)
}

Write-Host ''
Write-Host 'Local addresses (use these in Nginx Proxy Manager):'

try {
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object { $_.IPAddress -ne '127.0.0.1' } |
        ForEach-Object { Write-Host ("  {0}  ({1})" -f $_.IPAddress, $_.InterfaceAlias) }
}
catch {
    # XP and older PowerShell: ask the stack directly.
    [System.Net.Dns]::GetHostAddresses([System.Net.Dns]::GetHostName()) |
        Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
        ForEach-Object { Write-Host ("  {0}" -f $_.IPAddressToString) }
}

Write-Host ''
Write-Host 'Reverse proxy checklist for rune.dartpower.xyz:'
Write-Host ("  * forward https://rune.dartpower.xyz  ->  http://<this machine>:{0}" -f $webPort)
Write-Host '  * enable "Block common exploits" and a websocket upgrade is NOT needed'
Write-Host '  * leave the admin port alone; reach it directly on the LAN'
Write-Host ''
Write-Host 'Firewall configuration complete.' -ForegroundColor Green

try {
    Stop-Transcript | Out-Null
    Write-Host ''
    Write-Host ("Report written to {0}" -f $reportPath) -ForegroundColor DarkGray
}
catch {
}

# Keep the elevated window open long enough to read, unless this was a quiet run.
if ($Host.Name -eq 'ConsoleHost' -and -not $env:DARTSCAPE_NO_PAUSE) {
    Write-Host ''
    Write-Host 'Press Enter to close.' -ForegroundColor DarkGray
    Read-Host | Out-Null
}
