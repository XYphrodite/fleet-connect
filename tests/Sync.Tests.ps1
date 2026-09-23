# Minimal test for fleet-connect sync/add
# Usage: powershell -NoProfile -File tests/Sync.Tests.ps1

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptFull = (Resolve-Path (Join-Path $PSScriptRoot '..\fleet-connect.ps1')).Path
$powershell = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("fleet-test-" + [Guid]::NewGuid().ToString("N"))
$null = New-Item -ItemType Directory -Path $tmp -Force
$csv = Join-Path $tmp "pcs.csv"
Set-Content -LiteralPath $csv -Value 'Name,Host,Protocol,User,SshAlias,Port,Note' -Encoding UTF8

$failed = 0
function Assert($Cond, $Msg) {
    if (-not $Cond) { Write-Host "FAIL: $Msg" -ForegroundColor Red; $script:failed++ } else { Write-Host "PASS: $Msg" -ForegroundColor Green }
}
function Run($TestArgs) {
    $env:FLEET_CONNECT_CSV = $script:csv
    $out = & $script:powershell -NoProfile -File $script:scriptFull @TestArgs 2>&1 | Out-String
    $code = $LASTEXITCODE
    return @{ ExitCode = $code; Output = $out }
}

# Test 1: add via positional
$r = Run @('add','test1','10.0.0.5','rdp','--user','admin')
Assert ($r.ExitCode -eq 0) "add test1 exits 0 (got $($r.ExitCode)) $($r.Output)"
Assert ((Get-Content $csv -Raw) -match 'test1') "csv contains test1"

# Test 2: duplicate add should fail
$r2 = Run @('add','test1','10.0.0.6')
Assert ($r2.ExitCode -eq 1) "duplicate add exits 1 (got $($r2.ExitCode))"

# Test 3: add with ssh alias
$r3 = Run @('add','dev','dev-box.ts.net','ssh','--alias','dev','--port','2222')
Assert ($r3.ExitCode -eq 0) "add dev with alias exits 0 (got $($r3.ExitCode))"
Assert ((Get-Content $csv -Raw) -match 'dev-box') "csv contains dev"

# Test 4: sync --dry-run should list targets and exit 0 (no ssh required for dry-run)
$r4 = Run @('sync','--dry-run')
Assert ($r4.ExitCode -eq 0) "sync --dry-run exits 0 (got $($r4.ExitCode))"
Assert ($r4.Output -match 'dry-run') "sync dry-run output contains dry-run"
Assert ($r4.Output -match 'test1') "sync dry-run mentions test1"

# Test 5: sync with explicit name
$r5 = Run @('sync','--dry-run','test1')
Assert ($r5.ExitCode -eq 0) "sync --dry-run test1 exits 0 (got $($r5.ExitCode))"

# Test 6: list should work
$r6 = Run @('list')
Assert ($r6.ExitCode -eq 0) "list exits 0 (got $($r6.ExitCode))"
Assert ($r6.Output -match 'test1') "list output contains test1"

Remove-Item -Recurse -Force $tmp

if ($failed -gt 0) { Write-Host "`n$failed test(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "`nAll tests passed" -ForegroundColor Green
exit 0
