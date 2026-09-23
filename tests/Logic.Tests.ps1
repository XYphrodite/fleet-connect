# Cross-platform logic tests for the .NET port (no Windows required).
# Usage: powershell -NoProfile -ExecutionPolicy Bypass -File tests/Logic.Tests.ps1
# Needs the .NET 10 SDK; builds and runs src/fcon.LogicTests, which links the
# real fcon sources (except the Windows-only setup/update bodies) and drives
# the public CLI headlessly: picker fallback, name, name ssh/rdp, add, list,
# sync --dry-run, import, routing and all switch aliases, asserting outputs
# and exit codes 0/1/2.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
  Write-Host "SKIP: dotnet SDK not found; install .NET 10 SDK to run the logic tests." -ForegroundColor Yellow
  exit 0
}
$proj = Join-Path $PSScriptRoot '..\src\fcon.LogicTests\fcon.LogicTests.csproj'
& $dotnet.Source run --project $proj 2>&1 | ForEach-Object { $_ }
if ($LASTEXITCODE -ne 0) {
  Write-Host "`nLogic tests FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
  exit 1
}
Write-Host "`nLogic tests passed" -ForegroundColor Green
exit 0
