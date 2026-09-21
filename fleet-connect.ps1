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
        fcon add              add a new machine to the list
        fcon add gpu host     add with positional args (see help)
        fcon sync             push pcs.csv to remote machines
        fcon sync mks68 xeon  push to selected machines
        fcon import           merge the tailnet into the list
        fcon setup            enable RDP/SSH on this machine (requires admin)
        fcon update           update fcon from GitHub (progress bar + status)
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
    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $Extra,
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

function Invoke-Add {
    param([string[]] $AddArgs)

    # Help for the subcommand: fcon add help / fcon add -h
    if ($AddArgs.Count -eq 1 -and $AddArgs[0] -match '^(?i)(help|-h|/\?|--help)$') {
        Write-Host ''
        Write-Host '  fcon add - add a new machine to the list' -ForegroundColor Cyan
        Write-Host ''
        Write-Host '    fcon add                                   interactive prompts'
        Write-Host '    fcon add <name> <host> [rdp|ssh]            positional'
        Write-Host '    fcon add <name> <host> --user <user> --port <port> --note <text> --alias <sshAlias>'
        Write-Host ''
        Write-Host '  Flags (order does not matter):'
        Write-Host '    --user, -u       login for RDP and SSH (when no alias)'
        Write-Host '    --alias          Host entry from ~/.ssh/config (when set, SSH uses only it)'
        Write-Host '    --port           non-default port'
        Write-Host '    --note           free text shown in the list'
        Write-Host '    --protocol, -p   rdp or ssh (also as 3rd positional arg)'
        Write-Host ''
        Write-Host '  Examples:'
        Write-Host '    fcon add srv1 10.0.0.5 rdp --user admin --note "office"'
        Write-Host '    fcon add dev dev-box.ts.net ssh --alias dev --port 2222'
        Write-Host ''
        return 0
    }

    $addName = $null; $addHost = $null; $addProtoText = $null
    $addUser = $null; $addAlias = $null; $addPort = $null; $addNote = $null
    $positional = New-Object 'System.Collections.Generic.List[string]'

    for ($i = 0; $i -lt $AddArgs.Count; $i++) {
        $a = $AddArgs[$i]
        switch -Regex ($a) {
            '^(?i)--?(user|u)$' {
                if ($i + 1 -ge $AddArgs.Count) { Write-Fail "Missing value for $a"; return 1 }
                $addUser = $AddArgs[++$i]; continue
            }
            '^(?i)--?(alias|ssh-?alias)$' {
                if ($i + 1 -ge $AddArgs.Count) { Write-Fail "Missing value for $a"; return 1 }
                $addAlias = $AddArgs[++$i]; continue
            }
            '^(?i)--?port$' {
                if ($i + 1 -ge $AddArgs.Count) { Write-Fail "Missing value for $a"; return 1 }
                $addPort = $AddArgs[++$i]; continue
            }
            '^(?i)--?note$' {
                if ($i + 1 -ge $AddArgs.Count) { Write-Fail "Missing value for $a"; return 1 }
                $addNote = $AddArgs[++$i]; continue
            }
            '^(?i)--?(protocol|proto|p)$' {
                if ($i + 1 -ge $AddArgs.Count) { Write-Fail "Missing value for $a"; return 1 }
                $addProtoText = $AddArgs[++$i]; continue
            }
            default { $positional.Add($a) }
        }
    }

    # Positional mapping: name, host, [protocol|user] - protocol is detected by value.
    if ($positional.Count -ge 1 -and -not $addName) { $addName = $positional[0] }
    if ($positional.Count -ge 2 -and -not $addHost) { $addHost = $positional[1] }
    if ($positional.Count -ge 3) {
        $third = $positional[2]
        if (-not $addProtoText -and $third -match '^(?i)r(dp)?|s(sh)?$') { $addProtoText = $third }
        elseif (-not $addUser) { $addUser = $third }
    }
    if ($positional.Count -ge 4 -and -not $addUser) { $addUser = $positional[3] }
    # Extra positionals beyond 4 are treated as note if not set
    if ($positional.Count -ge 5 -and -not $addNote) { $addNote = ($positional.GetRange(4, $positional.Count - 4) -join ' ') }

    $interactive = Test-Interactive

    if (-not $addName) {
        if (-not $interactive) { Write-Fail 'Name is required. Usage: fcon add <name> <host> [rdp|ssh]'; return 1 }
        $addName = (Read-Host '  Name (short, e.g. gpu)').Trim()
        if (-not $addName) { Write-Note 'Cancelled.'; return 2 }
    }
    if (-not $addHost) {
        if (-not $interactive) { Write-Fail 'Host is required. Usage: fcon add <name> <host>'; return 1 }
        $addHost = (Read-Host '  Host (address or DNS)').Trim()
        if (-not $addHost) { Write-Note 'Cancelled.'; return 2 }
    }

    if (-not $addProtoText) {
        if ($interactive -and $positional.Count -eq 0 -and $AddArgs.Count -eq 0) {
            $ans = (Read-Host '  Protocol [rdp/ssh, default rdp]').Trim()
            if ($ans) { $addProtoText = $ans }
        }
    }

    try { $proto = ConvertTo-Protocol $addProtoText } catch { Write-Fail $_.Exception.Message; return 1 }

    if (-not $addUser -and $interactive -and $AddArgs.Count -eq 0) {
        $addUser = (Read-Host '  User (empty = none)').Trim()
    }
    if (-not $addAlias -and $interactive -and $AddArgs.Count -eq 0) {
        $addAlias = (Read-Host '  SshAlias (empty = none, uses Host from ~/.ssh/config)').Trim()
    }
    if (-not $addPort -and $interactive -and $AddArgs.Count -eq 0) {
        $addPort = (Read-Host '  Port (empty = default)').Trim()
    }
    if (-not $addNote -and $interactive -and $AddArgs.Count -eq 0) {
        $addNote = (Read-Host '  Note (empty = none)').Trim()
    }

    $existing = Read-PcList
    $dup = @($existing | Where-Object { $_.Name -eq $addName })
    if ($dup.Count -gt 0) {
        Write-Fail "A machine named '$addName' already exists."
        return 1
    }
    $dupHost = @($existing | Where-Object { $_.Address -eq $addHost })
    if ($dupHost.Count -gt 0) {
        Write-Note "Note: another machine '$($dupHost[0].Name)' already uses host '$addHost'."
    }

    $pc = [PcModel]::new()
    $pc.Name     = $addName
    $pc.Address  = $addHost
    $pc.Protocol = $proto
    $pc.User     = if ($addUser) { $addUser } else { '' }
    $pc.SshAlias = if ($addAlias) { $addAlias } else { '' }
    $pc.Port     = if ($addPort) { $addPort } else { '' }
    $pc.Note     = if ($addNote) { $addNote } else { '' }

    $all = @($existing) + $pc
    Write-PcList $all

    Write-Host ''
    Write-Host "  Added $addName  $addHost  $($proto.ToString().ToLowerInvariant())" -ForegroundColor Green
    if ($pc.User) { Write-Note "  user: $($pc.User)" }
    if ($pc.SshAlias) { Write-Note "  alias: $($pc.SshAlias)" }
    if ($pc.Port) { Write-Note "  port: $($pc.Port)" }
    Write-Note "  list: $(Get-ListPath)"
    Write-Host ''
    return 0
}

function Invoke-Sync {
    param([string[]] $SyncArgs)

    if ($SyncArgs.Count -eq 1 -and $SyncArgs[0] -match '^(?i)(help|-h|/\?|--help)$') {
        Write-Host ''
        Write-Host '  fcon sync - push pcs.csv to remote machines' -ForegroundColor Cyan
        Write-Host ''
        Write-Host '    fcon sync                          push to all known machines (except self)'
        Write-Host '    fcon sync mks68 xeon home-pc       push to listed names'
        Write-Host '    fcon sync --dry-run                show what would be pushed'
        Write-Host ''
        Write-Host '  Uses ssh/scp (OpenSSH). For each target:'
        Write-Host '    - creates %LOCALAPPDATA%\fleet-connect if missing'
        Write-Host '    - tries scp, falls back to "more" pipe for hosts without sftp'
        Write-Host '  SshAlias from the list is used when present, otherwise User@Host.'
        Write-Host ''
        return 0
    }

    $localPath = Get-ListPath
    if (-not (Test-Path -LiteralPath $localPath)) { Write-Fail "No local list at $localPath"; return 1 }
    $allPcs = Read-PcList
    if ($allPcs.Count -eq 0) { Write-Fail "List is empty"; return 1 }

    $dryRun = $false
    $wants = New-Object 'System.Collections.Generic.List[string]'
    foreach ($a in $SyncArgs) {
        if ($a -match '^(?i)--dry-run$') { $dryRun = $true; continue }
        $wants.Add($a)
    }

    $targets = @()
    if ($wants.Count -eq 0) {
        $targets = $allPcs
    } else {
        foreach ($want in $wants) {
            $hits = @($allPcs | Where-Object { $_.Name -eq $want })
            if ($hits.Count -eq 0) { $hits = @($allPcs | Where-Object { $_.Name -like "$want*" }) }
            if ($hits.Count -eq 0) { $hits = @($allPcs | Where-Object { $_.Address -eq $want }) }
            if ($hits.Count -eq 0) { Write-Fail "No machine matches '$want'"; return 1 }
            if ($hits.Count -gt 1) { Write-Fail "'$want' matches multiple: $($hits.Name -join ', ')"; return 1 }
            $targets += $hits[0]
        }
    }

    # Skip self: detect via Tailscale Self DNSName/Address
    try {
        $machines = Get-TailnetMachines
        $selfDns = $null; $selfAddr = $null; $selfName = $null
        if ($machines) {
            # Re-read Self directly for accuracy
            $exe = Resolve-Tailscale
            if ($exe) {
                $raw = & $exe status --json 2>$null | Out-String
                $j = $raw | ConvertFrom-Json
                $self = Get-Prop $j 'Self'
                if ($self) {
                    $selfDns = ([string](Get-Prop $self 'DNSName')).TrimEnd('.')
                    $ips = @(Get-Prop $self 'TailscaleIPs')
                    $selfAddr = $ips | Where-Object { $_ -and $_ -notmatch ':' } | Select-Object -First 1
                    $selfName = [string](Get-Prop $self 'HostName')
                }
            }
        }
        if ($selfDns -or $selfAddr) {
            $filtered = @()
            foreach ($t in $targets) {
                $isSelf = $false
                if ($selfDns -and ($t.Address -eq $selfDns -or $t.Address -eq "$selfDns.")) { $isSelf = $true }
                if ($selfAddr -and $t.Address -eq $selfAddr) { $isSelf = $true }
                if ($t.Name -eq 'RIO' -and $selfName -eq 'RE-7LQD67AHCM0R') { $isSelf = $true }
                if ($isSelf) { Write-Note "Skipping self $($t.Name)"; continue }
                $filtered += $t
            }
            $targets = $filtered
        }
    } catch { }

    if ($targets.Count -eq 0) { Write-Note "Nothing to sync."; return 0 }

    $ssh = Get-Command 'ssh.exe' -ErrorAction SilentlyContinue
    $scp = Get-Command 'scp.exe' -ErrorAction SilentlyContinue
    if (-not $ssh) { Write-Fail "No ssh.exe on PATH."; return 1 }

    Write-Host ''
    Write-Host "  Local: $localPath ($((Get-Content -LiteralPath $localPath | Measure-Object -Line).Lines) lines)" -ForegroundColor DarkGray
    if ($dryRun) { Write-Host "  Dry run - no files will be written" -ForegroundColor Yellow }
    Write-Host ''

    $failed = 0; $okCount = 0
    foreach ($pc in $targets) {
        $sshTarget = if ($pc.SshAlias) { $pc.SshAlias } elseif ($pc.User) { "$($pc.User)@$($pc.Address)" } else { $pc.Address }
        $portArgsSsh = @(); $portArgsScp = @()
        if ($pc.Port -and -not $pc.SshAlias) {
            $portArgsSsh = @('-p', $pc.Port)
            $portArgsScp = @('-P', $pc.Port)
        }

        Write-Host "  -> $($pc.Name) ($sshTarget) ..." -NoNewline

        if ($dryRun) { Write-Host " dry-run" -ForegroundColor DarkGray; $okCount++; continue }

        # 1) ensure remote dir exists
        try { & $ssh.Source @portArgsSsh $sshTarget 'mkdir "%LOCALAPPDATA%\fleet-connect" 2>nul & echo ok' 2>$null | Out-Null } catch { }

        $pushed = $false

        # 2) push via 'more' pipe - works on all Windows OpenSSH hosts, even when sftp is disabled
        #    (home-pc has sftp subsystem off, so scp fails; 'more' is always available)
        try {
            Get-Content -LiteralPath $localPath -Raw -Encoding UTF8 | & $ssh.Source @portArgsSsh $sshTarget 'more > "%LOCALAPPDATA%\fleet-connect\pcs.csv"' 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { $pushed = $true }
        } catch { }

        # 3) fallback to scp if pipe failed and scp is available
        if (-not $pushed -and $scp) {
            $null = & $scp.Source -o StrictHostKeyChecking=accept-new @portArgsScp $localPath "${sshTarget}:C:/Users/local/AppData/Local/fleet-connect/pcs.csv" 2>$null
            if ($LASTEXITCODE -eq 0) { $pushed = $true }
        }

        if ($pushed) {
            # verify by reading first line
            $verifyOk = $false
            try {
                $head = & $ssh.Source @portArgsSsh $sshTarget 'type "%LOCALAPPDATA%\fleet-connect\pcs.csv"' 2>$null | Select-Object -First 1
                if ($head -match 'Name.*Host') { $verifyOk = $true }
            } catch { }
            if ($verifyOk) { Write-Host " ok" -ForegroundColor Green; $okCount++ }
            else { Write-Host " ok (unverified)" -ForegroundColor Yellow; $okCount++ }
        } else {
            Write-Host " failed" -ForegroundColor Red
            $failed++
        }
    }

    Write-Host ''
    if ($failed -gt 0) { Write-Fail "$failed of $($targets.Count) failed."; return 1 }
    Write-Host "  Synced $okCount host(s)." -ForegroundColor Green
    Write-Host ''
    return 0
}

function Test-IsAdmin {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        $pr = New-Object Security.Principal.WindowsPrincipal($id)
        return $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

function Get-IsHomeEdition {
    try {
        $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
        $caption = [string]$os.Caption
        if ($caption -match '(?i)home') { return $true }
        return $false
    } catch {
        try {
            $caption2 = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop).ProductName
            if ([string]$caption2 -match '(?i)home') { return $true }
        } catch { }
        return $false
    }
}

function Enable-LocalRdp {
    if (Get-IsHomeEdition) {
        Write-Fail 'RDP host is not available on Windows Home. Upgrade to Pro/Enterprise or use SSH only.'
        return 1
    }
    try {
        Set-ItemProperty -Path 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name 'fDenyTSConnections' -Value 0 -ErrorAction Stop
    } catch {
        Write-Fail "Failed to enable RDP (registry): $($_.Exception.Message)"
        return 1
    }
    try {
        Enable-NetFirewallRule -DisplayGroup 'Remote Desktop' -ErrorAction Stop | Out-Null
    } catch {
        Write-Fail "Failed to enable firewall rule for Remote Desktop: $($_.Exception.Message)"
        return 1
    }
    try {
        $svc = Get-Service -Name TermService -ErrorAction Stop
        if ($svc.Status -ne 'Running') {
            try { Start-Service -Name TermService -ErrorAction Stop } catch { }
        }
    } catch { }
    Write-Host '  RDP enabled (port 3389, firewall Remote Desktop).' -ForegroundColor Green
    return 0
}

function Enable-LocalSsh {
    $cap = $null
    try { $cap = Get-WindowsCapability -Online -ErrorAction Stop | Where-Object { $_.Name -like 'OpenSSH.Server*' } | Select-Object -First 1 } catch { }
    if ($cap) {
        if ($cap.State -ne 'Installed') {
            Write-Note 'Installing OpenSSH Server (Add-WindowsCapability)...'
            try {
                $null = Add-WindowsCapability -Online -Name $cap.Name -ErrorAction Stop
            } catch {
                Write-Fail "Failed to install OpenSSH Server: $($_.Exception.Message)"
                return 1
            }
        }
    } else {
        # Fallback for older systems without Get-WindowsCapability
        try { $null = Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' -ErrorAction Stop } catch {
            Write-Fail "OpenSSH Server capability not found: $($_.Exception.Message)"
            return 1
        }
    }
    try {
        Set-Service -Name sshd -StartupType Automatic -ErrorAction Stop
        $svc = Get-Service -Name sshd -ErrorAction Stop
        if ($svc.Status -ne 'Running') { Start-Service -Name sshd -ErrorAction Stop }
    } catch {
        Write-Fail "Failed to start sshd: $($_.Exception.Message)"
        return 1
    }
    try {
        $rule = Get-NetFirewallRule -Name sshd -ErrorAction SilentlyContinue
        if (-not $rule) {
            $null = New-NetFirewallRule -Name sshd -DisplayName 'OpenSSH Server (sshd)' -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 -ErrorAction Stop
        } else {
            try { Enable-NetFirewallRule -Name sshd -ErrorAction Stop | Out-Null } catch { }
        }
    } catch {
        Write-Fail "Failed to configure firewall for sshd (port 22): $($_.Exception.Message)"
        return 1
    }
    Write-Host '  SSH enabled (port 22, sshd Automatic, firewall sshd).' -ForegroundColor Green
    return 0
}

function Invoke-Setup {
    param([string[]] $SetupArgs)

    if ($SetupArgs.Count -eq 1 -and $SetupArgs[0] -match '^(?i)(help|-h|/\?|--help)$') {
        Write-Host ''
        Write-Host '  fcon setup - enable RDP and/or SSH on this machine' -ForegroundColor Cyan
        Write-Host ''
        Write-Host '    fcon setup                 interactive: ask for RDP and SSH separately'
        Write-Host '    fcon setup --rdp           enable RDP only'
        Write-Host '    fcon setup --ssh           enable SSH only'
        Write-Host '    fcon setup --rdp --ssh     enable both'
        Write-Host '    fcon setup --yes           assume yes to asked components (use with --rdp/--ssh)'
        Write-Host ''
        Write-Host '  Requires Administrator. Strict mode:'
        Write-Host '    - without admin -> error, nothing changed'
        Write-Host '    - RDP on Windows Home -> error (use SSH only)'
        Write-Host '    - firewall rules are configured automatically'
        Write-Host '    - reboot is not required (RDP/SSH start immediately)'
        Write-Host ''
        return 0
    }

    if (-not (Test-IsAdmin)) {
        Write-Fail 'Administrator rights required. Run PowerShell as Administrator and try again.'
        return 1
    }

    $wantRdp = $null; $wantSsh = $null
    $assumeYes = $false
    $unknown = @()
    foreach ($a in $SetupArgs) {
        switch -Regex ($a) {
            '^(?i)--?rdp$'  { $wantRdp = $true; continue }
            '^(?i)--?ssh$'  { $wantSsh = $true; continue }
            '^(?i)--?yes$'  { $assumeYes = $true; continue }
            '^(?i)--?all$'  { $wantRdp = $true; $wantSsh = $true; continue }
            default { $unknown += $a }
        }
    }
    if ($unknown.Count -gt 0) {
        Write-Fail "Unknown option(s): $($unknown -join ' '). Use fcon setup --help"
        return 1
    }

    $interactive = Test-Interactive
    # No flags -> ask separately for each component
    if ($null -eq $wantRdp -and $null -eq $wantSsh) {
        if (-not $interactive -and -not $assumeYes) {
            Write-Fail 'No component selected. Use --rdp and/or --ssh, or run interactively.'
            return 1
        }
        if ($assumeYes) {
            $wantRdp = $true; $wantSsh = $true
        } else {
            $ansRdp = Read-Host '  Enable RDP? [y/N]'
            $wantRdp = ($ansRdp -match '^(?i)y')
            $ansSsh = Read-Host '  Enable SSH? [y/N]'
            $wantSsh = ($ansSsh -match '^(?i)y')
        }
    } elseif ($assumeYes) {
        # --yes without explicit component is handled above; with explicit component just proceed
    } else {
        # One flag given interactively -> ask for the other separately
        if ($null -eq $wantRdp -and $interactive) {
            $ansRdp = Read-Host '  Enable RDP? [y/N]'
            $wantRdp = ($ansRdp -match '^(?i)y')
        } elseif ($null -eq $wantRdp) { $wantRdp = $false }
        if ($null -eq $wantSsh -and $interactive) {
            $ansSsh = Read-Host '  Enable SSH? [y/N]'
            $wantSsh = ($ansSsh -match '^(?i)y')
        } elseif ($null -eq $wantSsh) { $wantSsh = $false }
    }

    if (-not $wantRdp -and -not $wantSsh) {
        Write-Note 'Nothing selected. No changes made.'
        return 2
    }

    $code = 0
    if ($wantRdp) {
        Write-Host ''
        Write-Host '  Enabling RDP...' -ForegroundColor Cyan
        $r = Enable-LocalRdp
        if ($r -ne 0) { $code = 1 }
    }
    if ($wantSsh) {
        Write-Host ''
        Write-Host '  Enabling SSH...' -ForegroundColor Cyan
        $r = Enable-LocalSsh
        if ($r -ne 0) { $code = 1 }
    }

    if ($code -eq 0) {
        Write-Host ''
        Write-Host '  Setup complete.' -ForegroundColor Green
        Write-Note 'Verify: Get-Service TermService,sshd | Select Name,Status; netstat -an | findstr "3389.*LISTENING 22.*LISTENING"'
    } else {
        Write-Host ''
        Write-Fail 'Setup finished with errors (see above). Strict mode: fix the error and run again.'
    }
    return $code
}

function Invoke-Update {
    param([string[]] $UpdateArgs)

    if ($UpdateArgs.Count -eq 1 -and $UpdateArgs[0] -match '^(?i)(help|-h|/\?|--help)$') {
        Write-Host ''
        Write-Host '  fcon update - update fcon from GitHub' -ForegroundColor Cyan
        Write-Host ''
        Write-Host '    fcon update                update to latest master'
        Write-Host '    fcon update --check        only check if update is available'
        Write-Host '    fcon update --help         this text'
        Write-Host ''
        Write-Host '  Mirrors install.ps1: uses $env:FLEET_CONNECT_REPO / _REF / _DIR if set.'
        Write-Host '  Shows progress bar and status text like the panel.'
        Write-Host ''
        return 0
    }

    $checkOnly = $false
    $unknown = @()
    foreach ($a in $UpdateArgs) {
        switch -Regex ($a) {
            '^(?i)--?check$' { $checkOnly = $true; continue }
            default { $unknown += $a }
        }
    }
    if ($unknown.Count -gt 0) {
        Write-Fail "Unknown option(s): $($unknown -join ' '). Use fcon update --help"
        return 1
    }

    $repo   = if ($env:FLEET_CONNECT_REPO) { $env:FLEET_CONNECT_REPO } else { 'XYphrodite/fleet-connect' }
    $ref    = if ($env:FLEET_CONNECT_REF)  { $env:FLEET_CONNECT_REF }  else { 'master' }
    $target = if ($env:FLEET_CONNECT_DIR)  { $env:FLEET_CONNECT_DIR }  else { Join-Path $env:LOCALAPPDATA 'Programs\fleet-connect' }
    $source = "https://raw.githubusercontent.com/$repo/$ref/fleet-connect.ps1"

    # Ensure invariant progress rendering even on hosts with $ProgressPreference=SilentlyContinue
    $prevPref = $ProgressPreference
    $ProgressPreference = 'Continue'
    try {
        Write-Progress -Activity 'fcon update' -Status 'Проверка обновлений...' -PercentComplete 5
        Write-Host ''
        Write-Host '==> Проверка обновлений...' -ForegroundColor Cyan
        Write-Host "    $source" -ForegroundColor DarkGray
        Start-Sleep -Milliseconds 150

        if ($checkOnly) {
            Write-Progress -Activity 'fcon update' -Status 'Проверка доступности...' -PercentComplete 40
            try {
                [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
                $head = Invoke-WebRequest -Uri $source -UseBasicParsing -TimeoutSec 15 -Method Head -ErrorAction Stop
                Write-Progress -Activity 'fcon update' -Completed
                Write-Host '  Доступен: ' -NoNewline -ForegroundColor Green
                Write-Host $source -ForegroundColor DarkGray
                Write-Host "  Репозиторий: $repo  ветка: $ref" -ForegroundColor DarkGray
                return 0
            } catch {
                Write-Progress -Activity 'fcon update' -Completed
                Write-Fail "Не удалось проверить обновление: $($_.Exception.Message)"
                return 1
            }
        }

        Write-Progress -Activity 'fcon update' -Status 'Загрузка fleet-connect.ps1...' -PercentComplete 25
        Write-Host '==> Загрузка fleet-connect.ps1...' -ForegroundColor Cyan
        Write-Host "    $source" -ForegroundColor DarkGray
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $resp = $null
        try {
            $resp = Invoke-WebRequest -Uri $source -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop
        } catch {
            Write-Progress -Activity 'fcon update' -Completed
            throw "Could not download fleet-connect.ps1 from $repo ($ref). ($($_.Exception.Message))"
        }

        Write-Progress -Activity 'fcon update' -Status 'Проверка файла...' -PercentComplete 55
        Write-Host '==> Проверка файла...' -ForegroundColor Cyan
        $body = [string]$resp.Content
        if ($body -notmatch '(?m)^\s*#Requires -Version') {
            Write-Progress -Activity 'fcon update' -Completed
            throw "What came back from $source is not the script. Is the repository published and does it have a $ref branch?"
        }
        Start-Sleep -Milliseconds 200
        Write-Host '    файл корректен' -ForegroundColor DarkGray

        Write-Progress -Activity 'fcon update' -Status "Установка в $target..." -PercentComplete 80
        Write-Host "==> Установка в $target..." -ForegroundColor Cyan
        $null = New-Item -ItemType Directory -Force -Path $target

        $scriptPath = Join-Path $target 'fleet-connect.ps1'
        [IO.File]::WriteAllText($scriptPath, $body, (New-Object Text.UTF8Encoding($false)))
        Write-Host "    fleet-connect.ps1 обновлен" -ForegroundColor DarkGray

        $stale = Join-Path $target 'fcon.ps1'
        if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force }

        $shim = @(
            '@echo off',
            'setlocal',
            'set "PS=powershell"',
            'where pwsh >nul 2>nul',
            'if not errorlevel 1 set "PS=pwsh"',
            '"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0fleet-connect.ps1" %*',
            'exit /b %errorlevel%'
        )
        Set-Content -LiteralPath (Join-Path $target 'fcon.cmd') -Value $shim -Encoding ASCII
        Write-Host '    fcon.cmd обновлен' -ForegroundColor DarkGray

        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (($userPath -split ';') -notcontains $target) {
            Write-Host '==> Добавление в PATH...' -ForegroundColor Cyan
            $updated = if ([string]::IsNullOrEmpty($userPath)) { $target } else { "$userPath;$target" }
            [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
        }
        if (($env:Path -split ';') -notcontains $target) { $env:Path = "$env:Path;$target" }

        Write-Progress -Activity 'fcon update' -Status 'Готово!' -PercentComplete 100
        Start-Sleep -Milliseconds 300
        Write-Progress -Activity 'fcon update' -Completed

        Write-Host ''
        Write-Host "  fleet-connect обновлен: $target" -ForegroundColor Green
        Write-Host "  версия: $repo@$ref" -ForegroundColor DarkGray
        $listPath = if ($env:FLEET_CONNECT_CSV) { $env:FLEET_CONNECT_CSV } else { Join-Path $env:LOCALAPPDATA 'fleet-connect\pcs.csv' }
        Write-Host "  список машин: $listPath" -ForegroundColor DarkGray
        Write-Host ''
        Write-Host '  Готово. Перезапусти терминал если fcon не найдена.' -ForegroundColor DarkGray
        return 0
    } finally {
        $ProgressPreference = $prevPref
        try { Write-Progress -Activity 'fcon update' -Completed } catch { }
    }
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
        '^(?i)add$'           {
            $addArgs = @()
            if ($Via) { $addArgs += $Via }
            if ($Extra) { $addArgs += $Extra }
            exit (Invoke-Add $addArgs)
        }
        '^(?i)sync$'          {
            $syncArgs = @()
            if ($Via) { $syncArgs += $Via }
            if ($Extra) { $syncArgs += $Extra }
            exit (Invoke-Sync $syncArgs)
        }
        '^(?i)import$'        { exit (Invoke-Import) }
        '^(?i)setup$'         {
            $setupArgs = @()
            if ($Via) { $setupArgs += $Via }
            if ($Extra) { $setupArgs += $Extra }
            exit (Invoke-Setup $setupArgs)
        }
        '^(?i)update$'        {
            $updArgs = @()
            if ($Via) { $updArgs += $Via }
            if ($Extra) { $updArgs += $Extra }
            exit (Invoke-Update $updArgs)
        }
        '^(?i)edit$'          { exit (Invoke-Edit) }
        '^(?i)path$'          { Write-Host (Get-ListPath); exit 0 }
    }
    exit (Invoke-Connect $Name $Via)
} catch {
    Write-Host ''
    Write-Fail $_.Exception.Message
    exit 1
}
