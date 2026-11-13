<#
    DartScape Windows XP compatibility check.

    Verifies the constraints that .NET Framework 4.0 does not enforce for us:

      1. Every shipped executable declares a PE subsystem version of at most 5.01
         (Windows XP). A higher value makes XP refuse to load the image with a
         generic "not a valid Win32 application" dialog and no further detail.
      2. Every executable's .config declares the .NET Framework v4.0 runtime.
      3. Every DllImport target is on the allowlist of APIs that exist on XP.
      4. Any call to a post-XP API is inside a method that catches
         EntryPointNotFoundException or DllNotFoundException.

    Exits non-zero when a violation is found, so CI fails loudly.
#>
[CmdletBinding()]
param(
    [string]$ArtifactsRoot
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))

if ([string]::IsNullOrEmpty($ArtifactsRoot)) {
    $ArtifactsRoot = Join-Path $repoRoot 'artifacts'
}

# --- configuration ---------------------------------------------------------

# DLL imports that exist on Windows XP SP3. Anything else must be guarded.
$allowedImports = @(
    'kernel32.dll', 'user32.dll', 'gdi32.dll', 'advapi32.dll', 'shell32.dll',
    'ole32.dll', 'oleaut32.dll', 'comdlg32.dll', 'winmm.dll', 'ws2_32.dll',
    'd3d9.dll', 'dsound.dll', 'version.dll', 'shlwapi.dll', 'imm32.dll',
    'msimg32.dll', 'mscoree.dll'
)

# APIs that do not exist on XP and must be wrapped in a missing-entry-point catch.
$postXpApis = @(
    'SetProcessDPIAware', 'GetDpiForWindow', 'GetDpiForMonitor',
    'DwmGetWindowAttribute', 'DwmSetWindowAttribute', 'GetTickCount64',
    'SetThreadDescription', 'InitializeCriticalSectionEx',
    'GetSystemMetricsForDpi', 'AdjustWindowRectExForDpi'
)

$violations = New-Object System.Collections.Generic.List[string]
$checked = 0

# --- helpers ---------------------------------------------------------------

function Read-PeSubsystem([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)

    if ($bytes.Length -lt 0x40) { return $null }
    if ($bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) { return $null }   # "MZ"

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($peOffset -le 0 -or $peOffset + 24 -ge $bytes.Length) { return $null }

    if ($bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45) { return $null }   # "PE"

    # Optional header starts 24 bytes after the PE signature.
    $opt = $peOffset + 24

    # Subsystem major/minor live at optional-header offset 48/50.
    $major = [BitConverter]::ToUInt16($bytes, $opt + 48)
    $minor = [BitConverter]::ToUInt16($bytes, $opt + 50)

    return [pscustomobject]@{ Major = $major; Minor = $minor }
}

function Test-PostXpGuard([string]$source) {
    # A guarded file mentions the API and also catches a missing entry point.
    return ($source -match 'EntryPointNotFoundException' -or $source -match 'DllNotFoundException')
}

<#
    Removes comments and string literals so that prose about an API is not
    mistaken for a call to it. This project documents its constraints in
    comments, so a naive text search flags every explanation.
#>
function Get-CodeOnly([string]$text) {
    $result = New-Object System.Text.StringBuilder $text.Length
    $i = 0
    $n = $text.Length

    while ($i -lt $n) {
        $c = $text[$i]
        $next = if ($i + 1 -lt $n) { $text[$i + 1] } else { [char]0 }

        # Line comment.
        if ($c -eq '/' -and $next -eq '/') {
            while ($i -lt $n -and $text[$i] -ne "`n") { $i++ }
            continue
        }

        # Block comment.
        if ($c -eq '/' -and $next -eq '*') {
            $i += 2
            while ($i + 1 -lt $n -and -not ($text[$i] -eq '*' -and $text[$i + 1] -eq '/')) { $i++ }
            $i += 2
            continue
        }

        # Regular string literal (with escapes).
        if ($c -eq '"') {
            $i++
            while ($i -lt $n) {
                if ($text[$i] -eq '\') { $i += 2; continue }
                if ($text[$i] -eq '"') { $i++; break }
                $i++
            }
            continue
        }

        # Character literal.
        if ($c -eq "'") {
            $i++
            while ($i -lt $n) {
                if ($text[$i] -eq '\') { $i += 2; continue }
                if ($text[$i] -eq "'") { $i++; break }
                $i++
            }
            continue
        }

        [void]$result.Append($c)
        $i++
    }

    return $result.ToString()
}

# --- executables -----------------------------------------------------------

Write-Host 'DartScape XP compatibility check'
Write-Host ("  artifacts root: {0}" -f $ArtifactsRoot)
Write-Host ''

$exeFiles = @()

foreach ($config in @('Debug', 'Release')) {
    foreach ($project in @('DartScape.Server', 'DartScape.Client', 'DartScape.Launcher')) {
        $dir = Join-Path $repoRoot ("src\{0}\bin\{1}\net40" -f $project, $config)
        if (-not (Test-Path $dir)) { continue }

        $exeFiles += Get-ChildItem -Path $dir -Filter *.exe -File -ErrorAction SilentlyContinue
    }
}

if ($exeFiles.Count -eq 0) {
    Write-Host '  No built executables found. Build the solution first.' -ForegroundColor Yellow
    exit 1
}

foreach ($exe in $exeFiles) {
    $checked++

    $subsystem = Read-PeSubsystem $exe.FullName

    if ($null -eq $subsystem) {
        $violations.Add(("{0}: could not read the PE header" -f $exe.Name))
        continue
    }

    # 5.01 = Windows XP. Anything above it is refused by the XP loader.
    if ($subsystem.Major -gt 5 -or ($subsystem.Major -eq 5 -and $subsystem.Minor -gt 1)) {
        $violations.Add(("{0}: PE subsystem {1}.{2} is above 5.01 (Windows XP)" -f
            $exe.Name, $subsystem.Major, $subsystem.Minor))
    }
    else {
        Write-Host ("  OK    {0} subsystem {1}.{2}" -f $exe.Name, $subsystem.Major, $subsystem.Minor)
    }

    # The runtime declaration has to name .NET 4.0.
    $configPath = $exe.FullName + '.config'
    if (Test-Path $configPath) {
        $content = Get-Content -Path $configPath -Raw

        if ($content -notmatch '\.NETFramework,Version=v4\.0') {
            $violations.Add(("{0}: .config does not declare .NETFramework,Version=v4.0" -f $exe.Name))
        }
        else {
            Write-Host ("  OK    {0}.config targets v4.0" -f $exe.Name)
        }
    }
}

# --- P/Invoke audit --------------------------------------------------------

Write-Host ''
Write-Host '  Auditing P/Invoke declarations...'

$sourceFiles = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\bin\\' }

$importCount = 0

foreach ($file in $sourceFiles) {
    $text = Get-Content -Path $file.FullName -Raw
    if ($text -notmatch 'DllImport') { continue }

    # Every DllImport mentions a library name in its attribute.
    foreach ($match in [regex]::Matches($text, 'DllImport\s*\(\s*"([^"]+)"')) {
        $importCount++
        $library = $match.Groups[1].Value.ToLowerInvariant()

        if ($allowedImports -notcontains $library) {
            $violations.Add(("{0}: DllImport of '{1}', which is not on the XP allowlist" -f
                $file.Name, $library))
        }
    }

    # Any post-XP API needs a missing-entry-point guard somewhere in the file.
    foreach ($api in $postXpApis) {
        if ($text -notmatch [regex]::Escape($api)) { continue }

        if (-not (Test-PostXpGuard $text)) {
            $violations.Add(("{0}: calls '{1}', which does not exist on XP, without a missing-entry-point guard" -f
                $file.Name, $api))
        }
    }
}

Write-Host ("  Checked {0} DllImport declarations across {1} source files." -f $importCount, $sourceFiles.Count)

# --- forbidden APIs --------------------------------------------------------

Write-Host ''
Write-Host '  Auditing for APIs missing from .NET Framework 4.0...'

$forbidden = @(
    @{ Pattern = 'System\.Text\.Json';        Name = 'System.Text.Json (4.6+)' },
    @{ Pattern = 'System\.Net\.Http';         Name = 'HttpClient (4.5+)' },
    @{ Pattern = 'System\.IO\.Compression\.ZipFile'; Name = 'ZipFile (4.5+)' },
    @{ Pattern = 'IReadOnlyList<';            Name = 'IReadOnlyList<T> (4.5+)' },
    @{ Pattern = 'Task\.Run\s*\(';            Name = 'Task.Run (4.5+)' },
    @{ Pattern = 'Array\.Empty<';             Name = 'Array.Empty<T> (4.6+)' },
    @{ Pattern = '\bnameof\s*\(';             Name = 'nameof (C# 6)' },
    @{ Pattern = '\$"';                       Name = 'string interpolation (C# 6)' },
    @{ Pattern = 'Buffer\.MemoryCopy';        Name = 'Buffer.MemoryCopy (4.6+)' },
    @{ Pattern = 'Math\.Clamp';               Name = 'Math.Clamp (core only)' },
    @{ Pattern = '\bSpan<';                   Name = 'Span<T> (core only)' }
)

foreach ($file in $sourceFiles) {
    $text = Get-CodeOnly (Get-Content -Path $file.FullName -Raw)

    foreach ($rule in $forbidden) {
        # -cmatch: C# keywords and type names are case sensitive, and PowerShell's
        # default comparison would flag our own NameOf method as "nameof".
        if ($text -cmatch $rule.Pattern) {
            $violations.Add(("{0}: uses {1}, which is unavailable on .NET Framework 4.0" -f
                $file.Name, $rule.Name))
        }
    }
}

# --- report ----------------------------------------------------------------

Write-Host ''

if ($violations.Count -eq 0) {
    Write-Host ("XP compatibility: PASS ({0} executables checked)" -f $checked) -ForegroundColor Green
    exit 0
}

Write-Host ("XP compatibility: FAIL ({0} violation(s))" -f $violations.Count) -ForegroundColor Red
Write-Host ''

foreach ($violation in $violations) {
    Write-Host ("  * {0}" -f $violation) -ForegroundColor Red
}

exit 1
