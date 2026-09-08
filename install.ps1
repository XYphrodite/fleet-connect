<#
.SYNOPSIS
    Installs fleet-connect (the `fcon` command).

.DESCRIPTION
    Meant to be piped straight from the web:

        irm https://raw.githubusercontent.com/XYphrodite/fleet-connect/master/install.ps1 | iex

    fleet-connect is one PowerShell script, so there is nothing to build and no release to
    match: this fetches fcon.ps1, drops it in the per-user programs folder next to a small
    fcon.cmd shim, and puts that folder on PATH. No administrator rights - this runs on the
    machine you connect *from*, not on the machines you connect *to*.

    Run it again to update; it overwrites the script and leaves your machine list alone.

    Because `iex` cannot take parameters, overrides come from environment variables:

        $env:FLEET_CONNECT_REPO = 'owner/name'      # source repository
        $env:FLEET_CONNECT_REF  = 'v1.0.0'          # a tag or branch instead of master
        $env:FLEET_CONNECT_DIR  = 'D:\tools\fcon'   # install somewhere else
#>

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 still negotiates TLS 1.0 by default on some machines.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repo   = if ($env:FLEET_CONNECT_REPO) { $env:FLEET_CONNECT_REPO } else { 'XYphrodite/fleet-connect' }
$ref    = if ($env:FLEET_CONNECT_REF)  { $env:FLEET_CONNECT_REF }  else { 'master' }
$target = if ($env:FLEET_CONNECT_DIR)  { $env:FLEET_CONNECT_DIR }  else { Join-Path $env:LOCALAPPDATA 'Programs\fleet-connect' }

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

$source = "https://raw.githubusercontent.com/$repo/$ref/fcon.ps1"
Write-Step "Fetching $source"
try {
    $script = Invoke-WebRequest -Uri $source -UseBasicParsing -TimeoutSec 30
} catch {
    throw "Could not download fcon.ps1 from $repo ($ref). ($($_.Exception.Message))"
}

# A 404 on raw.githubusercontent comes back as an HTML page rather than an error on some
# proxies, and writing that to fcon.ps1 would leave a command that fails cryptically.
$body = [string]$script.Content
if ($body -notmatch '(?m)^\s*#Requires -Version') {
    throw "What came back from $source is not fcon.ps1. Is the repository published and does it have a $ref branch?"
}

Write-Step "Installing into $target"
New-Item -ItemType Directory -Force -Path $target | Out-Null

$scriptPath = Join-Path $target 'fcon.ps1'
# UTF-8 without a BOM: the script is ASCII, and a BOM only trips up diffing it later.
[IO.File]::WriteAllText($scriptPath, $body, (New-Object Text.UTF8Encoding($false)))

# The shim is what actually makes `fcon` a command: PATHEXT finds the .cmd, and .ps1 files
# are not directly executable from cmd.exe at all. pwsh is preferred when present only
# because it starts faster; the script itself runs on Windows PowerShell 5.1 too.
$shim = @(
    '@echo off',
    'setlocal',
    'set "PS=powershell"',
    'where pwsh >nul 2>nul',
    'if not errorlevel 1 set "PS=pwsh"',
    '"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0fcon.ps1" %*',
    'exit /b %errorlevel%'
)
Set-Content -LiteralPath (Join-Path $target 'fcon.cmd') -Value $shim -Encoding ASCII

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
Write-Host "fleet-connect installed to $target" -ForegroundColor Green
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
