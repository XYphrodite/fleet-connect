$ErrorActionPreference = 'Stop'
$setup = Join-Path (Split-Path -Parent $PSScriptRoot) 'setup-client.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('fcon-setup-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$saved = @{}
foreach ($name in @('USERPROFILE','LOCALAPPDATA','FLEET_CONNECT_DIR','FLEET_CONNECT_CSV')) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    Write-Host "PASS: $Message"
}
try {
    $shells = @((Get-Command powershell.exe).Source)
    if (Get-Command pwsh.exe -ErrorAction SilentlyContinue) { $shells += (Get-Command pwsh.exe).Source }
    foreach ($shell in $shells) {
        $case = Join-Path $root ([IO.Path]::GetFileNameWithoutExtension($shell))
        $env:USERPROFILE = Join-Path $case 'profile with spaces'
        $env:LOCALAPPDATA = Join-Path $case 'app data'
        $env:FLEET_CONNECT_DIR = Join-Path $env:LOCALAPPDATA 'Programs\fleet-connect'
        $env:FLEET_CONNECT_CSV = Join-Path $env:LOCALAPPDATA 'fleet-connect\pcs.csv'
        New-Item -ItemType Directory -Path $env:FLEET_CONNECT_DIR, (Split-Path -Parent $env:FLEET_CONNECT_CSV), (Join-Path $env:USERPROFILE '.ssh') -Force | Out-Null
        # Existing installation prevents downloading or touching the real user PATH.
        Set-Content (Join-Path $env:FLEET_CONNECT_DIR 'fcon.cmd') '@echo off'
        Set-Content (Join-Path $env:FLEET_CONNECT_DIR 'fleet-connect.ps1') '# test fixture'
        $config = Join-Path $env:USERPROFILE '.ssh\config'
        $originalConfig = "Host personal`r`n    HostName personal.example`r`n"
        [IO.File]::WriteAllText($config, $originalConfig)
        [pscustomobject]@{Name='personal';Host='personal.example';Protocol='Ssh';User='existing';SshAlias='personal';Port='';Note='keep me'} | Export-Csv $env:FLEET_CONNECT_CSV -NoTypeInformation -Encoding UTF8
        $argsSetup = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$setup,'-Name','home','-Address','100.100.100.10','-User','remote')
        & $shell @argsSetup | Out-Null
        Assert ($LASTEXITCODE -eq 0) "$shell first run"
        $key = Join-Path $env:USERPROFILE '.ssh\id_ed25519_fcon'
        $beforeHash = (Get-FileHash $key).Hash
        & $shell @argsSetup | Out-Null
        Assert ($LASTEXITCODE -eq 0) "$shell second run"
        Assert ((Get-FileHash $key).Hash -eq $beforeHash) 'Existing private key preserved'
        $rows = @(Import-Csv $env:FLEET_CONNECT_CSV)
        Assert ($rows.Count -eq 2) 'Repeated setup does not duplicate rows'
        Assert (($rows | Where-Object Name -eq personal).Note -eq 'keep me') 'Other machine metadata preserved'
        $text = [IO.File]::ReadAllText($config)
        Assert ($text.Contains($originalConfig)) 'Other SSH configuration preserved'
        Assert ([regex]::Matches($text, '(?m)^Host fcon-home\r?$').Count -eq 1) 'Managed SSH alias is unique'
        $pub = ((Get-Content "$key.pub" -Raw).Trim() -split '\s+')[0..1] -join ' '
        & $shell @argsSetup -HostKey $pub | Out-Null
        Assert ($LASTEXITCODE -eq 0) 'Server public key can be pinned'
        & $shell @argsSetup | Out-Null
        Assert ($LASTEXITCODE -eq 0) 'Rerun without HostKey keeps the pin'
        Assert ([IO.File]::ReadAllText($config).Contains('StrictHostKeyChecking yes')) 'Pinned host stays strict'
        $beforeConfig = [IO.File]::ReadAllText($config)
        $beforeCsv = [IO.File]::ReadAllText($env:FLEET_CONNECT_CSV)
        $ErrorActionPreference = 'Continue'
        & $shell @argsSetup -HostKey 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' 2>$null | Out-Null
        $ErrorActionPreference = 'Stop'
        Assert ($LASTEXITCODE -ne 0) 'Conflicting server key rejected'
        Assert ([IO.File]::ReadAllText($config) -eq $beforeConfig) 'Key conflict leaves SSH config unchanged'
        Assert ([IO.File]::ReadAllText($env:FLEET_CONNECT_CSV) -eq $beforeCsv) 'Key conflict leaves CSV unchanged'
        Assert (@(Get-ChildItem (Split-Path $config) -Filter 'config.*.bak').Count -gt 0) 'Configuration backups created'
    }
} finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    $resolved = [IO.Path]::GetFullPath($root)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'fcon-setup-test-*') { throw 'Refusing cleanup outside the test directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Host 'All setup tests passed.'
