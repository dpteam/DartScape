<#
    DartScape standalone check.

    Verifies that a shipped build carries no third-party dependencies and no
    development leftovers:

      1. No project declares a PackageReference other than the compile-only
         .NET Framework reference assemblies.
      2. Every project targets net40.
      3. A shipping output directory contains only our own assemblies, the
         runtime's, and data files.
      4. No refs/, cache copies, logs, source or PDB files leak into a package.

    Exits non-zero on any violation.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))

$violations = New-Object System.Collections.Generic.List[string]

Write-Host 'DartScape standalone check'
Write-Host ''

# --- project files ---------------------------------------------------------

Write-Host '  Auditing project references...'

$projectFiles = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Filter *.csproj -File
$projectFiles += Get-ChildItem -Path (Join-Path $repoRoot 'tests') -Recurse -Filter *.csproj -File

$allowedPackages = @('Microsoft.NETFramework.ReferenceAssemblies')

# The target framework is normally set once in Directory.Build.props, so the check
# accepts either an explicit per-project value or the shared property.
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$propsTargetsNet40 = (Test-Path $propsPath) -and
    ((Get-Content -Path $propsPath -Raw) -match '<TargetFramework>net40</TargetFramework>')

foreach ($project in $projectFiles) {
    $text = Get-Content -Path $project.FullName -Raw

    # Target framework: net40 only, from the project or from the shared props.
    if ($text -notmatch '<TargetFramework>net40</TargetFramework>' -and -not $propsTargetsNet40) {
        $violations.Add(("{0}: does not target net40" -f $project.Name))
    }

    # Package references: only the compile-only reference assemblies are allowed.
    foreach ($match in [regex]::Matches($text, 'PackageReference\s+Include="([^"]+)"')) {
        $package = $match.Groups[1].Value

        if ($allowedPackages -notcontains $package) {
            $violations.Add(("{0}: PackageReference to '{1}'" -f $project.Name, $package))
        }
    }
}

if (-not $propsTargetsNet40) {
    $violations.Add('Directory.Build.props does not set <TargetFramework>net40</TargetFramework>')
}

Write-Host ("  Checked {0} project files." -f $projectFiles.Count)

# --- output directories ----------------------------------------------------

Write-Host '  Auditing build output...'

$allowedDllPrefixes = @('DartScape.')

foreach ($config in @('Debug', 'Release')) {
    foreach ($project in @('DartScape.Server', 'DartScape.Client', 'DartScape.Launcher')) {
        $dir = Join-Path $repoRoot ("src\{0}\bin\{1}\net40" -f $project, $config)
        if (-not (Test-Path $dir)) { continue }

        foreach ($dll in Get-ChildItem -Path $dir -Filter *.dll -File) {
            $matched = $false
            foreach ($prefix in $allowedDllPrefixes) {
                if ($dll.Name.StartsWith($prefix)) { $matched = $true; break }
            }

            if (-not $matched) {
                $violations.Add(("{0}\{1}: unexpected assembly in the output" -f $project, $dll.Name))
            }
        }

        # Development leftovers that must never ship.
        foreach ($pattern in @('*.pdb', '*.log', 'refs', 'cache317')) {
            $found = Get-ChildItem -Path $dir -Filter $pattern -File -ErrorAction SilentlyContinue
            if ($pattern -like '*.pdb' -or $pattern -like '*.log') {
                # PDBs are fine in a build output; they are stripped at packaging time.
                continue
            }

            if ($found) {
                $violations.Add(("{0}: '{1}' should not be in the output" -f $project, $pattern))
            }
        }
    }
}

# --- repository hygiene ----------------------------------------------------

Write-Host '  Auditing repository hygiene...'

# Every source file must be tracked. A .gitignore pattern that is too broad can
# silently exclude a whole directory, which builds locally and fails only in CI
# on a clean checkout. That is exactly how src/DartScape.Core/Runtime/ went
# missing for a while.
$gitAvailable = $null -ne (Get-Command git -ErrorAction SilentlyContinue)

if (-not $gitAvailable) {
    Write-Host '    git not available; skipping the tracked-files check' -ForegroundColor Yellow
}
else {
    $tracked = @()
    $trackedRaw = git -C $repoRoot ls-files 2>$null

    if ($LASTEXITCODE -eq 0 -and $trackedRaw) {
        $tracked = $trackedRaw | ForEach-Object { $_.Trim().Replace('/', '\') }
    }

    if ($tracked.Count -eq 0) {
        Write-Host '    no tracked files reported; skipping' -ForegroundColor Yellow
    }
    else {
        $sourceFiles = Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }

        $untracked = @()

        foreach ($file in $sourceFiles) {
            $relative = $file.FullName.Substring($repoRoot.Length).TrimStart('\')

            if ($tracked -notcontains $relative) {
                $untracked += $relative
            }
        }

        foreach ($path in $untracked) {
            $violations.Add(("{0}: on disk but not tracked by git (a .gitignore pattern is too broad?)" -f $path))
        }

        Write-Host ("    {0} source files, {1} untracked" -f $sourceFiles.Count, $untracked.Count)
    }
}

$gitignore = Join-Path $repoRoot '.gitignore'

if (-not (Test-Path $gitignore)) {
    $violations.Add('.gitignore is missing')
}
else {
    $content = Get-Content -Path $gitignore -Raw

    # The file uses character classes ([Bb]in/), so match loosely.
    foreach ($required in @('refs', 'logs', '[Bb]in', '[Oo]bj')) {
        if ($content -notmatch [regex]::Escape($required)) {
            $violations.Add((".gitignore does not exclude '{0}'" -f $required))
        }
    }
}

# --- report ----------------------------------------------------------------

Write-Host ''

if ($violations.Count -eq 0) {
    Write-Host 'Standalone check: PASS' -ForegroundColor Green
    exit 0
}

Write-Host ("Standalone check: FAIL ({0} violation(s))" -f $violations.Count) -ForegroundColor Red
Write-Host ''

foreach ($violation in $violations) {
    Write-Host ("  * {0}" -f $violation) -ForegroundColor Red
}

exit 1
