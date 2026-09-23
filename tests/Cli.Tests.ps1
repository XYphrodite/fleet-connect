# CLI tests for the .NET fcon.exe (ports Sync.Tests.ps1).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tests/Cli.Tests.ps1
# Needs a published exe at src/fcon/bin/Release/net10.0-windows/win-x64/publish/fcon.exe
# (dotnet publish src/fcon -c Release -r win-x64 --self-contained).
# Without it the suite skips honestly instead of failing.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$exe = Join-Path $PSScriptRoot '..\src\fcon\bin\Release\net10.0-windows\win-x64\publish\fcon.exe'
if (-not (Test-Path -LiteralPath $exe)) {
  Write-Host "SKIP: fcon.exe not built; run 'dotnet publish src/fcon -c Release -r win-x64 --self-contained' first." -ForegroundColor Yellow
  exit 0
}
$exe = (Resolve-Path $exe).Path

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("fcon-cli-test-" + [Guid]::NewGuid().ToString("N"))
$null = New-Item -ItemType Directory -Path $tmp -Force
$csv = Join-Path $tmp "pcs.csv"
Set-Content -LiteralPath $csv -Value 'Name,Host,Protocol,User,SshAlias,Port,Note' -Encoding UTF8

$failed = 0
function Assert($Cond, $Msg) {
  if (-not $Cond) { Write-Host "FAIL: $Msg" -ForegroundColor Red; $script:failed++ } else { Write-Host "PASS: $Msg" -ForegroundColor Green }
}
function Run($TestArgs) {
  $env:FLEET_CONNECT_CSV = $script:csv
  $out = & $script:exe @TestArgs 2>&1 | Out-String
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

# Test 3: add with ssh alias + port shows in list
$r3 = Run @('add','dev','dev-box.ts.net','ssh','--alias','dev','--port','2222')
Assert ($r3.ExitCode -eq 0) "add dev with alias exits 0 (got $($r3.ExitCode))"

# Test 4: list shows the alias column
$r4 = Run @('list')
Assert ($r4.ExitCode -eq 0) "list exits 0 (got $($r4.ExitCode))"
Assert ($r4.Output -match 'alias dev') "list output shows ssh alias"
Assert ($r4.Output -match 'port 2222') "list output shows port"

# Test 5: sync --dry-run should list targets and exit 0 (no ssh required)
$r5 = Run @('sync','--dry-run')
Assert ($r5.ExitCode -eq 0) "sync --dry-run exits 0 (got $($r5.ExitCode))"
Assert ($r5.Output -match 'dry-run') "sync dry-run output contains dry-run"
Assert ($r5.Output -match 'test1') "sync dry-run mentions test1"

# Test 6: sync warns about aliases missing from the local ssh config
Assert ($r5.Output -match 'not in ~/.ssh/config|alias') "sync warns about local alias coverage"

Remove-Item -Recurse -Force $tmp

if ($failed -gt 0) { Write-Host "`n$failed test(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "`nAll CLI tests passed" -ForegroundColor Green
exit 0
