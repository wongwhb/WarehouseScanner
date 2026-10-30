<#
.SYNOPSIS
    Build gate: compile the project and enforce "0 errors, warnings <= baseline".

.DESCRIPTION
    Callable from local shells or any CI platform:

      1) dotnet build -t:Rebuild (full rebuild)
      2) count unique compiler errors/warnings (MSBuild prints each diagnostic
         twice: inline + summary; dedupe before counting)
      3) exit non-zero if errors > 0 or warnings > baseline

    Baseline policy: ratchet only (lower it as warnings are fixed, never raise).

.PARAMETER MaxWarnings
    Max allowed warnings. Defaults to the current baseline (0).

.EXAMPLE
    pwsh -File build/verify-build.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build/verify-build.ps1 -MaxWarnings 10
#>
[CmdletBinding()]
param(
    [int] $MaxWarnings = -1,
    [string] $ProjectPath
)

$ErrorActionPreference = 'Stop'

# All nullable warnings have been fixed; baseline is 0 so any new warning fails the build.
$BaselineWarnings = 0

if ($MaxWarnings -lt 0) { $MaxWarnings = $BaselineWarnings }
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'WarehouseScanner.csproj'
}

Write-Host "Building: $ProjectPath"

$raw = & dotnet build $ProjectPath -t:Rebuild --nologo 2>&1 | Out-String

$diagnostics = ($raw -split "`r?`n") |
    Where-Object { $_ -match ': (warning|error) ' } |
    ForEach-Object { ($_ -replace '\s+', ' ').Trim() } |
    Sort-Object -Unique

$errors = @($diagnostics | Where-Object { $_ -match ': error ' })
$warnings = @($diagnostics | Where-Object { $_ -match ': warning ' })

if ($errors.Count -gt 0) {
    Write-Host "Compile errors ($($errors.Count)):"
    $errors | ForEach-Object { Write-Host "  $_" }
}
Write-Host ("Errors:   {0}" -f $errors.Count)
Write-Host ("Warnings: {0} (baseline {1})" -f $warnings.Count, $MaxWarnings)

$failed = $false

if ($errors.Count -gt 0) {
    Write-Host "FAILED: compile errors present."
    $failed = $true
}

if ($warnings.Count -gt $MaxWarnings) {
    Write-Host "FAILED: warning count $($warnings.Count) exceeds baseline $MaxWarnings. Fix the new warnings, or (after review) lower the baseline."
    $failed = $true
}

if ($failed) { exit 1 }

Write-Host "PASSED: 0 errors, warnings within baseline."
exit 0
