# Tests for list rendering (alias/port shown) and local ssh-alias detection.
# Usage: pwsh -NoProfile -File tests/List.Tests.ps1  (or powershell)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = (Resolve-Path (Join-Path $PSScriptRoot '..\fleet-connect.ps1')).Path

$failed = 0
function Assert($Cond, $Msg) {
  if (-not $Cond) { Write-Host "FAIL: $Msg" -ForegroundColor Red; $script:failed++ } else { Write-Host "PASS: $Msg" -ForegroundColor Green }
}

$raw = Get-Content -LiteralPath $scriptPath -Raw
$idx = $raw.LastIndexOf("# ---------------------------------------------------------------- entry point")
$funcPart = if ($idx -ge 0) { $raw.Substring(0,$idx) } else { $raw }
Invoke-Expression $funcPart

function New-TestPc($Name, $Address, $Proto, $User, $Alias, $Port, $Note) {
  $pc = [PcModel]::new()
  $pc.Name = $Name
  $pc.Address = $Address
  $pc.Protocol = $Proto
  $pc.User = $User
  $pc.SshAlias = $Alias
  $pc.Port = $Port
  $pc.Note = $Note
  return $pc
}

# Row with alias + port shows both; row without them is unchanged.
$withAlias = New-TestPc 'xeon' 'desktop-ib88isg.tail08a9a5.ts.net' ([PcProtocol]::Rdp) 'local' 'fcon-xeon' '' 'Xeon'
$plain = New-TestPc 'nuc' '100.100.10.14' ([PcProtocol]::Ssh) 'root' '' '2222' ''
$lines = Format-PcLines @($withAlias, $plain)
Assert ($lines.Count -eq 2) "two rows render two lines (got $($lines.Count))"
Assert ($lines[0] -match 'alias fcon-xeon') "aliased row shows its ssh alias ($($lines[0]))"
Assert ($lines[1] -notmatch 'alias ') "plain row shows no alias ($($lines[1]))"
Assert ($lines[1] -match 'port 2222') "plain row shows its non-default port ($($lines[1]))"

# Local alias detection reads Host blocks, skips wildcards, tolerates no config.
$savedProfile = [Environment]::GetEnvironmentVariable('USERPROFILE', 'Process')
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("fcon-list-test-" + [guid]::NewGuid().ToString("N"))
$null = New-Item -ItemType Directory -Path $tmp -Force
try {
  $env:USERPROFILE = $tmp
  $none = Get-LocalSshAliases
  Assert ($none.Count -eq 0) "no ssh config answers zero aliases (got $($none.Count))"
  $cfgDir = Join-Path $tmp '.ssh'
  $null = New-Item -ItemType Directory -Path $cfgDir -Force
  $cfg = Join-Path $cfgDir 'config'
  [IO.File]::WriteAllText($cfg, "Host fcon-xeon`r`n    HostName example.ts.net`r`n`r`nHost wsl desktop-ib88isg-wsl`r`n    HostName 100.74.101.71`r`n`r`nHost *`r`n    ServerAliveInterval 60`r`n")
  $found = Get-LocalSshAliases
  Assert ($found -contains 'fcon-xeon') "finds fcon-xeon alias"
  Assert ($found -contains 'wsl') "finds multi-name Host block entries"
  Assert ($found -notcontains '*') "skips wildcard Host blocks"
  Assert ($found.Count -eq 3) "exactly three aliases parsed (got $($found.Count): $($found -join ','))"
} finally {
  [Environment]::SetEnvironmentVariable('USERPROFILE', $savedProfile, 'Process')
  Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

if ($failed -gt 0) { Write-Host "`n$failed test(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "`nAll list tests passed" -ForegroundColor Green
exit 0
