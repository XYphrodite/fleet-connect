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

# Mock Invoke-WebRequest to return fake fleet-connect.ps1 content.
# $script:mockVersion selects the remote version; $script:mockBroken serves garbage.
$script:mockVersion = '1.0.1'
$script:mockBroken = $false
function Invoke-WebRequest { param($Uri,[switch]$UseBasicParsing,$TimeoutSec,$Method)
  if ($script:mockBroken) {
    return [pscustomobject]@{ Content = "<html>not the script</html>" }
  }
  return [pscustomobject]@{ Content = "#Requires -Version 5.1`n`$FLEET_CONNECT_VERSION = '$script:mockVersion'`nWrite-Host 'mocked'" }
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
  Assert ($script:progressCalls -match 'Done') "progress bar reaches 'Done'"

  # --check path with mock
  $script:progressCalls = @()
  $code2 = Invoke-Update @('--check')
  Assert ($code2 -eq 0) "mocked update --check exits 0 (got $code2)"

  # version comparisons are numeric, not lexical
  Assert ((Compare-ScriptVersion '1.0.0' '1.0.1') -eq -1) '1.0.0 < 1.0.1'
  Assert ((Compare-ScriptVersion '1.0.1' '1.0.0') -eq 1) '1.0.1 > 1.0.0'
  Assert ((Compare-ScriptVersion '1.0.0' '1.0.0') -eq 0) '1.0.0 == 1.0.0'
  Assert ((Compare-ScriptVersion '1.9.0' '1.10.0') -eq -1) '1.9.0 < 1.10.0 (numeric)'
  Assert ((Get-ScriptVersion "#Requires -Version 5.1`n`$FLEET_CONNECT_VERSION = '2.3.4'") -eq '2.3.4') 'version marker parses'
  Assert ($null -eq (Get-ScriptVersion '#Requires -Version 5.1')) 'missing marker parses as null'

  # up to date: same version is not rewritten
  $script:mockVersion = '1.0.0'
  Set-Content -LiteralPath $written -Value 'old sentinel' -Encoding ASCII
  $code3 = Invoke-Update @()
  Assert ($code3 -eq 0) "up-to-date update exits 0 (got $code3)"
  Assert ((Get-Content -LiteralPath $written -Raw) -match 'old sentinel') 'up-to-date update leaves the file alone'

  # --force reinstalls the same version
  $code4 = Invoke-Update @('--force')
  Assert ($code4 -eq 0) "forced update exits 0 (got $code4)"
  Assert ((Get-Content -LiteralPath $written -Raw) -match 'mocked') 'forced update rewrites the file'
  $bak = Join-Path $tmpTarget 'fleet-connect.ps1.bak'
  Assert (Test-Path -LiteralPath $bak) 'successful update keeps a .bak'
  Assert ((Get-Content -LiteralPath $bak -Raw) -match 'old sentinel') '.bak holds the previous version'

  # garbage from the server is refused and the install is preserved
  Set-Content -LiteralPath $written -Value 'precious install' -Encoding ASCII
  $script:mockBroken = $true
  $threw = $false
  try { Invoke-Update @() | Out-Null } catch { $threw = $true }
  $script:mockBroken = $false
  Assert $threw 'garbage body throws instead of installing'
  Assert ((Get-Content -LiteralPath $written -Raw) -match 'precious install') 'refused update preserves the install'

  # --check reports versions both ways
  $script:mockVersion = '1.2.0'
  $code5 = Invoke-Update @('--check')
  Assert ($code5 -eq 0) "newer --check exits 0 (got $code5)"
  $script:mockVersion = '1.0.0'
  $code6 = Invoke-Update @('--check')
  Assert ($code6 -eq 0) "current --check exits 0 (got $code6)"
} finally {
  Remove-Item -Recurse -Force $tmpTarget -ErrorAction SilentlyContinue
  $env:FLEET_CONNECT_DIR = $oldDir; $env:FLEET_CONNECT_REPO = $oldRepo; $env:FLEET_CONNECT_REF = $oldRef; $env:FLEET_CONNECT_CSV = $oldCsv; $env:LOCALAPPDATA = $oldAppData
  # Restore original Write-Progress if it was overridden (pwsh built-in)
  Remove-Item Function:\Invoke-WebRequest -ErrorAction SilentlyContinue
  Remove-Item Function:\Write-Progress -ErrorAction SilentlyContinue
}

# Test 5: file must be ASCII only (Windows PowerShell 5.1 compat) and parse without errors
$bytes = [IO.File]::ReadAllBytes($scriptPath)
$nonAscii = @($bytes | Where-Object { $_ -gt 127 })
Assert ($nonAscii.Count -eq 0) "fleet-connect.ps1 is ASCII only for WinPS 5.1 compat (non-ASCII bytes: $($nonAscii.Count))"
$tokensErr = $null; $parseErrs = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokensErr, [ref]$parseErrs)
Assert ($parseErrs.Count -eq 0) "fleet-connect.ps1 parses with 0 errors (got $($parseErrs.Count))"

if ($failed -gt 0) { Write-Host "`n$failed test(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "`nAll update tests passed" -ForegroundColor Green
exit 0
