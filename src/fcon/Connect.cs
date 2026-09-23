// Connecting: RDP through mstsc with a two-line-owned .rdp file, SSH through
// the local OpenSSH client. stdio is always inherited, never redirected: a
// redirected ssh detaches the remote session instead of attaching it.
using System.Text;

namespace FleetConnect;

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

    public static int InvokeRdp(PcModel pc)
    {
        string target = pc.Address;
        if (pc.Port != "")
            target = pc.Address + ":" + pc.Port;

        // No user name to carry means no file to carry it in.
        if (pc.User == "")
        {
            Render.Note("mstsc /v:" + target);
            return Util.RunDetached("mstsc.exe", "/v:" + target) < 0
                ? ExitCodes.Error
                : ExitCodes.Ok;
        }

        string dir = Path.Combine(CsvStore.GetListDirectory(), "rdp");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, SafeFileName(pc.Name) + ".rdp");
        SetRdpSetting(file, "full address:s:", target);
        SetRdpSetting(file, "username:s:", pc.User);
        Render.Note("mstsc " + file + "   (" + pc.User + " at " + target + ")");
        return Util.RunDetached("mstsc.exe", "\"" + file + "\"") < 0
            ? ExitCodes.Error
            : ExitCodes.Ok;
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

        var args = new List<string>();
        if (pc.SshAlias != "")
        {
            // An alias is used whole: its ~/.ssh/config entry already carries
            // the user, the port and the key, and repeating any of them here
            // could only contradict it.
            args.Add(pc.SshAlias);
        }
        else
        {
            args.Add(pc.User != "" ? pc.User + "@" + pc.Address : pc.Address);
            if (pc.Port != "")
            {
                args.Add("-p");
                args.Add(pc.Port);
            }
        }
        Render.Note("ssh " + string.Join(" ", args));
        int code = Util.RunInherited(ssh, QuoteAll(args));
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

    // Only this machine's ~/.ssh/config knows its 'Host <alias>' blocks:
    // pushing pcs.csv does not carry them. Answers the alias names found
    // there, empty when there is no config.
    public static List<string> GetLocalSshAliases()
    {
        var names = new List<string>();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
            return names;
        string config = Path.Combine(profile, ".ssh", "config");
        if (!File.Exists(config))
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
