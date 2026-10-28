// Connecting: RDP through mstsc with a two-line-owned .rdp file, SSH through
// the local OpenSSH client. stdio is always inherited, never redirected: a
// redirected ssh detaches the remote session instead of attaching it.
using System.Text;

namespace FleetConnect;

sealed class SshAliasInfo
{
    public string Alias = "";
    public string HostName = "";
    public string User = "";
    public string Port = "";
}

static class Connect
{
    public static string SafeFileName(string text)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in text)
            sb.Append(bad.Contains(c) ? '_' : c);
        string safe = sb.ToString();
        return string.IsNullOrWhiteSpace(safe) ? "pc" : safe;
    }

    // Rewrites only the two lines this tool owns and leaves the rest of the
    // .rdp alone, so a window size or a drive redirection the operator set
    // inside mstsc survives the next connect.
    public static void SetRdpSetting(string path, string key, string value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path, Encoding.Unicode).ToList()
                                      : new List<string>();
        var result = new List<string>();
        bool written = false;
        foreach (string line in lines)
        {
            if (line.StartsWith(key, StringComparison.Ordinal))
            {
                if (!written)
                {
                    result.Add(key + value);
                    written = true;
                }
            }
            else
            {
                result.Add(line);
            }
        }
        if (!written)
            result.Add(key + value);
        File.WriteAllLines(path, result, Encoding.Unicode);
    }

    public static void PrepareRdp(PcModel pc, out string exe, out string args,
                                  out string summary, out string rdpFile)
    {
        exe = "mstsc.exe";
        string target = pc.Address;
        if (pc.Port != "")
            target = pc.Address + ":" + pc.Port;

        // No user name to carry means no file to carry it in.
        if (pc.User == "")
        {
            rdpFile = null;
            args = "/v:" + target;
            summary = "mstsc /v:" + target;
            return;
        }

        string dir = Path.Combine(CsvStore.GetListDirectory(), "rdp");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, SafeFileName(pc.Name) + ".rdp");
        SetRdpSetting(file, "full address:s:", target);
        SetRdpSetting(file, "username:s:", pc.User);
        rdpFile = file;
        args = "\"" + file + "\"";
        summary = "mstsc " + file + "   (" + pc.User + " at " + target + ")";
    }

    public static int InvokeRdp(PcModel pc)
    {
        PrepareRdp(pc, out string exe, out string args, out string summary, out _);
        Render.Note(summary);
        return Util.RunDetached(exe, args) < 0
            ? ExitCodes.Error
            : ExitCodes.Ok;
    }

    public static bool PrepareSsh(PcModel pc, out string exe, out string args,
                                  out string summary, out string error)
    {
        exe = Util.FindExe("ssh.exe");
        args = "";
        summary = "";
        error = null;
        if (exe == null)
        {
            error = "No ssh.exe on PATH. Install the OpenSSH client.";
            return false;
        }
        if (pc.SshAlias == "" && pc.Address == "")
        {
            error = pc.Name + " has neither a host nor an ssh alias.";
            return false;
        }

        var argList = new List<string>();
        if (pc.SshAlias != "")
        {
            // An alias is used whole: its ~/.ssh/config entry already carries
            // the user, the port and the key, and repeating any of them here
            // could only contradict it.
            argList.Add(pc.SshAlias);
        }
        else
        {
            argList.Add(pc.User != "" ? pc.User + "@" + pc.Address : pc.Address);
            if (pc.Port != "")
            {
                argList.Add("-p");
                argList.Add(pc.Port);
            }
        }
        args = QuoteAll(argList);
        summary = "ssh " + string.Join(" ", argList);
        return true;
    }

    public static int InvokeSsh(PcModel pc)
    {
        string ssh = Util.FindExe("ssh.exe");
        if (ssh == null)
        {
            Render.Fail("No ssh.exe on PATH. Install the OpenSSH client:");
            Render.Fail("  Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0");
            return ExitCodes.Error;
        }

        if (!PrepareSsh(pc, out string exe, out string args, out string summary, out string error))
        {
            Render.Fail(error);
            return ExitCodes.Error;
        }
        Render.Note(summary);
        int code = Util.RunInherited(exe, args);
        return code < 0 ? ExitCodes.Error : code;
    }

    public static int ConnectPc(PcModel pc, PcProtocol protocol)
    {
        if (pc.Address == "" && pc.SshAlias == "")
        {
            Render.Fail(pc.Name + " has neither a host nor an ssh alias.");
            return ExitCodes.Error;
        }
        if (protocol == PcProtocol.Ssh)
            return InvokeSsh(pc);
        return InvokeRdp(pc);
    }

    static string ResolveSshConfigPath()
    {
        // $env:USERPROFILE first, like the PS version: the shell folder API
        // ignores an overridden variable, and on Linux an unset variable
        // means "no config" rather than "$HOME/.ssh/config".
        string profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(profile))
        {
            if (OperatingSystem.IsWindows())
                return null;
            profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        if (string.IsNullOrEmpty(profile))
            return null;
        string config = Path.Combine(profile, ".ssh", "config");
        return File.Exists(config) ? config : null;
    }

    // Only this machine's ~/.ssh/config knows its 'Host <alias>' blocks:
    // pushing pcs.csv does not carry them. Answers the alias names found
    // there, empty when there is no config.
    public static List<string> GetLocalSshAliases()
    {
        var names = new List<string>();
        string config = ResolveSshConfigPath();
        if (config == null)
            return names;
        foreach (string line in File.ReadAllLines(config))
        {
            string t = line.Trim();
            // "Host" must stand alone: "HostName ..." lines start with the
            // same four letters but are key/value pairs, not Host blocks.
            if (!t.StartsWith("Host", StringComparison.OrdinalIgnoreCase) ||
                t.Length <= 4 || !char.IsWhiteSpace(t[4]))
                continue;
            string rest = t.Substring(4).Trim();
            if (rest == "")
                continue;
            foreach (string h in rest.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (h != "*" && !names.Contains(h))
                    names.Add(h);
            }
        }
        return names;
    }

    public static Dictionary<string, SshAliasInfo> GetLocalSshConfigMap()
    {
        var map = new Dictionary<string, SshAliasInfo>(StringComparer.OrdinalIgnoreCase);
        string config = ResolveSshConfigPath();
        if (config == null)
            return map;

        var currentBlocks = new List<SshAliasInfo>();
        foreach (string rawLine in File.ReadAllLines(config))
        {
            string line = rawLine;
            int hash = line.IndexOf('#');
            if (hash >= 0)
                line = line.Substring(0, hash);
            string t = line.Trim();
            if (t == "")
                continue;

            if (t.StartsWith("Host", StringComparison.OrdinalIgnoreCase) &&
                t.Length > 4 && (char.IsWhiteSpace(t[4]) || t[4] == '='))
            {
                currentBlocks.Clear();
                string rest = t.Substring(4).TrimStart();
                if (rest.StartsWith("=", StringComparison.Ordinal))
                    rest = rest.Substring(1).TrimStart();
                foreach (string h in rest.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (h == "*" || h.IndexOf('*') >= 0 || h.IndexOf('?') >= 0)
                        continue;
                    if (!map.TryGetValue(h, out SshAliasInfo info))
                    {
                        info = new SshAliasInfo { Alias = h };
                        map[h] = info;
                    }
                    currentBlocks.Add(info);
                }
                continue;
            }

            if (currentBlocks.Count == 0)
                continue;

            if (!TrySplitSshOption(t, out string key, out string val))
                continue;

            foreach (var info in currentBlocks)
            {
                if (key.Equals("HostName", StringComparison.OrdinalIgnoreCase) && info.HostName == "")
                    info.HostName = val;
                else if (key.Equals("User", StringComparison.OrdinalIgnoreCase) && info.User == "")
                    info.User = val;
                else if (key.Equals("Port", StringComparison.OrdinalIgnoreCase) && info.Port == "")
                    info.Port = val;
            }
        }
        return map;
    }

    static bool TrySplitSshOption(string trimmed, out string key, out string val)
    {
        key = "";
        val = "";
        int i = 0;
        while (i < trimmed.Length && !char.IsWhiteSpace(trimmed[i]) && trimmed[i] != '=')
            i++;
        if (i == 0 || i >= trimmed.Length)
            return false;
        key = trimmed.Substring(0, i);
        string rest = trimmed.Substring(i).TrimStart();
        if (rest.StartsWith("=", StringComparison.Ordinal))
            rest = rest.Substring(1).TrimStart();
        if (rest.Length >= 2 && rest[0] == '"' && rest[rest.Length - 1] == '"')
            rest = rest.Substring(1, rest.Length - 2);
        val = rest.Trim();
        return val != "";
    }

    static string QuoteAll(List<string> args)
    {
        var parts = new List<string>();
        foreach (string a in args)
            parts.Add(a.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0
                ? "\"" + a.Replace("\"", "\\\"") + "\""
                : a);
        return string.Join(" ", parts);
    }
}
