# Minimal test for fcon update (progress bar + text like panel)
# Usage: pwsh -NoProfile -File tests/Update.Tests.ps1  (or powershell)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..\fleet-connect.ps1')).Path

# Find a shell that exists (Windows: powershell.exe, pwsh.exe; Linux: pwsh)
$shells = @()
foreach ($n in @('pwsh','powershell')) {
  $c = Get-Command $n -ErrorAction SilentlyContinue
  if ($c) { $shells += $c.Source }
}
if ($shells.Count -eq 0) { $shells = @('pwsh') }

$failed = 0
function Assert($Cond, $Msg) {
  if (-not $Cond) { Write-Host "FAIL: $Msg" -ForegroundColor Red; $script:failed++ } else { Write-Host "PASS: $Msg" -ForegroundColor Green }
}
function Run($TestArgs) {
  # Use direct invocation like fcon.cmd does: & 'fleet-connect.ps1' update ...
  $argStr = ($TestArgs | ForEach-Object { "'$_'" }) -join ' '
  $cmd = "& '$scriptPath' $argStr"
  $pwsh = $shells[0]
  $env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1"
  $out = & $pwsh -NoProfile -Command $cmd 2>&1 | Out-String
  $code = $LASTEXITCODE
  return @{ ExitCode = $code; Output = $out }
}

# Test 1: update --help
$r = Run @('update','--help')
Assert ($r.ExitCode -eq 0) "update --help exits 0 (got $($r.ExitCode))"
Assert ($r.Output -match 'fcon update') "update --help contains 'fcon update'"
Assert ($r.Output -match 'progress bar') "update --help mentions progress bar"

# Test 2: update with unknown option
$r2 = Run @('update','--bogus')
Assert ($r2.ExitCode -eq 1) "update --bogus exits 1 (got $($r2.ExitCode))"
Assert ($r2.Output -match 'Unknown option') "update --bogus mentions Unknown option"

# Test 3: --check help path
$r3 = Run @('update','help')
Assert ($r3.ExitCode -eq 0) "update help exits 0"
Assert ($r3.Output -match 'FLEET_CONNECT_REPO') "update help mentions env"

# Test 4: mocked full update with progress bar and file write (no network)
# Dot-source functions and mock Invoke-WebRequest / filesystem
$raw = Get-Content -LiteralPath $scriptPath -Raw
$idx = $raw.LastIndexOf("# ---------------------------------------------------------------- entry point")
$funcPart = if ($idx -ge 0) { $raw.Substring(0,$idx) } else { $raw }
# Execute in isolated scope to avoid polluting global, but we need functions in current scope
Invoke-Expression $funcPart

$tmpTarget = Join-Path ([IO.Path]::GetTempPath()) ("fcon-update-test-" + [guid]::NewGuid().ToString("N"))
$null = New-Item -ItemType Directory -Path $tmpTarget -Force
$oldDir = $env:FLEET_CONNECT_DIR; $oldRepo = $env:FLEET_CONNECT_REPO; $oldRef = $env:FLEET_CONNECT_REF; $oldCsv = $env:FLEET_CONNECT_CSV; $oldAppData = $env:LOCALAPPDATA
$env:FLEET_CONNECT_DIR = $tmpTarget
$env:FLEET_CONNECT_REPO = "test/repo"
$env:FLEET_CONNECT_REF = "test-ref"
$env:FLEET_CONNECT_CSV = Join-Path $tmpTarget "pcs.csv"
$env:LOCALAPPDATA = $tmpTarget

# Mock Invoke-WebRequest to return fake fleet-connect.ps1 content
function Invoke-WebRequest { param($Uri,[switch]$UseBasicParsing,$TimeoutSec,$Method)
  if ($Method -eq 'Head') {
    return [pscustomobject]@{ StatusCode = 200 }
  }
  return [pscustomobject]@{ Content = "#Requires -Version 5.1`nWrite-Host 'mocked'" }
}
# Capture Write-Progress calls
$script:progressCalls = @()
function Write-Progress { param([string]$Activity,[string]$Status,[int]$PercentComplete,[switch]$Completed, $CurrentOperation)
  if ($Completed) { $script:progressCalls += "Completed" } else { $script:progressCalls += "$Activity|$Status|$PercentComplete" }
}

try {
  $code = Invoke-Update @()
  Assert ($code -eq 0) "mocked update exits 0 (got $code)"
  $written = Join-Path $tmpTarget 'fleet-connect.ps1'
  Assert (Test-Path -LiteralPath $written) "mocked update writes fleet-connect.ps1"
  Assert ((Get-Content -LiteralPath $written -Raw) -match '#Requires') "written file contains #Requires"
  $shim = Join-Path $tmpTarget 'fcon.cmd'
  Assert (Test-Path -LiteralPath $shim) "mocked update writes fcon.cmd"
  Assert ($script:progressCalls.Count -ge 4) "progress bar called >=4 times (got $($script:progressCalls.Count))"
  Assert ($script:progressCalls -match 'fcon update') "progress bar activity is 'fcon update'"
  Assert ($script:progressCalls -match 'Готово') "progress bar reaches 'Готово'"

  # --check path with mock
  $script:progressCalls = @()
  $code2 = Invoke-Update @('--check')
  Assert ($code2 -eq 0) "mocked update --check exits 0 (got $code2)"
} finally {
  Remove-Item -Recurse -Force $tmpTarget -ErrorAction SilentlyContinue
  $env:FLEET_CONNECT_DIR = $oldDir; $env:FLEET_CONNECT_REPO = $oldRepo; $env:FLEET_CONNECT_REF = $oldRef; $env:FLEET_CONNECT_CSV = $oldCsv; $env:LOCALAPPDATA = $oldAppData
  # Restore original Write-Progress if it was overridden (pwsh built-in)
  Remove-Item Function:\Invoke-WebRequest -ErrorAction SilentlyContinue
  Remove-Item Function:\Write-Progress -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host "`n$failed test(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "`nAll update tests passed" -ForegroundColor Green
exit 0
