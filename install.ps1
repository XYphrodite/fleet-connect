<#
.SYNOPSIS
    Installs fleet-connect (the `fcon` command).

.DESCRIPTION
    Meant to be piped straight from the web:

        irm https://raw.githubusercontent.com/XYphrodite/fleet-connect/master/install.ps1 | iex

    fleet-connect is a single self-contained .NET exe, so there is no runtime to
    install: this fetches fcon.exe from the latest GitHub release, drops it in the
    per-user programs folder, and puts that folder on PATH. No administrator
    rights - this runs on the machine you connect *from*, not on the machines
    you connect *to*.

    Run it again to reinstall; it keeps a .bak of the previous exe and leaves
    your machine list alone. Day to day, `fcon update` does the same from inside
    the tool.

    Because `iex` cannot take parameters, overrides come from environment variables:

        $env:FLEET_CONNECT_REPO = 'owner/name'      # source repository
        $env:FLEET_CONNECT_REF  = 'v1.0.0'          # a release tag instead of latest
        $env:FLEET_CONNECT_DIR  = 'D:\tools\fcon'   # install somewhere else
#>

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 still negotiates TLS 1.0 by default on some machines.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo   = if ($env:FLEET_CONNECT_REPO) { $env:FLEET_CONNECT_REPO } else { 'XYphrodite/fleet-connect' }
$ref    = if ($env:FLEET_CONNECT_REF)  { $env:FLEET_CONNECT_REF }  else { 'latest' }
$target = if ($env:FLEET_CONNECT_DIR)  { $env:FLEET_CONNECT_DIR }  else { Join-Path $env:LOCALAPPDATA 'Programs\fleet-connect' }

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

if ($ref -eq 'latest') {
    $source = "https://github.com/$repo/releases/latest/download/fcon.exe"
} else {
    $source = "https://github.com/$repo/releases/download/$ref/fcon.exe"
}
Write-Step "Fetching $source"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('fcon-install-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    Invoke-WebRequest -Uri $source -UseBasicParsing -TimeoutSec 120 -OutFile $tmp -ErrorAction Stop
} catch {
    throw "Could not download fcon.exe from $repo ($ref). ($($_.Exception.Message))"
}

# A 404 or an error page downloads as a small HTML/text file rather than failing
# on some proxies; a real single-file exe starts with MZ and weighs megabytes.
$info = Get-Item -LiteralPath $tmp
$magic = New-Object byte[] 2
$stream = [IO.File]::OpenRead($tmp)
try { $null = $stream.Read($magic, 0, 2) } finally { $stream.Close() }
if ($info.Length -lt 256KB -or $magic[0] -ne 0x4D -or $magic[1] -ne 0x5A) {
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    throw "What came back from $source is not the tool. Is the release published under $repo ($ref)?"
}

Write-Step "Installing into $target"
New-Item -ItemType Directory -Force -Path $target | Out-Null

$exePath = Join-Path $target 'fcon.exe'
if (Test-Path -LiteralPath $exePath) {
    Copy-Item -LiteralPath $exePath -Destination "$exePath.bak" -Force
}
Copy-Item -LiteralPath $tmp -Destination $exePath -Force
Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
if (-not (Test-Path -LiteralPath $exePath)) { throw "Install failed: $exePath is missing after the copy." }

# Versions up to this one installed a PowerShell payload with a fcon.cmd shim.
# The exe answers to `fcon` on its own (PATHEXT prefers .exe over .cmd), so the
# old files are removed rather than left shadowing anything.
foreach ($stale in @('fcon.cmd', 'fleet-connect.ps1', 'fcon.ps1')) {
    $p = Join-Path $target $stale
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
}

# Put it on PATH for future shells, and on this one so it can be run right away.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $target) {
    Write-Step 'Adding to your PATH'
    $updated = if ([string]::IsNullOrEmpty($userPath)) { $target } else { "$userPath;$target" }
    [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
}
if (($env:Path -split ';') -notcontains $target) { $env:Path = "$env:Path;$target" }

$listPath = if ($env:FLEET_CONNECT_CSV) { $env:FLEET_CONNECT_CSV }
            else { Join-Path $env:LOCALAPPDATA 'fleet-connect\pcs.csv' }
$fresh = -not (Test-Path -LiteralPath $listPath)
if ($fresh) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $listPath) | Out-Null
    Set-Content -LiteralPath $listPath -Value 'Name,Host,Protocol,User,SshAlias,Port,Note' -Encoding UTF8
}

Write-Host ''
Write-Host "fcon installed to $target" -ForegroundColor Green
Write-Host ''
Write-Host 'Next:' -ForegroundColor Cyan
if ($fresh) {
    Write-Host '  fcon import   # read your machines out of the tailnet'
}
Write-Host '  fcon          # pick a machine and connect'
Write-Host '  fcon edit     # fill in user names, ssh aliases and notes'
Write-Host '  fcon help'
Write-Host ''
Write-Host "Your machine list: $listPath" -ForegroundColor DarkGray
Write-Host 'Open a new terminal if `fcon` is not found in an existing one.' -ForegroundColor DarkGray
