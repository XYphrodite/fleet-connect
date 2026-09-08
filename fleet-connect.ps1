#Requires -Version 5.1
<#
.SYNOPSIS
    fleet-connect - pick a machine from a list and open RDP or SSH to it.

.DESCRIPTION
    The machine list is a CSV; one row is one PC. Nothing here stores a password:
    RDP is handed a user name and Windows asks for the rest, SSH goes out through the
    local OpenSSH client and whatever ~/.ssh/config already says about that host.

        fcon                  pick from the list
        fcon gpu              connect to the machine named gpu, its own protocol
        fcon gpu ssh          the same machine over SSH instead
        fcon list             print the list and exit
        fcon import           merge the tailnet into the list
        fcon edit             open the CSV in an editor
        fcon path             print where the CSV lives
        fcon help             this text

    The list lives in %LOCALAPPDATA%\fleet-connect\pcs.csv unless $env:FLEET_CONNECT_CSV
    names another file. Its columns are

        Name      short name, what you type after `fcon`
        Host      address or DNS name, put into the command as it stands
        Protocol  Rdp or Ssh - the default for this machine, still overridable
        User      login for RDP, and for SSH when no alias is set
        SshAlias  a Host entry from ~/.ssh/config; when set, SSH uses it and nothing else
        Port      only when it is not the protocol's usual one
        Note      free text, shown in the list

.NOTES
    Exit codes: 0 done, 1 error, 2 nothing chosen or no such machine.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)] [string] $Name,
    [Parameter(Position = 1)] [string] $Via,
    [switch] $NoStatus,
    [switch] $Yes,
    [switch] $Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

enum PcProtocol {
    Rdp
    Ssh
}

class PcModel {
    [string] $Name
    [string] $Address
    [PcProtocol] $Protocol = [PcProtocol]::Rdp
    [string] $User
    [string] $SshAlias
    [string] $Port
    [string] $Note
    # 'up', 'down' or '' - read from tailscale status, never stored in the CSV.
    [string] $Online = ''
}

# ---------------------------------------------------------------- small helpers

function Write-Fail([string]$Text) { Write-Host "  $Text" -ForegroundColor Red }
function Write-Note([string]$Text) { Write-Host "  $Text" -ForegroundColor DarkGray }

# Import-Csv and ConvertFrom-Json both hand back objects whose shape depends on the
# file, and StrictMode turns a missing column into a terminating error. Everything read
# from either goes through here, so an older CSV or a renamed JSON field blanks one cell
# instead of killing the run.
function Get-Field($Row, [string]$Field) {
    if ($null -eq $Row) { return '' }
    $p = $Row.PSObject.Properties[$Field]
    if ($null -eq $p -or $null -eq $p.Value) { return '' }
    return ([string]$p.Value).Trim()
}

function Get-Prop($Object, [string]$Field) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Field]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function ConvertTo-Protocol([string]$Text, [PcProtocol]$Default = [PcProtocol]::Rdp) {
    if ([string]::IsNullOrWhiteSpace($Text)) { return $Default }
    switch -Regex ($Text.Trim()) {
        '^(?i)r(dp)?$' { return [PcProtocol]::Rdp }
        '^(?i)s(sh)?$' { return [PcProtocol]::Ssh }
    }
    throw "Unknown protocol '$Text'. Use rdp or ssh."
}

function Test-Interactive {
    try {
        if ([Console]::IsInputRedirected)  { return $false }
        if ([Console]::IsOutputRedirected) { return $false }
        $null = [Console]::WindowWidth
        $null = [Console]::CursorTop
        return $true
    } catch { return $false }
}

# ---------------------------------------------------------------- the list file

function Get-ListPath {
    if ($env:FLEET_CONNECT_CSV) { return $env:FLEET_CONNECT_CSV }
    return (Join-Path $env:LOCALAPPDATA 'fleet-connect\pcs.csv')
}

function Get-ListDirectory { return (Split-Path -Parent (Get-ListPath)) }

function Read-PcList {
    $path = Get-ListPath
    if (-not (Test-Path -LiteralPath $path)) { return ,@() }

    $rows = @(Import-Csv -LiteralPath $path)
    $list = New-Object 'System.Collections.Generic.List[PcModel]'
    foreach ($row in $rows) {
        $rowName = Get-Field $row 'Name'
        $rowHost = Get-Field $row 'Host'
        if (-not $rowName -and -not $rowHost) { continue }

        $pc = [PcModel]::new()
        $pc.Name     = if ($rowName) { $rowName } else { $rowHost }
        $pc.Address  = $rowHost
        $pc.User     = Get-Field $row 'User'
        $pc.SshAlias = Get-Field $row 'SshAlias'
        $pc.Port     = Get-Field $row 'Port'
        $pc.Note     = Get-Field $row 'Note'
        try {
            $pc.Protocol = ConvertTo-Protocol (Get-Field $row 'Protocol')
        } catch {
            # A typo in one row must not hide the other twenty machines.
            Write-Note "$($pc.Name): $($_.Exception.Message) Falling back to rdp."
            $pc.Protocol = [PcProtocol]::Rdp
        }
        $list.Add($pc)
    }
    # Comma-wrapped, here and in every function below that answers with a list: an empty
    # or single-element array unrolls on the way out otherwise, and the caller is then
    # asking $null or one bare object for its .Count.
    return ,$list.ToArray()
}

function Write-PcList($Pcs) {
    $path = Get-ListPath
    $dir  = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        $null = New-Item -ItemType Directory -Path $dir -Force
    }
    if (Test-Path -LiteralPath $path) {
        Copy-Item -LiteralPath $path -Destination "$path.bak" -Force
    }
    $Pcs |
        Select-Object Name,
                      @{ n = 'Host';     e = { $_.Address } },
                      @{ n = 'Protocol'; e = { $_.Protocol.ToString() } },
                      User, SshAlias, Port, Note |
        Export-Csv -LiteralPath $path -NoTypeInformation -Encoding UTF8
}

function New-EmptyList {
    $path = Get-ListPath
    $dir  = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        $null = New-Item -ItemType Directory -Path $dir -Force
    }
    Set-Content -LiteralPath $path -Value 'Name,Host,Protocol,User,SshAlias,Port,Note' -Encoding UTF8
    return $path
}

# ---------------------------------------------------------------- tailnet status

function Resolve-Tailscale {
    $cmd = Get-Command 'tailscale.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($root in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $root) { continue }
        $candidate = Join-Path $root 'Tailscale\tailscale.exe'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

# One record per tailnet machine: Name, DnsName, Address, Online, Os. Returns $null when
# tailscale is absent or says nothing usable, which every caller treats as "no status to
# show" rather than as a failure.
function Get-TailnetMachines {
    $exe = Resolve-Tailscale
    if (-not $exe) { return $null }

    try {
        $raw = & $exe status --json 2>$null | Out-String
    } catch { return $null }
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }

    try { $json = $raw | ConvertFrom-Json } catch { return $null }

    $nodes = New-Object 'System.Collections.Generic.List[object]'
    $self = Get-Prop $json 'Self'
    if ($self) { $nodes.Add($self) }
    $peers = Get-Prop $json 'Peer'
    if ($peers) {
        foreach ($p in $peers.PSObject.Properties) { $nodes.Add($p.Value) }
    }
    if ($nodes.Count -eq 0) { return $null }

    $result = New-Object 'System.Collections.Generic.List[object]'
    foreach ($node in $nodes) {
        $ips = @(Get-Prop $node 'TailscaleIPs')
        $v4  = $ips | Where-Object { $_ -and $_ -notmatch ':' } | Select-Object -First 1
        if (-not $v4) { continue }

        # The name is read from DNSName and never built from HostName: a hostname that no
        # DNS label can hold - 'moscow_enjoyer', 'TECNO CAMON 20' - is already sanitised
        # there and nowhere else.
        $dns   = ([string](Get-Prop $node 'DNSName')).TrimEnd('.')
        $label = if ($dns) { $dns.Split('.')[0] } else { [string](Get-Prop $node 'HostName') }
        if (-not $label) { continue }

        $result.Add([pscustomobject]@{
            Name    = $label
            DnsName = $dns
            Address = $v4
            Online  = [bool](Get-Prop $node 'Online')
            Os      = [string](Get-Prop $node 'OS')
        })
    }
    if ($result.Count -eq 0) { return $null }
    return ,$result.ToArray()
}

# MagicDNS either resolves on this machine or it does not: it is one suffix served by one
# resolver, and it answers for a machine that is switched off. So this is asked once and
# the answer used for the whole tailnet, rather than paying eight DNS timeouts.
function Test-MagicDnsWorks([string]$SampleName) {
    if ([string]::IsNullOrWhiteSpace($SampleName)) { return $false }
    try {
        $null = [Net.Dns]::GetHostAddresses($SampleName)
        return $true
    } catch { return $false }
}

function Add-OnlineStatus($Pcs) {
    $Pcs = @($Pcs)
    if ($NoStatus -or $Pcs.Count -eq 0) { return ,$Pcs }
    $machines = Get-TailnetMachines
    if (-not $machines) { return ,$Pcs }

    foreach ($pc in $Pcs) {
        $match = $machines | Where-Object {
            $_.Name -eq $pc.Name -or $_.Address -eq $pc.Address -or $_.DnsName -eq $pc.Address
        } | Select-Object -First 1
        if ($match) { $pc.Online = if ($match.Online) { 'up' } else { 'down' } }
    }
    return ,$Pcs
}

# ---------------------------------------------------------------- rendering

function Format-PcLines($Pcs) {
    $Pcs = @($Pcs)
    $wName = 4
    $wHost = 4
    $wUser = 0
    foreach ($pc in $Pcs) {
        if ($pc.Name.Length    -gt $wName) { $wName = $pc.Name.Length }
        if ($pc.Address.Length -gt $wHost) { $wHost = $pc.Address.Length }
        if ($pc.User.Length    -gt $wUser) { $wUser = $pc.User.Length }
    }
    $wName = [Math]::Min($wName, 20)
    $wHost = [Math]::Min($wHost, 38)
    $wUser = [Math]::Min($wUser, 18)

    $anyStatus = @($Pcs | Where-Object { $_.Online }).Count -gt 0
    $lines = New-Object 'System.Collections.Generic.List[string]'
    foreach ($pc in $Pcs) {
        $mark = ''
        if ($anyStatus) {
            $mark = switch ($pc.Online) {
                'up'   { '* ' }
                'down' { '. ' }
                default { '? ' }
            }
        }
        $tail = @($pc.Protocol.ToString().ToLowerInvariant().PadRight(3))
        if ($wUser -gt 0) { $tail += $pc.User.PadRight($wUser) }
        if ($pc.Note) { $tail += $pc.Note }
        $lines.Add(("{0}{1}  {2}  {3}" -f $mark, $pc.Name.PadRight($wName),
                                           $pc.Address.PadRight($wHost), ($tail -join '  ')).TrimEnd())
    }
    return ,$lines.ToArray()
}

# Returns the chosen index, or -1 when the operator backed out.
function Show-Picker {
    param(
        [string]   $Title,
        [string[]] $Items,
        [string[]] $Keys = @(),
        [int]      $Start = 0,
        [switch]   $SelectOnKey
    )
    if ($Items.Count -eq 0) { return -1 }

    $fits = $false
    try { $fits = ($Items.Count + 4) -lt [Console]::WindowHeight } catch { $fits = $false }
    if (-not (Test-Interactive) -or -not $fits) {
        return (Read-Choice -Title $Title -Items $Items)
    }

    $index = [Math]::Max(0, [Math]::Min($Start, $Items.Count - 1))
    $top = $null
    $cursorWas = $true
    Write-Host ''
    Write-Host "  $Title" -ForegroundColor Cyan
    try {
        try { $cursorWas = [Console]::CursorVisible; [Console]::CursorVisible = $false } catch { }
        while ($true) {
            $width = 40
            try { $width = [Math]::Max(20, [Console]::WindowWidth - 1) } catch { }
            if ($null -ne $top) { [Console]::SetCursorPosition(0, $top) }
            for ($i = 0; $i -lt $Items.Count; $i++) {
                $marker = if ($i -eq $index) { '>' } else { ' ' }
                $line = "  $marker $($Items[$i])"
                if ($line.Length -gt $width) { $line = $line.Substring(0, $width) }
                $line = $line.PadRight($width)
                if ($i -eq $index) { Write-Host $line -ForegroundColor Black -BackgroundColor Gray }
                else                { Write-Host $line }
            }
            # Recomputed on every pass, so a redraw that scrolled the window corrects
            # itself instead of drawing the list twice.
            $top = [Console]::CursorTop - $Items.Count

            $key = [Console]::ReadKey($true)
            $handled = $true
            switch ($key.Key) {
                'UpArrow'   { $index = ($index - 1 + $Items.Count) % $Items.Count }
                'DownArrow' { $index = ($index + 1) % $Items.Count }
                'Home'      { $index = 0 }
                'End'       { $index = $Items.Count - 1 }
                'Enter'     { return $index }
                'Escape'    { return -1 }
                default     { $handled = $false }
            }
            if ($handled) { continue }

            $ch = $key.KeyChar
            if ($ch -eq 'q' -or $ch -eq 'Q') { return -1 }
            if ($ch -match '^[0-9]$') {
                $n = [int][string]$ch
                if ($n -ge 1 -and $n -le $Items.Count) {
                    if ($SelectOnKey) { return ($n - 1) }
                    $index = $n - 1
                }
                continue
            }
            if ($ch -match '^[A-Za-z]$' -and $Keys.Count -eq $Items.Count) {
                $hits = @(0..($Items.Count - 1) | Where-Object { $Keys[$_] -like "$ch*" })
                if ($hits.Count -eq 1 -and $SelectOnKey) { return $hits[0] }
                if ($hits.Count -gt 0) {
                    # Repeated presses walk the matches instead of sticking on the first.
                    $next = @($hits | Where-Object { $_ -gt $index })
                    $index = if ($next.Count -gt 0) { $next[0] } else { $hits[0] }
                }
            }
        }
    } finally {
        try { [Console]::CursorVisible = $cursorWas } catch { }
        if ($null -ne $top) {
            try { [Console]::SetCursorPosition(0, $top + $Items.Count) } catch { }
        }
        Write-Host ''
    }
}

function Read-Choice {
    param([string] $Title, [string[]] $Items)
    Write-Host ''
    Write-Host "  $Title" -ForegroundColor Cyan
    for ($i = 0; $i -lt $Items.Count; $i++) {
        Write-Host ("  {0,3}) {1}" -f ($i + 1), $Items[$i])
    }
    if (-not (Test-Interactive)) {
        Write-Host ''
        Write-Note 'No console to read from, so nothing was chosen.'
        return -1
    }
    $answer = Read-Host "  Choose 1-$($Items.Count), Enter to cancel"
    if ([string]::IsNullOrWhiteSpace($answer)) { return -1 }
    $n = 0
    if ([int]::TryParse($answer.Trim(), [ref]$n) -and $n -ge 1 -and $n -le $Items.Count) {
        return ($n - 1)
    }
    Write-Fail "Not one of 1-$($Items.Count)."
    return -1
}

# ---------------------------------------------------------------- connecting

function Get-SafeFileName([string]$Text) {
    $bad = [IO.Path]::GetInvalidFileNameChars()
    $sb = New-Object System.Text.StringBuilder
    foreach ($c in $Text.ToCharArray()) {
        if ($bad -contains $c) { $null = $sb.Append('_') } else { $null = $sb.Append($c) }
    }
    $safe = $sb.ToString()
    if ([string]::IsNullOrWhiteSpace($safe)) { return 'pc' }
    return $safe
}

# Rewrites only the two lines this tool owns and leaves the rest of the .rdp alone, so a
# window size or a drive redirection the operator set inside mstsc survives the next
# connect instead of being reset by it.
function Set-RdpSetting([string]$Path, [string]$Key, [string]$Value) {
    $lines = @()
    if (Test-Path -LiteralPath $Path) { $lines = @(Get-Content -LiteralPath $Path) }
    $written = $false
    $out = New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in $lines) {
        if ($line -like "$Key*") {
            if (-not $written) { $out.Add("$Key$Value"); $written = $true }
        } else {
            $out.Add($line)
        }
    }
    if (-not $written) { $out.Add("$Key$Value") }
    Set-Content -LiteralPath $Path -Value $out.ToArray() -Encoding Unicode
}

function Invoke-Rdp($Pc) {
    $target = $Pc.Address
    if ($Pc.Port) { $target = "$($Pc.Address):$($Pc.Port)" }

    # No user name to carry means no file to carry it in.
    if (-not $Pc.User) {
        Write-Note "mstsc /v:$target"
        Start-Process -FilePath 'mstsc.exe' -ArgumentList "/v:$target"
        return 0
    }

    $dir = Join-Path (Get-ListDirectory) 'rdp'
    if (-not (Test-Path -LiteralPath $dir)) { $null = New-Item -ItemType Directory -Path $dir -Force }
    $file = Join-Path $dir ((Get-SafeFileName $Pc.Name) + '.rdp')
    Set-RdpSetting $file 'full address:s:' $target
    Set-RdpSetting $file 'username:s:'     $Pc.User
    Write-Note "mstsc $file   ($($Pc.User) at $target)"
    Start-Process -FilePath 'mstsc.exe' -ArgumentList """$file"""
    return 0
}

function Invoke-Ssh($Pc) {
    $ssh = Get-Command 'ssh.exe' -ErrorAction SilentlyContinue
    if (-not $ssh) {
        Write-Fail 'No ssh.exe on PATH. Install the OpenSSH client:'
        Write-Fail '  Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0'
        return 1
    }

    $sshArgs = @()
    if ($Pc.SshAlias) {
        # An alias is used whole: its ~/.ssh/config entry already carries the user, the
        # port and the key, and repeating any of them here could only contradict it.
        $sshArgs += $Pc.SshAlias
    } else {
        $sshArgs += $(if ($Pc.User) { "$($Pc.User)@$($Pc.Address)" } else { $Pc.Address })
        if ($Pc.Port) { $sshArgs += @('-p', $Pc.Port) }
    }
    Write-Note ('ssh ' + ($sshArgs -join ' '))
    & $ssh.Source @sshArgs
    return $LASTEXITCODE
}

function Connect-Pc($Pc, [PcProtocol]$Protocol) {
    if (-not $Pc.Address -and -not $Pc.SshAlias) {
        Write-Fail "$($Pc.Name) has neither a host nor an ssh alias."
        return 1
    }
    if ($Protocol -eq [PcProtocol]::Ssh) { return (Invoke-Ssh $Pc) }
    return (Invoke-Rdp $Pc)
}

# ---------------------------------------------------------------- commands

function Invoke-List {
    # Assigned straight across, never wrapped in @(): these functions already answer with
    # one array object, and @() around the call would collect that array as a single
    # element instead of enumerating it.
    $pcs = Add-OnlineStatus (Read-PcList)
    if ($pcs.Count -eq 0) {
        Write-Note 'No machines yet. Run "fcon import" or "fcon edit".'
        Write-Note (Get-ListPath)
        return 2
    }
    Write-Host ''
    foreach ($line in (Format-PcLines $pcs)) { Write-Host "  $line" }
    Write-Host ''
    return 0
}

function Invoke-Edit {
    $path = Get-ListPath
    if (-not (Test-Path -LiteralPath $path)) { $path = New-EmptyList }
    $editor = if ($env:FLEET_CONNECT_EDITOR) { $env:FLEET_CONNECT_EDITOR } else { 'notepad.exe' }
    Start-Process -FilePath $editor -ArgumentList """$path"""
    return 0
}

function Invoke-Import {
    $machines = Get-TailnetMachines
    if (-not $machines) {
        Write-Fail 'Could not read "tailscale status --json". Is Tailscale installed and up?'
        return 1
    }

    # Phones and tablets are in the tailnet too, and neither answers RDP or SSH.
    $mobile   = '^(?i)(ios|android|tvos)$'
    $skipped  = @($machines | Where-Object { $_.Os -match $mobile })
    $machines = @($machines | Where-Object { $_.Os -notmatch $mobile })
    if ($machines.Count -eq 0) {
        Write-Fail 'The tailnet has no machine this tool could connect to.'
        return 1
    }

    $useNames = Test-MagicDnsWorks $machines[0].DnsName

    $existing = Read-PcList
    $added    = New-Object 'System.Collections.Generic.List[string]'
    $changed  = New-Object 'System.Collections.Generic.List[string]'

    foreach ($m in $machines) {
        $address = if ($useNames -and $m.DnsName) { $m.DnsName } else { $m.Address }

        # Matched on the name and on either form of the address, so a machine already in
        # the list under an operator's own name is updated rather than added twice.
        $match = $existing | Where-Object {
            $_.Name -eq $m.Name -or $_.Address -eq $m.Address -or $_.Address -eq $m.DnsName
        } | Select-Object -First 1

        if ($match) {
            if ($match.Address -ne $address) {
                $changed.Add(("~ {0}: {1} -> {2}" -f $match.Name, $match.Address, $address))
                $match.Address = $address
            }
            continue
        }

        $pc = [PcModel]::new()
        $pc.Name    = $m.Name
        $pc.Address = $address
        # A Windows box is reached over RDP and a Linux one over SSH far more often than
        # the other way round; either is still one keystroke away in the menu.
        $pc.Protocol = if ($m.Os -match '^(?i)linux') { [PcProtocol]::Ssh } else { [PcProtocol]::Rdp }
        $existing += $pc
        $added.Add(("+ {0}  {1}  {2}" -f $pc.Name, $pc.Address, $pc.Protocol.ToString().ToLowerInvariant()))
    }

    Write-Host ''
    if (-not $useNames) {
        Write-Note 'MagicDNS does not resolve here, so 100.x addresses are stored instead of names.'
    }
    foreach ($s in $skipped)    { Write-Note ("- {0} ({1}), skipped" -f $s.Name, $s.Os) }
    foreach ($line in $added)   { Write-Host "  $line" -ForegroundColor Green }
    foreach ($line in $changed) { Write-Host "  $line" -ForegroundColor Yellow }

    if ($added.Count -eq 0 -and $changed.Count -eq 0) {
        Write-Note 'Nothing to change.'
        Write-Host ''
        return 0
    }

    Write-Host ''
    if (-not $Yes) {
        if (-not (Test-Interactive)) {
            Write-Note 'Nothing written. Run with -Yes to apply this without being asked.'
            return 2
        }
        $answer = Read-Host "  Write this to $(Get-ListPath)? [y/N]"
        if ($answer -notmatch '^(?i)y') {
            Write-Note 'Left alone.'
            return 2
        }
    }
    Write-PcList $existing
    Write-Note "Written. The previous list is kept as $(Get-ListPath).bak"
    Write-Host ''
    return 0
}

function Invoke-Help {
    Get-Help -Detailed $PSCommandPath | Out-String | Write-Host
    return 0
}

function Invoke-Connect([string]$Wanted, [string]$WantedVia) {
    $pcs = Read-PcList
    if ($pcs.Count -eq 0) {
        Write-Fail 'There are no machines in the list yet.'
        Write-Note 'Run "fcon import" to read them out of the tailnet, or "fcon edit" to type them in.'
        Write-Note (Get-ListPath)
        return 2
    }

    $pc = $null
    if ($Wanted) {
        # Exact name first, then prefix, then anything containing it. A machine called
        # 'home' is never shadowed by 'home-backup' just because it was typed in full.
        $hits = @($pcs | Where-Object { $_.Name -eq $Wanted })
        if ($hits.Count -eq 0) { $hits = @($pcs | Where-Object { $_.Name -like "$Wanted*" }) }
        if ($hits.Count -eq 0) {
            $hits = @($pcs | Where-Object { $_.Name -like "*$Wanted*" -or $_.Address -like "*$Wanted*" })
        }

        if ($hits.Count -eq 0) {
            Write-Fail "No machine matches '$Wanted'."
            $null = Invoke-List
            return 2
        }
        if ($hits.Count -eq 1) {
            $pc = $hits[0]
        } else {
            $i = Show-Picker -Title "$($hits.Count) machines match '$Wanted'" `
                             -Items (Format-PcLines $hits) `
                             -Keys @($hits | ForEach-Object { $_.Name })
            if ($i -lt 0) { return 2 }
            $pc = $hits[$i]
        }
    } else {
        $pcs = Add-OnlineStatus $pcs
        $i = Show-Picker -Title 'Machines   (arrows move, letters jump, Enter connects, Esc quits)' `
                         -Items (Format-PcLines $pcs) `
                         -Keys @($pcs | ForEach-Object { $_.Name })
        if ($i -lt 0) { return 2 }
        $pc = $pcs[$i]
    }

    $protocol = $pc.Protocol
    if ($WantedVia) {
        $protocol = ConvertTo-Protocol $WantedVia $pc.Protocol
    } elseif (-not $Wanted) {
        # Only the menu asks. "fcon gpu" is the shortcut, and stopping it to ask would
        # undo the whole point of having one.
        $start = if ($pc.Protocol -eq [PcProtocol]::Ssh) { 1 } else { 0 }
        $i = Show-Picker -Title "$($pc.Name) - how?" `
                         -Items @('rdp   Remote Desktop', 'ssh   Secure Shell') `
                         -Keys @('rdp', 'ssh') -Start $start -SelectOnKey
        if ($i -lt 0) { return 2 }
        $protocol = if ($i -eq 1) { [PcProtocol]::Ssh } else { [PcProtocol]::Rdp }
    }

    return (Connect-Pc $pc $protocol)
}

# ---------------------------------------------------------------- entry point

try {
    if ($Help) { exit (Invoke-Help) }

    switch -Regex ($Name) {
        '^(?i)(help|-h|/\?)$' { exit (Invoke-Help) }
        '^(?i)list$'          { exit (Invoke-List) }
        '^(?i)import$'        { exit (Invoke-Import) }
        '^(?i)edit$'          { exit (Invoke-Edit) }
        '^(?i)path$'          { Write-Host (Get-ListPath); exit 0 }
    }
    exit (Invoke-Connect $Name $Via)
} catch {
    Write-Host ''
    Write-Fail $_.Exception.Message
    exit 1
}
