<#
.SYNOPSIS
    Installs fleet-connect (the `fcon` command).

.DESCRIPTION
    Meant to be piped straight from the web:

        irm https://raw.githubusercontent.com/XYphrodite/fleet-connect/master/install.ps1 | iex

    fleet-connect is a .NET 10 single-file exe. Two flavours are published:
    self-contained fcon.exe (no runtime needed, ~70 MiB) and framework-dependent
    fcon-framework.exe (needs .NET 10 runtime, ~0.24 MiB). The installer
    auto-detects the runtime and picks the right one: if dotnet lists a
    10.x runtime, the tiny framework build is fetched, otherwise the
    self-contained one. No administrator rights - this runs on the machine
    you connect *from*, not on the machines you connect *to*.

    Run it again to reinstall; it keeps a .bak of the previous exe and leaves
    your machine list alone. Day to day, `fcon update` does the same from inside
    the tool.

    Because `iex` cannot take parameters, overrides come from environment variables:

        $env:FLEET_CONNECT_REPO = 'owner/name'      # source repository
        $env:FLEET_CONNECT_REF  = 'v1.0.0'          # a release tag instead of latest
        $env:FLEET_CONNECT_DIR  = 'D:\tools\fcon'   # install somewhere else
        $env:FLEET_CONNECT_FRAMEWORK = '1'          # force framework-dependent (needs .NET 10)
        $env:FLEET_CONNECT_FRAMEWORK = '0'          # force self-contained
        $env:FLEET_CONNECT_FRAMEWORK = 'auto'       # auto-detect (default)

    Auto-detect checks `dotnet --list-runtimes` and the shared folder
    `C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.*`. Every script
    that downloads fcon (this one, setup-client.ps1, fcon update) uses the same
    check, so the choice stays consistent.
#>

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 still negotiates TLS 1.0 by default on some machines.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Test-DotNet10 {
    # Every downloader must agree on this check (setup-client.ps1, fcon update).
    try {
        $out = & dotnet --list-runtimes 2>$null
        if ($LASTEXITCODE -eq 0 -and $out -match 'Microsoft\.NETCore\.App 10\.') { return $true }
    } catch {}
    try {
        $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        if (Test-Path -LiteralPath $dotnet) {
            $out = & $dotnet --list-runtimes 2>$null
            if ($out -match 'Microsoft\.NETCore\.App 10\.') { return $true }
        }
    } catch {}
    $shared = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.NETCore.App'
    if (Test-Path -LiteralPath $shared) {
        try {
            if (Get-ChildItem -LiteralPath $shared -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '10.*' }) { return $true }
        } catch {}
    }
    # Also check dotnet in DOTNET_ROOT or UserProfile
    foreach ($root in @($env:DOTNET_ROOT, (Join-Path $env:LOCALAPPDATA 'Programs\dotnet'), $env:ProgramFiles)) {
        if (-not $root) { continue }
        $p = Join-Path $root 'shared\Microsoft.NETCore.App'
        if (Test-Path -LiteralPath $p) {
            try { if (Get-ChildItem -LiteralPath $p -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '10.*' }) { return $true } } catch {}
        }
    }
    return $false
}

$repo   = if ($env:FLEET_CONNECT_REPO) { $env:FLEET_CONNECT_REPO } else { 'XYphrodite/fleet-connect' }
$ref    = if ($env:FLEET_CONNECT_REF)  { $env:FLEET_CONNECT_REF }  else { 'latest' }
$target = if ($env:FLEET_CONNECT_DIR)  { $env:FLEET_CONNECT_DIR }  else { Join-Path $env:LOCALAPPDATA 'Programs\fleet-connect' }
# FLEET_CONNECT_FRAMEWORK: explicit 1/true -> framework, 0/false -> self-contained,
# auto/empty/unset -> probe the machine. Set it to force a flavour regardless of probe.
$frameworkRaw = $env:FLEET_CONNECT_FRAMEWORK
if ($null -ne $frameworkRaw -and $frameworkRaw -ne '') {
    $low = $frameworkRaw.ToString().ToLowerInvariant()
    if ($low -eq '0' -or $low -eq 'false' -or $low -eq 'no' -or $low -eq 'off') { $useFramework = $false }
    elseif ($low -eq '1' -or $low -eq 'true' -or $low -eq 'yes' -or $low -eq 'on') { $useFramework = $true }
    elseif ($low -eq 'auto' -or $low -eq 'detect') { $useFramework = Test-DotNet10 }
    else { $useFramework = $true } # any other truthy string forces framework
} else {
    $useFramework = Test-DotNet10
}
$exeName = if ($useFramework) { 'fcon-framework.exe' } else { 'fcon.exe' }

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

if ($useFramework) {
    Write-Host "Detected .NET 10 runtime - using framework-dependent build ($exeName, ~0.24 MiB)" -ForegroundColor DarkGray
} else {
    Write-Host "No .NET 10 runtime detected - using self-contained build ($exeName, ~70 MiB)" -ForegroundColor DarkGray
}
if ($null -ne $frameworkRaw -and $frameworkRaw -ne '' -and $frameworkRaw.ToString().ToLowerInvariant() -ne 'auto' -and $frameworkRaw.ToString().ToLowerInvariant() -ne 'detect') {
    Write-Host "  (forced by FLEET_CONNECT_FRAMEWORK=$frameworkRaw)" -ForegroundColor DarkGray
}

if ($ref -eq 'latest') {
    $source = "https://github.com/$repo/releases/latest/download/$exeName"
} else {
    $source = "https://github.com/$repo/releases/download/$ref/$exeName"
}
# For auto-detect, the requested flavour may not be published in older releases.
# Probe it first; if the framework asset is missing, fall back to self-contained.
if ($useFramework) {
    $probeOk = $false
    try {
        $probeReq = [Net.HttpWebRequest]::Create($source)
        $probeReq.Method = 'HEAD'
        $probeReq.Timeout = 8000
        $resp = $probeReq.GetResponse()
        $probeOk = [int]$resp.StatusCode -ge 200 -and [int]$resp.StatusCode -lt 400
        $resp.Close()
    } catch {
        $probeOk = $false
        # 404 is expected when the release predates the framework flavour
        if ($_.Exception.Message -match '404') { $probeOk = $false }
    }
    if (-not $probeOk) {
        # Only auto-detected picks are allowed to fall back; explicit FLEET_CONNECT_FRAMEWORK=1 stays strict.
        $isExplicitFramework = $null -ne $frameworkRaw -and $frameworkRaw -ne '' -and $frameworkRaw.ToString().ToLowerInvariant() -notin @('auto','detect')
        if (-not $isExplicitFramework) {
            Write-Host "Framework asset not found at $source, falling back to self-contained fcon.exe" -ForegroundColor Yellow
            $exeName = 'fcon.exe'
            $useFramework = $false
            if ($ref -eq 'latest') { $source = "https://github.com/$repo/releases/latest/download/$exeName" }
            else { $source = "https://github.com/$repo/releases/download/$ref/$exeName" }
        }
    }
}
Write-Step "Fetching $source"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('fcon-install-' + [guid]::NewGuid().ToString('N') + '.exe')
try {
    Invoke-WebRequest -Uri $source -UseBasicParsing -TimeoutSec 120 -OutFile $tmp -ErrorAction Stop
} catch {
    throw "Could not download $exeName from $repo ($ref). ($($_.Exception.Message))"
}

# A 404 or an error page downloads as a small HTML/text file rather than failing
# on some proxies; a real single-file exe starts with MZ.
$info = Get-Item -LiteralPath $tmp
$magic = New-Object byte[] 2
$stream = [IO.File]::OpenRead($tmp)
try { $null = $stream.Read($magic, 0, 2) } finally { $stream.Close() }
if ($info.Length -lt 40KB -or $magic[0] -ne 0x4D -or $magic[1] -ne 0x5A) {
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
