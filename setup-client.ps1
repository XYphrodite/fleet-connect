#Requires -Version 5.1
<#
.SYNOPSIS
    Install fcon and prepare this Windows user to connect to one remote PC.
.DESCRIPTION
    Creates a local SSH key, updates one CSV entry and SSH alias, and optionally
    returns the public key through Taildrop. Does not configure the remote server,
    create Windows accounts, change firewalls, or send private keys/passwords.
#>
[CmdletBinding()]
param(
    [string]$Name,
    [string]$Address,
    [string]$User,
    [string]$HostKey,
    [switch]$SendKey
)

$ErrorActionPreference = 'Stop'
if (-not $Name) { $Name = Read-Host 'Name in fcon (for example home)' }
if (-not $Address) { $Address = Read-Host 'Tailscale IP or DNS name of the remote PC' }
if (-not $User) { $User = Read-Host 'Windows user on the remote PC' }
if ($Name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_.-]*$') { throw 'Name must contain letters, digits, dots, underscores or hyphens.' }
if ($Address -notmatch '^[a-zA-Z0-9][a-zA-Z0-9:._-]*$') { throw 'Address must be an IP address or DNS name, without a port.' }
if (-not $User -or $User -match '[\r\n"]' -or $User.StartsWith('-')) { throw 'Invalid remote user name.' }
if ($HostKey -and $HostKey -notmatch '^ssh-ed25519 [A-Za-z0-9+/]+={0,2}$') { throw 'HostKey must be an ed25519 public host key without a comment.' }
$ssh = Get-Command ssh.exe -ErrorAction SilentlyContinue
$keygen = Get-Command ssh-keygen.exe -ErrorAction SilentlyContinue
if (-not $ssh -or -not $keygen) { throw 'Install the Windows OpenSSH Client optional feature first.' }
$tailscale = $null
if ($SendKey) {
    $tailscale = Get-Command tailscale.exe -ErrorAction SilentlyContinue
    if (-not $tailscale) {
        $candidate = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
        if (Test-Path -LiteralPath $candidate) { $tailscale = Get-Item -LiteralPath $candidate }
    }
    if (-not $tailscale) { throw 'Tailscale is required for -SendKey.' }
}
function Test-DotNet10 {
    try { $out = & dotnet --list-runtimes 2>$null; if ($LASTEXITCODE -eq 0 -and $out -match 'Microsoft\.NETCore\.App 10\.') { return $true } } catch {}
    try { $d = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'; if (Test-Path -LiteralPath $d) { $out = & $d --list-runtimes 2>$null; if ($out -match 'Microsoft\.NETCore\.App 10\.') { return $true } } } catch {}
    $shared = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App'
    if (Test-Path -LiteralPath $shared) { try { if (Get-ChildItem -LiteralPath $shared -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '10.*' }) { return $true } } catch {} }
    return $false
}
# setup-client also auto-detects the runtime so every downloader agrees on the flavour.
if ($null -eq $env:FLEET_CONNECT_FRAMEWORK -or $env:FLEET_CONNECT_FRAMEWORK -eq '') {
    # propagate auto choice to the nested install.ps1 invocation (it probes again, but keep env consistent)
    $autoUseFramework = Test-DotNet10
    if ($autoUseFramework) { Write-Host "Detected .NET 10 runtime - setup will fetch framework-dependent build" -ForegroundColor DarkGray }
    else { Write-Host "No .NET 10 runtime - setup will fetch self-contained build" -ForegroundColor DarkGray }
}
$programDir = if ($env:FLEET_CONNECT_DIR) { $env:FLEET_CONNECT_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\fleet-connect' }
# Install fcon if the exe is missing, or if this is a legacy PS-only install (shim without exe).
$needInstall = -not (Test-Path -LiteralPath (Join-Path $programDir 'fcon.exe'))
if (-not $needInstall) {
    $legacyShim = Test-Path -LiteralPath (Join-Path $programDir 'fcon.cmd')
    $legacyPs = Test-Path -LiteralPath (Join-Path $programDir 'fleet-connect.ps1')
    if (-not $legacyShim -and -not $legacyPs) { $needInstall = $false } elseif ($legacyShim -or $legacyPs) { $needInstall = $false }
    # Actually: old installs had both shim+ps; if exe is there we are already on .NET, no need to reinstall.
    # Keep the check simple: exe present means done.
}
if ($needInstall) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $repo = if ($env:FLEET_CONNECT_REPO) { $env:FLEET_CONNECT_REPO } else { 'XYphrodite/fleet-connect' }
    $ref = if ($env:FLEET_CONNECT_REF) { $env:FLEET_CONNECT_REF } else { 'master' }
    $installer = (Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/$repo/$ref/install.ps1" -TimeoutSec 30).Content
    & ([scriptblock]::Create($installer))
}
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$utf8 = [Text.UTF8Encoding]::new($false)
$sshDir = Join-Path $env:USERPROFILE '.ssh'
New-Item -ItemType Directory -Path $sshDir -Force | Out-Null
$keyPath = Join-Path $sshDir 'id_ed25519_fcon'
if (-not (Test-Path -LiteralPath $keyPath)) {
    if (Test-Path -LiteralPath "$keyPath.pub") { throw 'Public key exists without its private key; refusing to replace it.' }
    # Windows PowerShell 5.1 needs literal quotes to pass an empty native argument.
    if ($PSVersionTable.PSVersion.Major -le 5 -or $PSVersionTable.PSVersion -lt [version]'7.3') {
        & $keygen.Source -q -t ed25519 -f $keyPath -N '""' -C "fcon@$env:COMPUTERNAME"
    } else {
        & $keygen.Source -q -t ed25519 -f $keyPath -N '' -C "fcon@$env:COMPUTERNAME"
    }
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate the SSH key.' }
}
if (-not (Test-Path -LiteralPath "$keyPath.pub")) { throw 'Public key is missing; existing private key was preserved.' }
$alias = 'fcon-' + $Name.ToLowerInvariant()
$knownPath = Join-Path $sshDir ($alias + '_known_hosts')
if ($HostKey) {
    $line = "$Address $HostKey"
    if ((Test-Path -LiteralPath $knownPath) -and [IO.File]::ReadAllText($knownPath).Trim() -ne $line) {
        throw "A different server key is already pinned in $knownPath. Verify the server identity before changing it."
    }
    [IO.File]::WriteAllText($knownPath, $line + "`n", $utf8)
} elseif (Test-Path -LiteralPath $knownPath) {
    if (([IO.File]::ReadAllText($knownPath).Trim() -split '\s+')[0] -ne $Address) {
        throw "The existing server key is pinned to a different address in $knownPath. Verify it before changing the destination."
    }
}
$configPath = Join-Path $sshDir 'config'
$oldConfig = if (Test-Path -LiteralPath $configPath) { [IO.File]::ReadAllText($configPath) } else { '' }
$marker = 'FCON CLIENT ' + $alias
$oldConfig = [regex]::Replace($oldConfig, '(?ms)^# BEGIN ' + [regex]::Escape($marker) + '\r?\n.*?^# END ' + [regex]::Escape($marker) + '\r?\n?', '')
$block = @("# BEGIN $marker", "Host $alias", "    HostName $Address", "    User `"$User`"", '    IdentityFile ~/.ssh/id_ed25519_fcon', '    IdentitiesOnly yes', '    ConnectTimeout 8')
# Preserve an existing pin even when rerunning without -HostKey.
if (Test-Path -LiteralPath $knownPath) {
    $block += @("    UserKnownHostsFile ~/.ssh/$($alias)_known_hosts", '    StrictHostKeyChecking yes')
} else {
    $block += @('    StrictHostKeyChecking ask')
}
$block += @("# END $marker", '')
if (Test-Path -LiteralPath $configPath) { Copy-Item -LiteralPath $configPath -Destination "$configPath.$stamp.bak" }
[IO.File]::WriteAllText($configPath, ($block -join "`r`n") + $oldConfig, $utf8)
$csvPath = if ($env:FLEET_CONNECT_CSV) { $env:FLEET_CONNECT_CSV } else { Join-Path $env:LOCALAPPDATA 'fleet-connect\pcs.csv' }
New-Item -ItemType Directory -Path (Split-Path -Parent $csvPath) -Force | Out-Null
$rows = @()
if (Test-Path -LiteralPath $csvPath) {
    Copy-Item -LiteralPath $csvPath -Destination "$csvPath.$stamp.bak"
    $rows = @(Import-Csv -LiteralPath $csvPath -Encoding UTF8)
}
$row = $rows | Where-Object { $_.Name -eq $Name -or $_.Host -eq $Address } | Select-Object -First 1
if ($row) {
    foreach ($field in @{Host=$Address;User=$User;SshAlias=$alias}.GetEnumerator()) { $row | Add-Member -MemberType NoteProperty -Name $field.Key -Value $field.Value -Force }
} else {
    $rows += [pscustomobject]@{Name=$Name;Host=$Address;Protocol='Rdp';User=$User;SshAlias=$alias;Port='';Note=''}
}
$rows | Select-Object Name,Host,Protocol,User,SshAlias,Port,Note | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding UTF8
Write-Host "Ready: fcon $Name rdp / fcon $Name ssh"
Write-Host 'RDP uses the remote Windows password. SSH needs the administrator to authorize this public key:'
Get-Content -LiteralPath "$keyPath.pub"
if ($SendKey) {
    $returnName = 'fcon-' + ($env:COMPUTERNAME -replace '[^a-zA-Z0-9_.-]', '-') + '.pub'
    $tsPath = if ($tailscale -is [IO.FileInfo]) { $tailscale.FullName } else { $tailscale.Source }
    & $tsPath file cp --name $returnName "$keyPath.pub" "${Address}:"
    if ($LASTEXITCODE -ne 0) { throw "Taildrop failed; send only $keyPath.pub to the remote administrator manually." }
    Write-Host 'Public key sent through Taildrop. SSH will work after the administrator authorizes it.'
}
